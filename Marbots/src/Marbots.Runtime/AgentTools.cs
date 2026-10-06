using System.Text;
using System.Text.Json;
using Marbots.Abstractions;
using Marbots.Kernel;
using Microsoft.Extensions.DependencyInjection;

namespace Marbots.Runtime;

public sealed class ListBotsFunction : KernelFunctionBase
{
    public override FunctionDescriptor Descriptor { get; } = new(
        "list_bots", "List the bots on your team with their roles, skills, tools and current status.",
        """{"type":"object","properties":{}}""", "agents", PermissionCategory.ReadOnly, RiskLevel.Low);

    protected override async ValueTask<FunctionResult> ExecuteAsync(FunctionCall call, FunctionExecutionContext ctx, CancellationToken ct)
    {
        var bots = await ctx.Services.GetRequiredService<BotRegistry>().ActiveAsync(ct);
        var sb = new StringBuilder();
        foreach (var b in bots.Where(b => b.Id != ctx.Bot.Id))
        {
            sb.Append("- id: ").Append(b.Id).Append(" | ").Append(b.Name).Append(" — ").Append(b.Role).Append(" | status: ").Append(b.Status).AppendLine();
            sb.Append("  ").AppendLine(b.Description);
            sb.Append("  skills: ").Append(b.Skills.Count == 0 ? "none" : string.Join(", ", b.Skills))
              .Append(" | tools: ").Append(string.Join(", ", b.KernelFunctions))
              .Append(" | mcp: ").Append(b.McpServers.Count == 0 ? "none" : string.Join(", ", b.McpServers))
              .Append(" | permissions: ").Append(b.PermissionProfile)
              .Append(" | model: ").AppendLine(ModelRouter.IsDefaultSetting(b.ModelProfile) ? "default" : b.ModelProfile);
        }
        return FunctionResult.Ok(sb.Length == 0 ? "You have no teammates yet. Use create_bot." : sb.ToString());
    }
}

public sealed class DelegateTasksFunction : KernelFunctionBase
{
    public override FunctionDescriptor Descriptor { get; } = new(
        "delegate_tasks",
        "Hand sub-tasks to teammates and wait for their results. Tasks without dependencies run in parallel. Each objective must be self-contained (the teammate cannot see this chat): include goal, inputs, file names in the shared workspace, and the definition of done.",
        """
        {"type":"object","properties":{"tasks":{"type":"array","items":{"type":"object","properties":{
          "key":{"type":"string","description":"Short unique key, e.g. research"},
          "bot":{"type":"string","description":"Teammate id or name"},
          "objective":{"type":"string","description":"Self-contained instructions"},
          "depends_on":{"type":"array","items":{"type":"string"},"description":"Keys that must finish first; their results are appended to the objective"}},
          "required":["key","bot","objective"]}}},"required":["tasks"]}
        """,
        "agents", PermissionCategory.AgentControl, RiskLevel.Low, 3600);

