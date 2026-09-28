using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DotCode.Abstractions;
using DotCode.Engine.Agent;
using DotCode.Engine.Extensibility;
using DotCode.Engine.Permissions;
using DotCode.Engine.Util;

namespace DotCode.Engine.Tools.Builtin;

public sealed class TodoWriteTool : Tool
{
    public override string Name => "TodoWrite";
    public override string Description => """
        Creates and manages a structured task list for the current session, shown to the user. Use it proactively for tasks with 3+ steps, when the user gives multiple tasks, or after receiving new instructions. Skip it for trivial single-step tasks.
        Rules:
        - Each todo has content (imperative, e.g. "Run tests"), activeForm (present continuous, e.g. "Running tests") and status: pending | in_progress | completed.
        - Exactly one task in_progress at a time. Mark tasks completed immediately after finishing (don't batch).
        - Only mark completed when fully done (tests pass, no errors). If blocked, keep it in_progress and add a task for the blocker.
        - Always send the complete list (it replaces the previous one).
        """;
    public override JsonElement InputSchema { get; } = Schema("""
        {"type":"object","properties":{"todos":{"type":"array","items":{"type":"object","properties":{
          "content":{"type":"string","minLength":1},
          "status":{"type":"string","enum":["pending","in_progress","completed"]},
          "activeForm":{"type":"string","minLength":1}},
          "required":["content","status","activeForm"]}}},
         "required":["todos"]}
        """);
    public override bool IsReadOnly(JsonElement input) => true;
    public override bool IsConcurrencySafe(JsonElement input) => false;
    public override PermissionTarget GetPermissionTarget(JsonElement input, AgentSession s) => new(PermissionKind.None);
    public override string DisplayName(JsonElement input, AgentSession s) => "Update Todos";

    public static List<TodoItem> Parse(JsonElement input)
    {
        var list = new List<TodoItem>();
        if (input.GetProp("todos") is not { ValueKind: JsonValueKind.Array } arr) return list;
        foreach (var t in arr.EnumerateArray())
        {
            var status = t.GetString("status") switch { "in_progress" => TodoStatus.InProgress, "completed" => TodoStatus.Completed, _ => TodoStatus.Pending };
            list.Add(new TodoItem(t.GetString("content") ?? "", status, t.GetString("activeForm")));
        }
        return list;
    }

    public override Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
    {
        var todos = Parse(input);
        var root = ctx.Session;
        while (root.Parent is not null) root = root.Parent;
        root.SetTodos(todos);
        return Task.FromResult(ToolResult.Ok(
            "Todos have been modified successfully. Ensure that you continue to use the todo list to track your progress. Please proceed with the current tasks if applicable",
            $"{todos.Count(t => t.Status == TodoStatus.Completed)}/{todos.Count} completed"));
    }
}

public sealed class AgentTool : Tool
{
    public override string Name => "Agent";
    public override string Description
    {
        get
        {
            var sb = new StringBuilder("""
                Launches a subagent to handle a complex, multi-step task autonomously in its own context window. It returns a single final report; its intermediate tool output does not enter your context.
                Use it for broad codebase exploration, open-ended searches, or independent subtasks. Launch several agents in parallel (multiple tool calls in one message) when tasks are independent.
                Write a complete, self-contained prompt: the subagent has none of your conversation context. Say exactly what it should return.
                Available agent types:

                """);
            foreach (var (name, def) in Registry?.Agents ?? [])
                sb.Append("- ").Append(name).Append(": ").Append(def.Description).Append(def.Tools is { } t ? $" (Tools: {string.Join(", ", t)})" : " (Tools: *)").Append('\n');
            return sb.ToString();
        }
    }
    internal ExtensionRegistry? Registry { get; set; }

