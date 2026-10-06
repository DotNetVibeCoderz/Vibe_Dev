using System.Text.Json;
using Marbots.Abstractions;
using Marbots.Sdk;

namespace Marbots.Cli;

/// <summary>Command-line control center for Marbots. Talks to a running server over the REST API.</summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var theme = Theme.Load();
        var url = Environment.GetEnvironmentVariable("MARBOTS_URL") ?? "http://localhost:5170";
        var key = Environment.GetEnvironmentVariable("MARBOTS_API_KEY");
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        using var client = new MarbotsClient(new Uri(url), key);
        var ui = new Ui(theme);
        var cmd = new Commands(client, ui);
        try
        {
            return await cmd.RunAsync(args, cts.Token);
        }
        catch (HttpRequestException ex)
        {
            ui.Error($"Cannot reach Marbots at {url} ({ex.Message}). Start the server with: dotnet run --project src/Marbots.Server");
            return 2;
        }
        catch (MarbotsApiException ex)
        {
            ui.Error(ex.Message);
            return 1;
        }
        catch (OperationCanceledException)
        {
            return 130;
        }
    }
}

internal sealed class Commands(MarbotsClient client, Ui ui)
{
    private static readonly string[] SkipFlags = ["--dangerously-skip-approvals", "--dangerously-skip-permissions"];