    protected override async ValueTask<FunctionResult> ExecuteAsync(FunctionCall call, FunctionExecutionContext ctx, CancellationToken ct)
    {
        var specs = new List<DelegationSpec>();
        if (!call.Arguments.TryGetProperty("tasks", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return FunctionResult.Fail("'tasks' array is required.");
        var i = 0;
        foreach (var t in arr.EnumerateArray())
        {
            i++;
            string? Str(string n) => t.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var deps = t.TryGetProperty("depends_on", out var d) && d.ValueKind == JsonValueKind.Array
                ? d.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList() : [];
            specs.Add(new DelegationSpec(Str("key") ?? $"task{i}", Str("bot") ?? "", Str("objective") ?? "", deps));
        }
        var engine = ctx.Services.GetRequiredService<MarbotsEngine>();
        var parent = await engine.GetTaskAsync(ctx.TaskId, ct) ?? throw new InvalidOperationException("Parent task not found.");
        try
        {
            var outcomes = await engine.DelegateAsync(parent, ctx.Bot, specs, ct);
            var sb = new StringBuilder();
            foreach (var o in outcomes)
            {
                sb.Append("### ").Append(o.Key).Append(" — ").Append(o.BotName).Append(" [").Append(o.State).Append("] (task ").Append(o.TaskId).AppendLine(")");
                sb.AppendLine(o.Output.Length > 8000 ? o.Output[..8000] + "…" : o.Output).AppendLine();
            }
            return new FunctionResult(outcomes.All(o => o.State == TaskState.Completed), sb.ToString());
        }
        catch (BotValidationException ex)
        {
            return FunctionResult.Fail(ex.Message);
        }
    }
}

public sealed class ListTemplatesFunction : KernelFunctionBase
{
    public override FunctionDescriptor Descriptor { get; } = new(
        "list_templates", "Search the Bot Template Gallery for a suitable role before creating a new bot.",
        Schema(("query", "string", "Keywords, e.g. 'designer' or 'finance'", false), ("category", "string", "Optional category filter", false)),
        "management", PermissionCategory.ReadOnly, RiskLevel.Low);

    protected override async ValueTask<FunctionResult> ExecuteAsync(FunctionCall call, FunctionExecutionContext ctx, CancellationToken ct)
    {
        var list = await ctx.Services.GetRequiredService<TemplateService>().ListAsync(call.GetString("query"), call.GetString("category"), ct);
        if (list.Count == 0) return FunctionResult.Ok("No templates matched. Try a broader query.");
        var sb = new StringBuilder();
        foreach (var t in list.Take(25))
            sb.Append("- ").Append(t.Id).Append(" | ").Append(t.Name).Append(" (").Append(t.Category).Append(") — ").AppendLine(t.Description);
        return FunctionResult.Ok(sb.ToString());
    }
}

public sealed class CreateBotFunction : KernelFunctionBase
{
    public override FunctionDescriptor Descriptor { get; } = new(
        "create_bot", "Create a new teammate bot, optionally from a template. Returns the new bot id.",
        """
        {"type":"object","properties":{
          "name":{"type":"string"},
          "template_id":{"type":"string","description":"Optional template id from list_templates"},
          "role":{"type":"string"},
          "persona":{"type":"string","description":"Instructions / system prompt"},
          "skills":{"type":"array","items":{"type":"string"}},
          "kernel_functions":{"type":"array","items":{"type":"string","enum":["files","search","shell","web","memory","todo","agents"]}},
          "mcp_servers":{"type":"array","items":{"type":"string"}},
          "permission_profile":{"type":"string","enum":["read-only","workspace-write","developer-safe","autonomous"]},
          "auto_learn":{"type":"string","enum":["Off","MemoryOnly","SuggestSkills"]},
          "model":{"type":"string","description":"Optional model: 'default' (workspace default), a profile name, or 'provider/model'. Omit unless the user asked for a specific model."}},
          "required":["name"]}
        """,
        "management", PermissionCategory.AgentControl, RiskLevel.Medium);

    protected override async ValueTask<FunctionResult> ExecuteAsync(FunctionCall call, FunctionExecutionContext ctx, CancellationToken ct)
    {
        var registry = ctx.Services.GetRequiredService<BotRegistry>();
        var templates = ctx.Services.GetRequiredService<TemplateService>();
        BotDefinition bot;
        if (call.GetString("template_id") is { Length: > 0 } tid)
        {
            var t = await templates.GetAsync(tid, ct);
            if (t is null) return FunctionResult.Fail($"Template '{tid}' not found.");
            bot = BotRegistry.FromTemplate(t, call.GetString("name"));
        }
        else bot = new BotDefinition { Name = call.Require("name"), KernelFunctions = ["files", "search", "web", "memory", "todo"] };

        if (call.GetString("role") is { Length: > 0 } role) bot.Role = role;
        if (call.GetString("persona") is { Length: > 0 } persona) bot.Persona = persona;
        List<string>? Arr(string n) => call.Arguments.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList() : null;
        if (Arr("skills") is { } skills) bot.Skills = skills;
        if (Arr("kernel_functions") is { } kf) bot.KernelFunctions = kf.Where(k => k != "management").ToList();
        if (Arr("mcp_servers") is { } mcp) bot.McpServers = mcp;
        if (call.GetString("permission_profile") is { Length: > 0 } pp) bot.PermissionProfile = pp;
        if (Enum.TryParse<AutoLearnMode>(call.GetString("auto_learn"), true, out var al)) bot.AutoLearn = al;
        string? modelWarning = null;
        if (call.GetString("model") is { Length: > 0 } model)
        {
            var resolved = ctx.Services.GetRequiredService<ModelRouter>().Resolve(model);
            if (resolved.Fallback && !ModelRouter.IsDefaultSetting(model)) modelWarning = resolved.Warning;
            else bot.ModelProfile = model;
        }
        if (bot.Description.Length == 0) bot.Description = $"Created by {ctx.Bot.Name} on request.";

        // Creating a bot with broader privileges than the creator's own profile needs a human.
        if (bot.PermissionProfile is "autonomous")
        {
            var approvals = ctx.Services.GetRequiredService<ApprovalService>();
            var approval = await approvals.RequestAsync(new ApprovalRequest
            {
                TaskId = ctx.TaskId, ThreadId = ctx.ThreadId, BotId = ctx.Bot.Id, ToolName = "create_bot",
                Arguments = call.Arguments.GetRawText(), Category = PermissionCategory.Admin, Risk = RiskLevel.High,
                Reason = $"Create bot '{bot.Name}' with the autonomous permission profile (shell without approval).",
            }, ct);
            if (approval.State != ApprovalState.Approved) return FunctionResult.Fail("The user did not approve creating an autonomous bot.");
        }
        try
        {
            bot = await registry.CreateAsync(bot, ct);
        }
        catch (BotValidationException ex) { return FunctionResult.Fail(ex.Message); }
        var effective = ctx.Services.GetRequiredService<ModelRouter>().Resolve(bot.ModelProfile).Label;
        return FunctionResult.Ok($"Created bot '{bot.Name}' with id '{bot.Id}' (role: {bot.Role}, model: {effective}{(ModelRouter.IsDefaultSetting(bot.ModelProfile) ? " (workspace default)" : "")}, tools: {string.Join(", ", bot.KernelFunctions)}, permissions: {bot.PermissionProfile})." + (modelWarning is null ? "" : $" Note: {modelWarning}"));
    }
}

public sealed class ScheduleTaskFunction : KernelFunctionBase
{
    public override FunctionDescriptor Descriptor { get; } = new(
        "schedule_task", "Schedule a prompt for a bot: recurring with a 5-field cron expression (minute hour day month weekday) or once at a specific time.",
        Schema(("name", "string", "Short schedule name", true),
               ("bot", "string", "Bot id or name that should run it", true),
               ("prompt", "string", "What the bot should do each time", true),
               ("cron", "string", "Cron expression, e.g. '0 8 * * 1' for Mondays 08:00", false),
               ("run_at", "string", "ISO-8601 date-time for a one-off run", false),
               ("time_zone", "string", "IANA/Windows time zone id (default UTC)", false)),
        "management", PermissionCategory.AgentControl, RiskLevel.Low);

    protected override async ValueTask<FunctionResult> ExecuteAsync(FunctionCall call, FunctionExecutionContext ctx, CancellationToken ct)
    {
        var scheduler = ctx.Services.GetRequiredService<SchedulerService>();
        var bot = await ctx.Services.GetRequiredService<BotRegistry>().ResolveAsync(call.Require("bot"), ct);
        if (bot is null) return FunctionResult.Fail("Bot not found.");
        var job = new ScheduleJob
        {
            Name = call.Require("name"), BotId = bot.Id, Prompt = call.Require("prompt"),
            Cron = call.GetString("cron") ?? "", TimeZone = call.GetString("time_zone") ?? "UTC",
            RunAt = DateTimeOffset.TryParse(call.GetString("run_at"), out var at) ? at : null,
        };
        try
        {
            job = await scheduler.SaveAsync(job, ct);
        }
        catch (ArgumentException ex) { return FunctionResult.Fail(ex.Message); }
        return FunctionResult.Ok($"Scheduled '{job.Name}' for {bot.Name}. Next run: {job.NextRunAt:u}.");
    }
}

public sealed class GetTaskFunction : KernelFunctionBase
{
    public override FunctionDescriptor Descriptor { get; } = new(
        "get_task", "Get the status and result of a task by id.",
        Schema(("task_id", "string", "Task id", true)), "management", PermissionCategory.ReadOnly, RiskLevel.Low);

    protected override async ValueTask<FunctionResult> ExecuteAsync(FunctionCall call, FunctionExecutionContext ctx, CancellationToken ct)
    {
        var t = await ctx.Services.GetRequiredService<MarbotsEngine>().GetTaskAsync(call.Require("task_id"), ct);
        return t is null ? FunctionResult.Fail("Task not found.")
            : FunctionResult.Ok($"{t.Id} [{t.State}] bot={t.BotId} steps={t.Steps}\nObjective: {AgentRuntime.Preview(t.Objective, 400)}\nResult: {t.Result ?? t.Error ?? "(none yet)"}");
    }
}