    public override JsonElement InputSchema { get; } = Schema("""
        {"type":"object","properties":{
          "description":{"type":"string","description":"A short (3-5 word) description of the task"},
          "prompt":{"type":"string","description":"The task for the agent to perform"},
          "subagent_type":{"type":"string","description":"The type of specialized agent to use (default general-purpose)"},
          "model":{"type":"string","description":"Optional model override (role or provider:model)"},
          "isolation":{"type":"string","enum":["worktree"],"description":"Run the subagent in a fresh git worktree (isolated checkout on its own branch); unchanged worktrees are removed automatically"}},
         "required":["description","prompt"]}
        """);
    public override bool IsReadOnly(JsonElement input) => true;
    public override bool IsConcurrencySafe(JsonElement input) => true;
    public override int MaxResultChars => 60_000;
    public override string DisplayName(JsonElement input, AgentSession s) => $"{Str(input, "subagent_type", "general-purpose")}({Str(input, "description")})";
    public override PermissionTarget GetPermissionTarget(JsonElement input, AgentSession s) => new(PermissionKind.Agent, Str(input, "subagent_type", "general-purpose"));

    public override string? Validate(JsonElement input, AgentSession s)
    {
        var type = Str(input, "subagent_type", "general-purpose");
        return s.Runtime.Extensions.Agents.ContainsKey(type) ? null : $"Unknown subagent_type '{type}'. Available: {string.Join(", ", s.Runtime.Extensions.Agents.Keys)}";
    }

    public override async Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
    {
        var session = ctx.Session;
        var type = Str(input, "subagent_type", "general-purpose");
        var def = session.Runtime.Extensions.Agents[type];
        var modelRef = input.GetString("model") ?? def.Model ?? "subagent";
        ResolvedModel model;
        try { model = session.Runtime.Router.Resolve(modelRef, session.Model.Qualified); }
        catch (InvalidOperationException) { model = session.Model; }

        WorktreeInfo? worktree = null;
        if ((input.GetString("isolation") ?? def.Isolation) is "worktree")
        {
            try
            {
                worktree = Worktrees.Create(session.Cwd, $"agent-{type.Replace(':', '-')}-{Guid.NewGuid().ToString("n")[..6]}");
                session.Permissions.AddWorkingDirectory(worktree.Path);
            }
            catch (InvalidOperationException ex) { return ToolResult.Error($"Could not create a git worktree for the subagent: {ex.Message}"); }
        }

        await using var child = session.CreateSubagent(def, ctx.ToolUseId, model, worktree);
        var sw = Stopwatch.StartNew();
        session.Emit(new SubagentStartedEvent(ctx.ToolUseId, type, Str(input, "description"), model.Qualified));
        var toolUses = 0;
        var before = child.Sink;
        child.Sink = new DelegateEventSink(e =>
        {
            if (e is ToolStartedEvent) Interlocked.Increment(ref toolUses);
            before.Emit(e);
        });
        TurnResult result;
        string? worktreeNote = null;
        try
        {
            result = await child.RunTurnAsync(Str(input, "prompt"), null, ct).ConfigureAwait(false);
        }
        finally
        {
            if (worktree is not null) worktreeNote = FinishWorktree(worktree);
        }
        session.Emit(new SubagentCompletedEvent(ctx.ToolUseId, type, child.TotalUsage, sw.ElapsedMilliseconds, toolUses));
        if (result.IsError) return ToolResult.Error($"Subagent failed: {result.Error}{worktreeNote}");
        var text = (result.Text.Length > 0 ? result.Text : "(subagent returned no text)") + worktreeNote;
        return ToolResult.Ok(text, $"Done ({TextUtil.Plural(toolUses, "tool use")} · {TextUtil.FormatTokens(child.TotalUsage.TotalTokens)} tokens · {TextUtil.FormatDuration(sw.Elapsed)})", display: "");
    }

