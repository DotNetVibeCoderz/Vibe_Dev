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

        // Suggest mode: the user approves the plan before anyone starts working.
        var approvals = ctx.Services.GetRequiredService<ApprovalService>();
        if ((await approvals.GetSettingsAsync(ct)).Delegation == DelegationMode.Suggest)
        {
            var plan = new StringBuilder("**Proposed plan**\n\n");
            foreach (var s in specs)
            {
                plan.Append("- **").Append(s.Key).Append("** → `").Append(s.Bot).Append('`');
                if (s.DependsOn.Count > 0) plan.Append(" (after ").Append(string.Join(", ", s.DependsOn)).Append(')');
                plan.Append(": ").AppendLine(s.Objective.Length > 300 ? s.Objective[..300] + "…" : s.Objective);
            }
            var decision = await approvals.RequestAsync(new ApprovalRequest
            {
                TaskId = ctx.TaskId, ThreadId = ctx.ThreadId, BotId = ctx.Bot.Id, ToolName = "delegate_tasks",
                Arguments = call.Arguments.GetRawText(), Category = PermissionCategory.AgentControl, Risk = RiskLevel.Low,
                Reason = $"Suggest mode: {ctx.Bot.Name} proposes {specs.Count} sub-task(s). Approve to start them.",
                Summary = plan.ToString(),
            }, ct);
            if (decision.State != ApprovalState.Approved)
                return FunctionResult.Fail("The user did not approve this delegation plan. Ask what to change, or do the work yourself.");
        }
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
          "kernel_functions":{"type":"array","items":{"type":"string","enum":["files","search","shell","web","memory","todo","agents","desktop","subagents"]}},
          "mcp_servers":{"type":"array","items":{"type":"string"}},
          "permission_profile":{"type":"string","enum":["read-only","workspace-write","developer-safe","autonomous"]},
          "auto_learn":{"type":"string","enum":["Off","MemoryOnly","SuggestSkills"]},
          "model":{"type":"string","description":"Optional model: 'default' (workspace default), a profile name, or 'provider/model'. Omit unless the user asked for a specific model."},
          "host":{"type":"string","description":"Optional: which computer runs the bot's files/shell/desktop tools: 'local-default', a host id or name from list_hosts, or 'auto'. Omit to use this computer."},
          "container_image":{"type":"string","description":"Optional Docker image; the bot's shell commands then run in that container (e.g. python:3.12-slim)."}},
          "required":["name"]}
        """,
        "management", PermissionCategory.AgentControl, RiskLevel.Medium, 3600); // may wait for a human approval

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
        if (call.GetString("host") is { Length: > 0 } hostArg)
        {
            var hostIds = ctx.Services.GetRequiredService<HostRegistry>();
            var match = hostArg is WellKnown.LocalHostId or WellKnown.AutoHost ? hostArg
                : (await hostIds.ListAsync(ct)).FirstOrDefault(h => h.Id.Equals(hostArg, StringComparison.OrdinalIgnoreCase) || h.Name.Equals(hostArg, StringComparison.OrdinalIgnoreCase))?.Id;
            if (match is null) return FunctionResult.Fail($"Unknown host '{hostArg}'. Use list_hosts.");
            bot.HostRef = match;
        }
        if (call.GetString("container_image") is { Length: > 0 } image) bot.Container = new ContainerProfile { Image = image };
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

/// <summary>Boss Man: which computers can run bots' tools (this one plus enrolled agent hosts).</summary>
public sealed class ListHostsFunction : KernelFunctionBase
{
    public override FunctionDescriptor Descriptor { get; } = new(
        "list_hosts", "List the computers bots can run their tools on (status, OS, capabilities such as desktop, docker, dotnet, node, python).",
        "{\"type\":\"object\",\"properties\":{}}", "management", PermissionCategory.ReadOnly, RiskLevel.Low);

    protected override async ValueTask<FunctionResult> ExecuteAsync(FunctionCall call, FunctionExecutionContext ctx, CancellationToken ct)
    {
        var hosts = await ctx.Services.GetRequiredService<HostService>().ListAsync(ct);
        return FunctionResult.Ok(string.Join('\n', hosts.Select(h => $"- {h.Id} \"{h.Name}\" {h.Status}; {h.Os}; {string.Join(", ", h.Capabilities)}")));
    }
}

/// <summary>
/// Optional pack "subagents": split independent work across temporary copies of this bot that run in parallel.
/// </summary>
public sealed class SpawnSubagentsFunction : KernelFunctionBase
{
    public override FunctionDescriptor Descriptor { get; } = new(
        "spawn_subagents",
        "Split independent parts of your task across temporary copies of yourself that work in parallel (same persona, skills, model, computer and tools; shared workspace). Use it when the parts do not depend on each other, e.g. researching several topics, writing several files or testing several pages. Each objective must be self-contained. You get every sub-agent's report back.",
        """
        {"type":"object","properties":{"tasks":{"type":"array","maxItems":6,"items":{"type":"object","properties":{
          "key":{"type":"string","description":"Short unique key, e.g. part-1"},
          "objective":{"type":"string","description":"Self-contained instructions for this part, incl. file names to write"}},
          "required":["key","objective"]}}},"required":["tasks"]}
        """,
        Packs.Subagents, PermissionCategory.AgentControl, RiskLevel.Low, 3600);

    protected override async ValueTask<FunctionResult> ExecuteAsync(FunctionCall call, FunctionExecutionContext ctx, CancellationToken ct)
    {
        if (!call.Arguments.TryGetProperty("tasks", out var arr) || arr.ValueKind != JsonValueKind.Array) return FunctionResult.Fail("tasks is required.");
        var work = arr.EnumerateArray()
            .Select((t, i) => (Key: t.TryGetProperty("key", out var k) && k.GetString() is { Length: > 0 } key ? key : $"part-{i + 1}",
                               Objective: t.TryGetProperty("objective", out var o) ? o.GetString() ?? "" : ""))
            .Where(w => w.Objective.Length > 0).ToList();
        var engine = ctx.Services.GetRequiredService<MarbotsEngine>();
        var parent = await engine.GetTaskAsync(ctx.TaskId, ct) ?? throw new InvalidOperationException("Unknown task.");
        try
        {
            var outcomes = await engine.SpawnSubagentsAsync(parent, ctx.Bot, work, ct);
            var sb = new System.Text.StringBuilder();
            foreach (var o in outcomes)
                sb.Append("## ").Append(o.Key).Append(" (").Append(o.State).AppendLine(")").AppendLine(o.Output).AppendLine();
            return new FunctionResult(outcomes.All(o => o.State == TaskState.Completed), sb.ToString().Trim());
        }
        catch (BotValidationException ex) { return FunctionResult.Fail(ex.Message); }
    }
}

/// <summary>Boss Man: the curated MCP gallery (installed or not), with what each server still needs.</summary>
public sealed class ListMcpCatalogFunction : KernelFunctionBase
{
    public override FunctionDescriptor Descriptor { get; } = new(
        "list_mcp_catalog", "List the curated MCP server gallery: id, what it does, whether it is installed, and secrets it still needs. Only these can be installed with install_mcp.",
        """{"type":"object","properties":{"query":{"type":"string","description":"Optional filter, e.g. browser, github, docs"}}}""",
        "management", PermissionCategory.ReadOnly, RiskLevel.Low);

    protected override async ValueTask<FunctionResult> ExecuteAsync(FunctionCall call, FunctionExecutionContext ctx, CancellationToken ct)
    {
        var store = ctx.Services.GetRequiredService<IDocumentStore<McpServerConfig>>();
        var secrets = ctx.Services.GetRequiredService<ISecretProvider>();
        var q = call.GetString("query") ?? "";
        var sb = new System.Text.StringBuilder();
        foreach (var entry in McpCatalog.Entries.Where(e => q.Length == 0 || $"{e.Id} {e.Name} {e.Description}".Contains(q, StringComparison.OrdinalIgnoreCase)))
        {
            var current = await store.GetAsync(entry.Id, ct);
            var installed = current is { IsCatalogEntry: false, Enabled: true };
            sb.Append("- ").Append(entry.Id).Append(" — ").Append(entry.Name).Append(": ").Append(entry.Description)
              .Append(" [").Append(installed ? "installed" : "not installed").Append(", trust ").Append(entry.Trust).Append(']');
            var missing = McpTools.MissingSecrets(entry, secrets);
            if (missing.Count > 0) sb.Append(" (needs secret ").Append(string.Join(", ", missing)).Append(" set by the user in Settings → Secrets)");
            sb.AppendLine();
        }
        return FunctionResult.Ok(sb.Length == 0 ? "No gallery entry matches." : sb.ToString());
    }
}

/// <summary>
/// Boss Man: install a server from the curated MCP gallery (never arbitrary commands), optionally attach it to bots.
/// Always asks a human first, then starts the server once to check it works.
/// </summary>
public sealed class InstallMcpFunction : KernelFunctionBase
{
    public override FunctionDescriptor Descriptor { get; } = new(
        "install_mcp", "Install an MCP server from the curated gallery (see list_mcp_catalog) and optionally give it to bots. The user is asked to approve first. Custom servers can only be added by the user.",
        """
        {"type":"object","properties":{
          "id":{"type":"string","description":"Gallery id from list_mcp_catalog"},
          "bots":{"type":"array","items":{"type":"string"},"description":"Optional bot ids or names that should get this server"},
          "reason":{"type":"string","description":"Why the team needs it (shown to the user)"}},
          "required":["id"]}
        """,
        // The tool always asks a human itself (with a detailed summary), so policy treats the call like create_bot.
        "management", PermissionCategory.AgentControl, RiskLevel.Medium, 3600);

    protected override async ValueTask<FunctionResult> ExecuteAsync(FunctionCall call, FunctionExecutionContext ctx, CancellationToken ct)
    {
        var id = call.Require("id").Trim().ToLowerInvariant();
        var entry = McpCatalog.Entries.FirstOrDefault(e => e.Id == id);
        if (entry is null) return FunctionResult.Fail($"'{id}' is not in the curated gallery. Use list_mcp_catalog; other servers must be added by the user on the MCP page.");
        var store = ctx.Services.GetRequiredService<IDocumentStore<McpServerConfig>>();
        var registry = ctx.Services.GetRequiredService<BotRegistry>();
        var botNames = call.Arguments.TryGetProperty("bots", out var arr) && arr.ValueKind == JsonValueKind.Array
            ? arr.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList() : [];
        var bots = new List<BotDefinition>();
        foreach (var name in botNames)
            bots.Add(await registry.ResolveAsync(name, ct) ?? throw new ArgumentException($"No bot named '{name}'."));

        var approvals = ctx.Services.GetRequiredService<ApprovalService>();
        var approval = await approvals.RequestAsync(new ApprovalRequest
        {
            TaskId = ctx.TaskId, ThreadId = ctx.ThreadId, BotId = ctx.Bot.Id, ToolName = "install_mcp", Arguments = call.Arguments.GetRawText(),
            Category = PermissionCategory.Admin, Risk = RiskLevel.High,
            Reason = $"Install the MCP server '{entry.Name}' ({entry.Trust}) from the gallery" + (bots.Count > 0 ? $" for {string.Join(", ", bots.Select(b => b.Name))}" : "") + ".",
            Summary = $"**Install MCP server: {entry.Name}**\n\n{entry.Description}\n\n- Runs: `{entry.Command} {string.Join(' ', entry.Args)}`\n- Trust: {entry.Trust}, permission profile `{entry.PermissionProfile}`" +
                      (bots.Count > 0 ? $"\n- Give it to: {string.Join(", ", bots.Select(b => b.Name))}" : "") +
                      (call.GetString("reason") is { Length: > 0 } why ? $"\n\nWhy: {why}" : ""),
        }, ct);
        if (approval.State != ApprovalState.Approved) return FunctionResult.Fail("The user did not approve installing this MCP server.");

        var config = await store.GetAsync(id, ct) ?? new McpServerConfig
        {
            Id = entry.Id, Name = entry.Name, Description = entry.Description, Transport = entry.Transport, Command = entry.Command,
            Args = [.. entry.Args], Env = new(entry.Env), Url = entry.Url, Trust = entry.Trust, PermissionProfile = entry.PermissionProfile,
        };
        config.IsCatalogEntry = false;
        config.Enabled = true;
        await store.UpsertAsync(config, ct);
        foreach (var bot in bots.Where(b => !b.McpServers.Contains(id)))
        {
            bot.McpServers.Add(id);
            await registry.UpdateAsync(bot, ct);
        }

        var report = new System.Text.StringBuilder($"Installed MCP server '{entry.Name}'");
        if (bots.Count > 0) report.Append(" and gave it to ").Append(string.Join(", ", bots.Select(b => b.Name)));
        report.Append('.');
        var missing = McpTools.MissingSecrets(entry, ctx.Services.GetRequiredService<ISecretProvider>());
        if (missing.Count > 0)
        {
            report.Append($" It needs the secret {string.Join(", ", missing)}: ask the user to add it in Settings → Secrets; it will not start before that.");
            return FunctionResult.Ok(report.ToString());
        }
        try
        {
            var tools = await ctx.Services.GetRequiredService<McpManager>().GetToolsAsync(config, ctx.WorkspacePath, ct);
            report.Append($" Health check: started and offers {tools.Count} tools ({string.Join(", ", tools.Take(8).Select(t => t.Name))}{(tools.Count > 8 ? ", …" : "")}).");
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException or System.ComponentModel.Win32Exception)
        {
            report.Append($" Health check failed: {ex.Message}. The runtime it needs (npx/uvx) may be missing on this server.");
        }
        return FunctionResult.Ok(report.ToString());
    }
}

internal static class McpTools
{
    public static List<string> MissingSecrets(McpServerConfig entry, ISecretProvider secrets) =>
        entry.Env.Values.Where(v => v.StartsWith("secret:", StringComparison.Ordinal)).Select(v => v[7..])
            .Where(n => string.IsNullOrEmpty(secrets.Get(n))).Distinct().ToList();
}