    public async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        // Like Claude Code: skip approvals only for the duration of this command, then restore the previous mode.
        if (args.Any(x => SkipFlags.Contains(x)))
        {
            args = args.Where(x => !SkipFlags.Contains(x)).ToArray();
            var previous = await client.Approvals.GetSkipApprovalsAsync(ct);
            await client.Approvals.SetSkipApprovalsAsync(true, ct);
            ui.Warn("Approvals are skipped for this command: bots act without asking.");
            try
            {
                return await RunCoreAsync(args, ct);
            }
            finally
            {
                if (!previous) await client.Approvals.SetSkipApprovalsAsync(false, CancellationToken.None);
            }
        }
        return await RunCoreAsync(args, ct);
    }

    private async Task<int> RunCoreAsync(string[] args, CancellationToken ct)
    {
        if (args.Length == 0) return await StatusAsync(ct);
        var a = args.ToList();
        string Arg(int i) => i < a.Count ? a[i] : throw new ArgumentException("Missing argument. Run 'marbots help'.");
        string? Opt(string name)
        {
            var i = a.IndexOf(name);
            return i >= 0 && i + 1 < a.Count ? a[i + 1] : null;
        }
        bool Flag(string name) => a.Contains(name);

        switch (a[0])
        {
            case "help" or "--help" or "-h": Help(); return 0;
            case "status": return await StatusAsync(ct);
            case "bots": return await BotsAsync(ct);
            case "templates": return await TemplatesAsync(a.Count > 1 ? a[1] : null, ct);
            case "tasks": return await TasksAsync(ct);
            case "approvals" when a.Count > 1 && a[1] == "skip": return await SkipAsync(a, ct);
            case "approvals": return await ApprovalsAsync(ct);
            case "approve": await client.Approvals.ApproveAsync(Arg(1), Flag("--session") ? ApprovalScope.Session : ApprovalScope.Once, ct); ui.Ok("Approved."); return 0;
            case "reject": await client.Approvals.RejectAsync(Arg(1), ct); ui.Ok("Rejected."); return 0;
            case "skills": return await SkillsAsync(a, ct);
            case "mcp": return await McpAsync(a, ct);
            case "schedules": return await SchedulesAsync(ct);
            case "hosts": return await HostsAsync(ct);
            case "models": return await ModelsAsync(a, ct);
            case "channels":
                foreach (var v in await client.Channels.ListAsync(ct))
                    ui.Row(v.Channel.Name, v.Channel.Kind, v.Channel.BotId, v.Channel.Enabled ? "on" : "off", $"in {v.Channel.MessagesIn}/out {v.Channel.MessagesOut}", v.InboundUrl);
                return 0;
            case "triggers":
                foreach (var t in await client.Triggers.ListAsync(ct))
                    ui.Row(t.Name, t.Kind, t.BotId, $"{t.FireCount}x", t.Kind == TriggerKinds.Webhook ? $"POST /api/v1/hooks/{t.Id}" : $"after {t.SourceBotId ?? "any bot"}");
                return 0;
            case "delegation" when a.Count > 1 && Enum.TryParse<DelegationMode>(a[1], true, out var mode):
                ui.Ok($"Delegation mode: {await client.SetDelegationModeAsync(mode, ct)}");
                return 0;
            case "delegation":
                ui.Line($"Delegation mode: {await client.GetDelegationModeAsync(ct)} (set with: marbots delegation auto|suggest)");
                return 0;
            case "logs": return await LogsAsync(Opt("--thread"), ct);
            case "chat": return await ChatAsync(Arg(1), a.Count > 2 && !a[2].StartsWith("--", StringComparison.Ordinal) ? string.Join(' ', a.Skip(2)) : null, ct);
            case "theme" when a.Count > 2 && a[1] == "set": Theme.Save(a[2]); ui.Ok($"Theme set to {a[2]}."); return 0;
            case "theme": ui.Line("Themes: " + string.Join(", ", Theme.Names)); return 0;
            case "bot": return await BotAsync(a, Opt, Flag, ct);
            default: ui.Error($"Unknown command '{a[0]}'."); Help(); return 1;
        }
    }

    private void Help()
    {
        ui.Title("marbots — multi-agent control center");
        ui.Line("""
          status                         Server, model and team overview
          bots                           List bots
          bot inspect <bot>              Show a bot's definition
          bot hire <template> [--name N] Create a bot from a template
          bot export <bot> [-o file] [--memory]
          bot import <file.marbot>
          bot pause|resume|delete <bot>
          bot model <bot> [model]        Show or set a bot's model (default | provider/model | profile)
          models                         Default model, choices and profiles
          channels | triggers            External channels and webhook/event triggers
          delegation [auto|suggest]      Show or set whether Boss Man's plans need approval
          models default <provider/model>  Change the workspace default model
          templates [query]              Search the template gallery
          chat <bot> [message]           Chat (interactive when no message is given)
          tasks                          Recent tasks
          approvals | approve <id> [--session] | reject <id>
          approvals skip on|off|status   Dangerous: let bots act without asking (like --dangerously-skip-permissions)
          <any command> --dangerously-skip-approvals   Skip approvals only while that command runs
                                         (alias: --dangerously-skip-permissions)
          skills | skills install <git-url-or-folder>
          mcp | mcp install <id>
          schedules                      Scheduled jobs
          hosts                          Registered hosts
          logs [--thread <id>]           Follow the live event stream
          theme | theme set <name>       CLI colours (default, aurora, matrix, mono, high-contrast)

        Environment: MARBOTS_URL (default http://localhost:5170), MARBOTS_API_KEY
        """);
        ui.Dim(WellKnown.CreditsEn);
    }

    private async Task<int> StatusAsync(CancellationToken ct)
    {
        var info = await client.SystemAsync(ct);
        var bots = await client.Bots.ListAsync(ct);
        var pending = await client.Approvals.PendingAsync(ct);
        ui.Title($"Marbots {info.Version}");
        ui.Dim(info.CreditsEn);
        ui.Line($"Model: {(info.ModelConfigured ? string.Join("; ", info.Profiles) : "not configured (offline mock mode)")}");
        ui.Line($"Team:  {bots.Count} bots, {bots.Count(b => b.Status == BotStatus.Running)} working");
        if (pending.Count > 0) ui.Warn($"{pending.Count} approval(s) waiting — run 'marbots approvals'");
        return 0;
    }

    private async Task<int> BotsAsync(CancellationToken ct)
    {
        foreach (var b in await client.Bots.ListAsync(ct))
            ui.Row(ui.Bot(b.Name, b.Color), b.Id, b.Role, b.Status.ToString(), b.PermissionProfile);
        return 0;
    }

    private async Task<int> BotAsync(List<string> a, Func<string, string?> opt, Func<string, bool> flag, CancellationToken ct)
    {
        if (a.Count < 3) { Help(); return 1; }
        var sub = a[1];
        var target = a[2];
        switch (sub)
        {
            case "inspect":
                var bot = await client.Bots.GetAsync(target, ct);
                ui.Line(JsonSerializer.Serialize(bot, MarbotsJsonContext.Indented.BotDefinition));
                return 0;
            case "hire":
                var hired = await client.Bots.HireAsync(target, opt("--name"), ct);
                ui.Ok($"{hired.Name} joined the team (id: {hired.Id}).");
                return 0;
            case "export":
                var b2 = await client.Bots.GetAsync(target, ct);
                var bytes = await client.Bots.ExportAsync(b2.Id, flag("--memory"), ct);
                var file = opt("-o") ?? $"{b2.Id}.marbot";
                await File.WriteAllBytesAsync(file, bytes, ct);
                ui.Ok($"Exported {b2.Name} to {file} ({bytes.Length:N0} bytes, no secrets).");
                return 0;
            case "import":
                await using (var fs = File.OpenRead(target))
                {
                    var imported = await client.Bots.ImportAsync(fs, ct);
                    ui.Ok($"Imported {imported.Name} (id: {imported.Id}).");
                }
                return 0;
            case "model":
                var info = a.Count > 3 ? await client.Bots.SetModelAsync(target, a[3], ct) : await client.Bots.GetModelAsync(target, ct);
                ui.Line($"{info.BotId}: {info.Effective}{(info.UsesDefault ? " (workspace default)" : $" (setting: {info.Setting})")}");
                if (info.Warning is not null) ui.Warn(info.Warning);
                return 0;
            case "pause": await client.Bots.PauseAsync(target, ct); ui.Ok("Paused."); return 0;
            case "resume": await client.Bots.ResumeAsync(target, ct); ui.Ok("Resumed."); return 0;
            case "delete": await client.Bots.DeleteAsync(target, ct); ui.Ok("Deleted."); return 0;
            default: Help(); return 1;
        }
    }

    private async Task<int> TemplatesAsync(string? query, CancellationToken ct)
    {
        foreach (var t in await client.Templates.ListAsync(query, null, ct))
            ui.Row(ui.Bot(t.Name, t.Color), t.Id, t.Category, t.Description);
        return 0;
    }

    private async Task<int> TasksAsync(CancellationToken ct)
    {
        foreach (var t in (await client.Tasks.ListAsync(null, ct)).Take(30))
            ui.Row(new string(' ', t.Depth * 2) + t.State, t.BotId, $"{t.InputTokens + t.OutputTokens:N0} tok", AgentPreview(t.Objective, 70), t.Id);
        return 0;
    }

    private async Task<int> ApprovalsAsync(CancellationToken ct)
    {
        var list = await client.Approvals.PendingAsync(ct);
        if (list.Count == 0) { ui.Dim("Nothing waiting."); return 0; }
        foreach (var x in list)
        {
            ui.Warn($"{x.Id}  {x.BotId} wants {x.ToolName} ({x.Risk})");
            ui.Dim("  " + AgentPreview(x.Arguments, 160));
        }
        ui.Dim("Approve with: marbots approve <id> [--session]");
        return 0;
    }

    private async Task<int> SkipAsync(List<string> a, CancellationToken ct)
    {
        var mode = a.Count > 2 ? a[2] : "status";
        bool skip;
        switch (mode)
        {
            case "on": skip = await client.Approvals.SetSkipApprovalsAsync(true, ct); break;
            case "off": skip = await client.Approvals.SetSkipApprovalsAsync(false, ct); break;
            case "status": skip = await client.Approvals.GetSkipApprovalsAsync(ct); break;
            default: ui.Error("Use: marbots approvals skip on|off|status"); return 1;
        }
        if (skip) ui.Warn("Approvals are SKIPPED: bots run shell, delete files and send external messages without asking. Turn off with: marbots approvals skip off");
        else ui.Ok("Approvals are required.");
        return 0;
    }

    private async Task<int> SkillsAsync(List<string> a, CancellationToken ct)
    {
        if (a.Count > 2 && a[1] == "install")
        {
            var installed = await client.Skills.InstallAsync(a[2], ct);
            ui.Ok($"Installed: {string.Join(", ", installed.Select(s => s.Name))}");
            return 0;
        }
        foreach (var s in await client.Skills.ListAsync(ct)) ui.Row(s.Name, s.Trust, s.Pending ? "pending review" : "", AgentPreview(s.Description, 80));
        return 0;
    }

    private async Task<int> McpAsync(List<string> a, CancellationToken ct)
    {
        if (a.Count > 2 && a[1] == "install")
        {
            var s = await client.Mcp.InstallAsync(a[2], ct);
            ui.Ok($"{s.Name} installed.");
            return 0;
        }
        foreach (var s in await client.Mcp.ListAsync(ct)) ui.Row(s.Id, s.IsCatalogEntry ? "available" : "installed", s.Transport, AgentPreview(s.Description, 70));
        return 0;
    }

    private async Task<int> SchedulesAsync(CancellationToken ct)
    {
        foreach (var j in await client.Schedules.ListAsync(ct))
            ui.Row(j.Name, j.BotId, string.IsNullOrEmpty(j.Cron) ? "once" : j.Cron, j.NextRunAt?.ToLocalTime().ToString("g") ?? "—");
        return 0;
    }

    private async Task<int> ModelsAsync(List<string> a, CancellationToken ct)
    {
        if (a.Count > 2 && a[1] == "default")
        {
            ui.Ok($"Default model is now {await client.Models.SetDefaultAsync(a[2], ct)}.");
            return 0;
        }
        var catalog = await client.Models.ListAsync(ct);
        ui.Title($"Default: {catalog.Default}");
        foreach (var c in catalog.Choices) ui.Row(c == catalog.Default ? $"* {c}" : $"  {c}");
        foreach (var p in catalog.Profiles.Where(p => p.Name != "default")) ui.Row($"  {p.Name}", $"{p.Provider}/{p.Model}", "profile");
        var bots = await client.Bots.ListAsync(ct);
        ui.Dim($"{bots.Count(b => b.ModelProfile == ModelRef.Default)} of {bots.Count} bots follow the default.");
        return 0;
    }

    private async Task<int> HostsAsync(CancellationToken ct)
    {
        foreach (var h in await client.HostsAsync(ct))
            ui.Row(h.Id, h.Name, h.Status, $"{h.Os} {h.Architecture}", $"{h.ProcessorCount} cpu", $"{h.ProcessWorkingSetMb} MB");
        return 0;
    }

    private async Task<int> LogsAsync(string? threadId, CancellationToken ct)
    {
        ui.Dim("Following events — Ctrl+C to stop.");
        await foreach (var e in client.Events.StreamAsync(threadId, null, ct)) PrintEvent(e);
        return 0;
    }

    private async Task<int> ChatAsync(string botRef, string? message, CancellationToken ct)
    {
        var bot = await client.Bots.GetAsync(botRef, ct);
        var thread = await client.Threads.CreateAsync(bot.Id, null, ct);
        ui.Title($"{bot.Name} · {bot.Role}");
        ui.Dim($"thread {thread.Id} — type /exit to quit");
        if (message is not null) return await TurnAsync(thread.Id, message, ct);
        while (!ct.IsCancellationRequested)
        {
            Console.Write(ui.Prompt("you › "));
            var line = Console.ReadLine();
            if (line is null || line.Trim() is "/exit" or "/quit") break;
            if (line.Trim().Length == 0) continue;
            await TurnAsync(thread.Id, line, ct);
        }
        return 0;
    }

    private async Task<int> TurnAsync(string threadId, string text, CancellationToken ct)
    {
        using var streamCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var sent = await client.Threads.SendAsync(threadId, text, false, 600, ct);
        var rootId = sent.Task.Id;
        var spinner = ui.Spinner();
        try
        {
            await foreach (var e in client.Events.StreamAsync(threadId, null, streamCts.Token))
            {
                if (e.Type is EventTypes.ToolCallStarted or EventTypes.TaskDelegated or EventTypes.ApprovalRequested or EventTypes.SkillLoaded)
                    PrintEvent(e, spinner.Next());
                if (e.Type == EventTypes.TaskStateChanged && e.TaskId == rootId && e.Data is "Completed" or "Failed" or "Cancelled") break;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        var task = await client.Tasks.GetAsync(rootId, ct);
        var messages = await client.Threads.MessagesAsync(threadId, 0, ct);
        var reply = messages.LastOrDefault(m => m.TaskId == rootId && m.Role is "assistant" or "system" && m.ToolCalls is null);
        Console.WriteLine();
        if (task.State == TaskState.Completed) ui.Reply(reply?.Content ?? task.Result ?? "");
        else ui.Error($"{task.State}: {task.Error}");
        ui.Dim($"{task.Steps} steps · {task.InputTokens + task.OutputTokens:N0} tokens · ${task.CostUsd:0.0000}");
        return task.State == TaskState.Completed ? 0 : 1;
    }

    private void PrintEvent(AgentEvent e, string? glyph = null) =>
        ui.Event(glyph ?? "•", e.Timestamp.ToLocalTime().ToString("HH:mm:ss"), e.BotId ?? "", e.Type, AgentPreview(e.Message, 110));

    private static string AgentPreview(string? s, int n)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.ReplaceLineEndings(" ");
        return s.Length <= n ? s : s[..n] + "…";
    }
}