    /// <summary>Removes the subagent's worktree when nothing changed; otherwise keeps it and tells the caller where it is.</summary>
    private static string FinishWorktree(WorktreeInfo wt)
    {
        var (dirty, commits) = Worktrees.Changes(wt);
        if (!dirty && commits == 0)
        {
            Worktrees.Remove(wt.RepoRoot, wt.Name);
            return "\n\n[The subagent's worktree made no changes and was removed.]";
        }
        var what = string.Join(", ", new[] { commits > 0 ? TextUtil.Plural(commits, "commit") : null, dirty ? "uncommitted changes" : null }.Where(x => x is not null));
        return $"\n\n[Worktree kept: {wt.Path} on branch {wt.Branch} ({what}). Review with `git diff {wt.BaseCommit[..Math.Min(12, wt.BaseCommit.Length)]}...{wt.Branch}` (commit pending changes in the worktree first), merge with `git merge {wt.Branch}`, and remove it with `dotcode worktree remove {wt.Name}`.]";
    }
}

public sealed class SkillTool : Tool
{
    public override string Name => "Skill";
    internal ExtensionRegistry? Registry { get; set; }

    public override string Description
    {
        get
        {
            var sb = new StringBuilder("""
                Loads a skill: a packaged set of instructions (and optional scripts/resources in the skill's folder) for a specific kind of task. When a task matches a skill below, invoke it FIRST and follow its instructions.
                Pass the skill name exactly as listed; optional args are appended.
                Available skills:

                """);
            var any = false;
            foreach (var (name, skill) in Registry?.Skills ?? [])
            {
                any = true;
                sb.Append("- ").Append(name).Append(": ").Append(skill.Description).Append('\n');
            }
            if (!any) sb.Append("(no skills installed)\n");
            return sb.ToString();
        }
    }
    public override JsonElement InputSchema { get; } = Schema("""
        {"type":"object","properties":{
          "skill":{"type":"string","description":"Skill name, e.g. \"pdf\" or \"plugin:skill\""},
          "args":{"type":"string","description":"Optional arguments"}},
         "required":["skill"]}
        """);
    public override bool IsReadOnly(JsonElement input) => true;
    public override bool IsConcurrencySafe(JsonElement input) => false;
    public override bool IsEnabled(AgentSession session) => session.Runtime.Extensions.Skills.Count > 0;
    public override string DisplayName(JsonElement input, AgentSession s) => $"Skill({Str(input, "skill")})";
    public override PermissionTarget GetPermissionTarget(JsonElement input, AgentSession s) => new(PermissionKind.Skill, Str(input, "skill"));

    public override Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
    {
        var name = Str(input, "skill").TrimStart('/');
        var skills = ctx.Session.Runtime.Extensions.Skills;
        if (!skills.TryGetValue(name, out var skill))
            skill = skills.Values.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (skill is null) return Task.FromResult(ToolResult.Error($"Unknown skill: {name}. Available: {string.Join(", ", skills.Keys)}"));
        return Task.FromResult(ToolResult.Ok(Render(skill, input.GetString("args")), $"Loaded skill {skill.QualifiedName}", display: ""));
    }

    public static string Render(SkillDefinition skill, string? args)
    {
        var body = skill.Body.Replace("${CLAUDE_PLUGIN_ROOT}", skill.BaseDir).Replace("${DOTCODE_PLUGIN_ROOT}", skill.BaseDir)
            .Replace("{baseDir}", skill.BaseDir);
        var sb = new StringBuilder();
        sb.Append("Launching skill: ").Append(skill.QualifiedName).Append('\n');
        sb.Append("Base directory for this skill: ").Append(skill.BaseDir).Append("\n\n");
        sb.Append(body.Trim());
        if (args is { Length: > 0 }) sb.Append("\n\nARGUMENTS: ").Append(args);
        return sb.ToString();
    }
}

public sealed class AskUserQuestionTool : Tool
{
    public override string Name => "AskUserQuestion";
    public override string Description => """
        Asks the user 1-4 multiple-choice questions when you are blocked on a decision only they can make (preferences, ambiguous requirements, choosing between approaches). The user can always type a custom answer.
        Do not use it for things you can determine yourself or that have a sensible default. Put a recommended option first and suffix it with "(Recommended)".
        """;
    public override JsonElement InputSchema { get; } = Schema("""
        {"type":"object","properties":{"questions":{"type":"array","minItems":1,"maxItems":4,"items":{"type":"object","properties":{
          "question":{"type":"string"},
          "header":{"type":"string","description":"Very short label (max 12 chars)"},
          "multiSelect":{"type":"boolean"},
          "options":{"type":"array","minItems":2,"maxItems":4,"items":{"type":"object","properties":{
             "label":{"type":"string"},"description":{"type":"string"}},"required":["label"]}}},
          "required":["question","header","options"]}}},
         "required":["questions"]}
        """);
    public override bool IsReadOnly(JsonElement input) => true;
    public override bool IsConcurrencySafe(JsonElement input) => false;
    public override PermissionTarget GetPermissionTarget(JsonElement input, AgentSession s) => new(PermissionKind.None);
    public override string DisplayName(JsonElement input, AgentSession s) => "AskUserQuestion";

    public override async Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
    {
        var questions = new List<UserQuestion>();
        if (input.GetProp("questions") is { ValueKind: JsonValueKind.Array } arr)
            foreach (var q in arr.EnumerateArray())
            {
                var options = q.GetProp("options") is { ValueKind: JsonValueKind.Array } opts
                    ? opts.EnumerateArray().Select(o => new QuestionOption(o.GetString("label") ?? "", o.GetString("description"))).ToList()
                    : [];
                questions.Add(new UserQuestion(q.GetString("question") ?? "", q.GetString("header") ?? "", options, q.GetBool("multiSelect") == true));
            }
        var answers = await ctx.Session.Interaction.AskQuestionsAsync(questions, ct).ConfigureAwait(false);
        if (answers is null) return ToolResult.Error("The user is not available to answer questions in this mode. Proceed with your best judgment and state your assumptions.");
        var sb = new StringBuilder("User has answered your questions: ");
        sb.Append(string.Join(", ", answers.Select(a => $"\"{a.Question}\"=\"{a.Answer}\"")));
        sb.Append(". You can now continue with the user's answers in mind.");
        return ToolResult.Ok(sb.ToString(), string.Join(" · ", answers.Select(a => a.Answer)));
    }
}

public sealed class ExitPlanModeTool : Tool
{
    public override string Name => "ExitPlanMode";
    public override string Description => "Use in plan mode when you have finished planning an implementation task and are ready to code. Pass the plan (markdown, concise). The user reviews it: if approved, plan mode ends and you proceed; if not, revise based on their feedback. Only use it for tasks that require writing code, not for research.";
    public override JsonElement InputSchema { get; } = Schema("""{"type":"object","properties":{"plan":{"type":"string","description":"The implementation plan in markdown"}},"required":["plan"]}""");
    public override bool IsReadOnly(JsonElement input) => true;
    public override bool IsConcurrencySafe(JsonElement input) => false;
    public override bool IsEnabled(AgentSession session) => session.Mode == PermissionMode.Plan;
    public override PermissionTarget GetPermissionTarget(JsonElement input, AgentSession s) => new(PermissionKind.None);
    public override string DisplayName(JsonElement input, AgentSession s) => "Plan";

    public override async Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
    {
        var plan = Str(input, "plan");
        var decision = await ctx.Session.Interaction.ReviewPlanAsync(plan, ct).ConfigureAwait(false);
        switch (decision.Approval)
        {
            case PlanApproval.Approve:
                ctx.Session.SetMode(PermissionMode.Default);
                return ToolResult.Ok("User has approved your plan. You can now start coding. Start with updating your todo list if applicable.", "User approved the plan", display: plan);
            case PlanApproval.ApproveAcceptEdits:
                ctx.Session.SetMode(PermissionMode.AcceptEdits);
                return ToolResult.Ok("User has approved your plan and enabled auto-accept for edits. You can now start coding. Start with updating your todo list if applicable.", "User approved the plan (auto-accept edits)", display: plan);
            default:
                return ToolResult.Error($"The user rejected the plan and wants to keep planning.{(decision.Feedback is { Length: > 0 } f ? $" Feedback: {f}" : "")}");
        }
    }
}
