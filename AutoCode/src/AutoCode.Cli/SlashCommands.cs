// Auto Code — Gravicode Studios (Kang Fadhil)

using AutoCode.Cli.Ui;
using AutoCode.Core.Abstractions;
using AutoCode.Core.Permissions;
using Spectre.Console;

namespace AutoCode.Cli;

/// <summary>What the REPL should do after a slash command ran.</summary>
public enum SlashResult
{
    /// <summary>Handled; wait for the next input.</summary>
    Handled = 0,

    /// <summary>Not a slash command; treat the input as a prompt.</summary>
    NotACommand = 1,

    /// <summary>Leave the REPL.</summary>
    Exit = 2,

    /// <summary>Handled, and the expansion in <see cref="SlashCommandRouter.PendingPrompt"/> should be sent.</summary>
    SendPrompt = 3,
}

/// <summary>
/// The <c>/command</c> surface.
///
/// EN: slash commands are the parts of the product that must never involve the model — switching
/// provider, inspecting cost, changing permission posture. Routing them here keeps them instant and
/// keeps them working even when the provider is misconfigured.
/// ID: perintah garis miring adalah bagian produk yang tidak boleh melibatkan model — berganti
/// provider, memeriksa biaya, mengubah mode izin. Semuanya ditangani di sini agar tetap responsif
/// dan tetap berfungsi walau konfigurasi provider bermasalah.
/// </summary>
public sealed class SlashCommandRouter(AutoCodeSession session, Theme theme)
{
    /// <summary>Set when a command expands into a prompt for the model (skills, /init).</summary>
    public string? PendingPrompt { get; private set; }

    public async Task<SlashResult> HandleAsync(string input, CancellationToken cancellationToken)
    {
        PendingPrompt = null;

        if (!input.StartsWith('/'))
            return SlashResult.NotACommand;

        var parts = input[1..].Split(' ', 2, StringSplitOptions.TrimEntries);
        var name = parts[0].ToLowerInvariant();
        var argument = parts.Length > 1 ? parts[1] : null;

        switch (name)
        {
            case "help" or "?":
                ShowHelp();
                return SlashResult.Handled;

            case "exit" or "quit" or "q":
                return SlashResult.Exit;

            case "clear":
                session.Loop.ResetMessages([]);
                session.Session.Messages.Clear();
                AnsiConsole.Clear();
                Banner.Render(session, theme);
                return SlashResult.Handled;

            case "compact":
                await CompactAsync(cancellationToken).ConfigureAwait(false);
                return SlashResult.Handled;

            case "cost":
                ShowCost();
                return SlashResult.Handled;

            case "status":
                ShowStatus();
                return SlashResult.Handled;

            case "model":
                ShowOrSetModel(argument);
                return SlashResult.Handled;

            case "provider":
                ShowOrSetProvider(argument);
                return SlashResult.Handled;

            case "permissions" or "mode":
                ShowOrSetPermissions(argument);
                return SlashResult.Handled;

            case "tools":
                ShowTools();
                return SlashResult.Handled;

            case "agents":
                ShowAgents();
                return SlashResult.Handled;

            case "teams":
                return await RunTeamAsync(argument, cancellationToken).ConfigureAwait(false);

            case "skills":
                ShowSkills();
                return SlashResult.Handled;

            case "mcp":
                ShowMcp();
                return SlashResult.Handled;

            case "context":
                ShowContext();
                return SlashResult.Handled;

            case "sessions" or "resume":
                await ShowSessionsAsync(cancellationToken).ConfigureAwait(false);
                return SlashResult.Handled;

            case "index":
                await BuildIndexAsync(cancellationToken).ConfigureAwait(false);
                return SlashResult.Handled;

            case "export":
                await ExportAsync(argument, cancellationToken).ConfigureAwait(false);
                return SlashResult.Handled;

            case "init":
                PendingPrompt = InitPrompt;
                return SlashResult.SendPrompt;

            case "language" or "lang":
                SetLanguage(argument);
                return SlashResult.Handled;

            default:
                return await TryRunSkillAsync(name, argument, cancellationToken).ConfigureAwait(false);
        }
    }

    private void ShowHelp()
    {
        var table = new Table()
            .Border(TableBorder.None)
            .AddColumn(new TableColumn("").PadRight(3))
            .AddColumn("");

        void Row(string command, string description) =>
            table.AddRow($"[{theme.Accent}]{command}[/]", $"[{theme.Muted}]{description}[/]");

        Row("/help", "Show this list");
        Row("/clear", "Start a fresh conversation");
        Row("/compact", "Summarise the conversation to free context");
        Row("/cost", "Token and cost accounting for this session");
        Row("/status", "Provider, model, workspace and permission posture");
        Row("/model [id]", "Show or switch the model");
        Row("/provider [name]", "Show or switch the provider profile");
        Row("/permissions [mode]", "Show or set ask | acceptEdits | plan | bypassPermissions");
        Row("/tools", "List the tools available to the model");
        Row("/agents", "List the subagents that can be dispatched");
        Row("/teams <name> <brief>", "Run an agent team on a brief");
        Row("/skills", "List installed skills");
        Row("/mcp", "Show MCP server connections");
        Row("/context", "Show the AUTOCODE.md files in effect");
        Row("/sessions", "List saved sessions for this workspace");
        Row("/index", "Build the semantic code index");
        Row("/export [path]", "Write the transcript to a markdown file");
        Row("/init", "Generate an AUTOCODE.md for this project");
        Row("/language <en|id>", "Switch the interface and reply language");
        Row("/exit", "Leave Auto Code");

        AnsiConsole.Write(table);

        if (session.Skills.Count > 0)
        {
            AnsiConsole.MarkupLine($"\n[{theme.Muted}]Skills are also slash commands: {string.Join(", ", session.Skills.Select(s => "/" + s.Name))}[/]");
        }
    }

    private async Task CompactAsync(CancellationToken cancellationToken)
    {
        // The summarizer client is created per invocation and owned here, so it has to be disposed;
        // leaving it to the GC leaks a socket handle on every /compact.
        using var summarizer = Providers.ProviderFactory.CreateSmallChatClient(session.Profile);

        var compactor = new Core.Agents.ContextCompactor(summarizer);
        var before = session.Loop.Messages.Count;

        var result = await AnsiConsole.Status()
            .StartAsync("Compacting…", _ => compactor.CompactAsync(session.Loop.Messages, cancellationToken))
            .ConfigureAwait(false);

        if (result.Summary is null)
        {
            AnsiConsole.MarkupLine($"[{theme.Muted}]Nothing to compact yet.[/]");
            return;
        }

        session.Loop.ResetMessages(result.Messages);

        // Recorded the same way auto-compaction records it, so an exported transcript still shows
        // that history was folded away rather than silently losing it.
        session.Session.CompactionSummaries.Add(result.Summary);

        AnsiConsole.MarkupLine($"[{theme.Success}]Compacted {before} → {result.Messages.Count} messages.[/]");
    }

    private void ShowCost()
    {
        var grid = new Grid().AddColumn(new GridColumn().PadRight(3)).AddColumn();

        grid.AddRow($"[{theme.Muted}]Input tokens[/]", $"{session.Cost.InputTokens:N0}");
        grid.AddRow($"[{theme.Muted}]Output tokens[/]", $"{session.Cost.OutputTokens:N0}");

        if (session.Cost.CachedInputTokens > 0)
            grid.AddRow($"[{theme.Muted}]Cached input[/]", $"{session.Cost.CachedInputTokens:N0}");

        grid.AddRow($"[{theme.Muted}]Requests[/]", $"{session.Cost.RequestCount:N0}");
        grid.AddRow($"[{theme.Muted}]Context used[/]", $"{session.Cost.ContextUtilization:P0} of {session.Profile.ContextWindow:N0}");
        grid.AddRow($"[{theme.Muted}]Estimated cost[/]", $"${session.Cost.TotalCostUsd:F4}");

        AnsiConsole.Write(new Panel(grid)
        {
            Header = new PanelHeader(" Cost ", Justify.Left),
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(theme.AccentColor),
        });
    }

    private void ShowStatus()
    {
        var grid = new Grid().AddColumn(new GridColumn().PadRight(3)).AddColumn();

        grid.AddRow($"[{theme.Muted}]Workspace[/]", Markup.Escape(session.WorkspaceRoot));
        grid.AddRow($"[{theme.Muted}]Provider[/]", $"{Markup.Escape(session.Profile.Name)} ({session.Profile.Kind})");
        grid.AddRow($"[{theme.Muted}]Model[/]", Markup.Escape(session.Profile.Model));
        grid.AddRow($"[{theme.Muted}]Permissions[/]", session.Permissions.Mode.ToString());
        grid.AddRow($"[{theme.Muted}]Tools[/]", $"{session.Tools.Tools.Count}");
        grid.AddRow($"[{theme.Muted}]Session[/]", $"{Markup.Escape(session.Session.Id)} · {session.Session.UserTurnCount} turns");
        grid.AddRow($"[{theme.Muted}]Context files[/]", session.ContextFiles.Count == 0
            ? "(none)"
            : string.Join(", ", session.ContextFiles.Select(f => Markup.Escape(f.DisplayPath))));

        grid.AddRow($"[{theme.Muted}]Embeddings[/]", DescribeEmbeddings());

        AnsiConsole.Write(new Panel(grid)
        {
            Header = new PanelHeader(" Status ", Justify.Left),
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(theme.AccentColor),
        });
    }

    private string DescribeEmbeddings()
    {
        if (!session.Options.EnableSemanticIndex)
            return $"[{theme.Muted}]disabled (enableSemanticIndex)[/]";

        if (session.EmbeddingError is { Length: > 0 } error)
            return $"[{theme.Error}]{Markup.Escape(error)}[/]";

        if (session.EmbeddingBackend is null or { Kind: Core.Configuration.EmbeddingProviderKind.None })
            return $"[{theme.Warning}]no backend — set embeddings.kind[/]";

        return Markup.Escape(session.EmbeddingBackend.Describe());
    }

    private void ShowOrSetModel(string? argument)
    {
        if (string.IsNullOrWhiteSpace(argument))
        {
            AnsiConsole.MarkupLine($"Model: [{theme.Accent}]{Markup.Escape(session.Profile.Model)}[/] on provider [{theme.Accent}]{Markup.Escape(session.Profile.Name)}[/]");
            AnsiConsole.MarkupLine($"[{theme.Muted}]Change it with /model <id>.[/]");
            return;
        }

        session.SwitchModel(argument.Trim());
        AnsiConsole.MarkupLine($"[{theme.Success}]Model switched to {Markup.Escape(session.Profile.Model)}.[/]");
    }

    private void ShowOrSetProvider(string? argument)
    {
        if (string.IsNullOrWhiteSpace(argument))
        {
            var table = new Table().Border(TableBorder.None)
                .AddColumn(new TableColumn("").PadRight(2))
                .AddColumn(new TableColumn("").PadRight(3))
                .AddColumn("");

            foreach (var (name, profile) in session.Options.Providers.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            {
                var active = string.Equals(name, session.Profile.Name, StringComparison.OrdinalIgnoreCase);
                var ready = profile.ResolveApiKey() is not null;

                table.AddRow(
                    active ? $"[{theme.Accent}]●[/]" : " ",
                    $"[{(active ? theme.Accent : theme.ToolName)}]{Markup.Escape(name)}[/]",
                    $"[{theme.Muted}]{Markup.Escape(profile.Model)}{(ready ? "" : "  (no API key)")}[/]");
            }

            AnsiConsole.Write(table);
            AnsiConsole.MarkupLine($"[{theme.Muted}]Switch with /provider <name>.[/]");
            return;
        }

        if (session.SwitchProvider(argument.Trim()))
            AnsiConsole.MarkupLine($"[{theme.Success}]Provider switched to {Markup.Escape(session.Profile.Name)} ({Markup.Escape(session.Profile.Model)}).[/]");
        else
            AnsiConsole.MarkupLine($"[{theme.Error}]No provider profile named '{Markup.Escape(argument)}'.[/]");
    }

    private void ShowOrSetPermissions(string? argument)
    {
        if (string.IsNullOrWhiteSpace(argument))
        {
            AnsiConsole.MarkupLine($"Permission mode: [{theme.Accent}]{session.Permissions.Mode}[/]");

            if (session.Permissions.Rules.Allow.Count > 0)
                AnsiConsole.MarkupLine($"[{theme.Muted}]Allow: {Markup.Escape(string.Join(", ", session.Permissions.Rules.Allow))}[/]");

            if (session.Permissions.Rules.Deny.Count > 0)
                AnsiConsole.MarkupLine($"[{theme.Muted}]Deny: {Markup.Escape(string.Join(", ", session.Permissions.Rules.Deny))}[/]");

            AnsiConsole.MarkupLine($"[{theme.Muted}]Change it with /permissions ask|acceptEdits|plan|bypassPermissions.[/]");
            return;
        }

        var mode = argument.Trim().ToLowerInvariant().Replace("-", "") switch
        {
            "ask" or "default" => (PermissionMode?)PermissionMode.Ask,
            "acceptedits" => PermissionMode.AcceptEdits,
            "plan" => PermissionMode.Plan,
            "bypasspermissions" or "yolo" => PermissionMode.BypassPermissions,
            _ => null,
        };

        if (mode is null)
        {
            AnsiConsole.MarkupLine($"[{theme.Error}]Unknown mode '{Markup.Escape(argument)}'.[/]");
            return;
        }

        session.Permissions.Mode = mode.Value;

        // The system prompt states the current posture, so the model has to be told it changed.
        session.RebuildLoop();

        AnsiConsole.MarkupLine($"[{theme.Success}]Permission mode set to {mode}.[/]");
    }

    private void ShowTools()
    {
        var table = new Table().Border(TableBorder.None)
            .AddColumn(new TableColumn("").PadRight(3))
            .AddColumn("");

        foreach (var tool in session.Tools.Tools.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
        {
            var capability = tool.Capability switch
            {
                var c when (c & ToolCapability.ExecutesCommands) != 0 => "runs commands",
                var c when (c & ToolCapability.WritesFiles) != 0 => "writes files",
                var c when (c & ToolCapability.AccessesNetwork) != 0 => "network",
                var c when (c & ToolCapability.ReadsFiles) != 0 => "read-only",
                _ => "local",
            };

            table.AddRow($"[{theme.ToolName}]{Markup.Escape(tool.Name)}[/]", $"[{theme.Muted}]{capability}[/]");
        }

        AnsiConsole.Write(table);
    }

    private void ShowAgents()
    {
        if (session.Agents.Count == 0)
        {
            AnsiConsole.MarkupLine($"[{theme.Muted}]No subagents are defined.[/]");
            return;
        }

        foreach (var agent in session.Agents.Values.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase))
        {
            AnsiConsole.MarkupLine($"[{theme.Accent}]{Markup.Escape(agent.Name)}[/]");
            AnsiConsole.MarkupLine($"  [{theme.Muted}]{Markup.Escape(agent.Description)}[/]");

            if (agent.Tools.Count > 0)
                AnsiConsole.MarkupLine($"  [{theme.Muted}]tools: {Markup.Escape(string.Join(", ", agent.Tools))}[/]");
        }
    }

    private async Task<SlashResult> RunTeamAsync(string? argument, CancellationToken cancellationToken)
    {
        if (session.Teams is null || session.Teams.AvailableTeams.Count == 0)
        {
            AnsiConsole.MarkupLine($"[{theme.Muted}]No agent teams are configured. Define them under \"teams\" in settings.json.[/]");
            return SlashResult.Handled;
        }

        var parts = argument?.Split(' ', 2, StringSplitOptions.TrimEntries) ?? [];

        if (parts.Length < 2)
        {
            AnsiConsole.MarkupLine($"[{theme.Muted}]Teams: {string.Join(", ", session.Teams.AvailableTeams)}[/]");
            AnsiConsole.MarkupLine($"[{theme.Muted}]Usage: /teams <name> <brief>[/]");
            return SlashResult.Handled;
        }

        var report = await session.Teams.RunAsync(parts[0], parts[1], cancellationToken).ConfigureAwait(false);
        AnsiConsole.WriteLine();
        AnsiConsole.WriteLine(report);

        return SlashResult.Handled;
    }

    private void ShowSkills()
    {
        if (session.Skills.Count == 0)
        {
            AnsiConsole.MarkupLine($"[{theme.Muted}]No skills installed. Add them under .autocode/skills/<name>/SKILL.md.[/]");
            return;
        }

        foreach (var skill in session.Skills)
        {
            AnsiConsole.MarkupLine($"[{theme.Accent}]/{Markup.Escape(skill.Name)}[/]");
            AnsiConsole.MarkupLine($"  [{theme.Muted}]{Markup.Escape(skill.Description)}[/]");
        }
    }

    private void ShowMcp()
    {
        if (session.McpConnections.Count == 0)
        {
            AnsiConsole.MarkupLine($"[{theme.Muted}]No MCP servers configured. Add them under \"mcpServers\" in settings.json.[/]");
            return;
        }

        foreach (var connection in session.McpConnections)
        {
            if (connection.Connected)
            {
                AnsiConsole.MarkupLine(
                    $"[{theme.Success}]●[/] [{theme.ToolName}]{Markup.Escape(connection.ServerName)}[/] [{theme.Muted}]{connection.ToolCount} tools[/]");
            }
            else
            {
                AnsiConsole.MarkupLine(
                    $"[{theme.Error}]●[/] [{theme.ToolName}]{Markup.Escape(connection.ServerName)}[/] [{theme.Error}]{Markup.Escape(connection.Error ?? "failed")}[/]");
            }
        }
    }

    private void ShowContext()
    {
        if (session.ContextFiles.Count == 0)
        {
            AnsiConsole.MarkupLine($"[{theme.Muted}]No AUTOCODE.md or CLAUDE.md found. Run /init to generate one.[/]");
            return;
        }

        foreach (var file in session.ContextFiles)
        {
            AnsiConsole.MarkupLine(
                $"[{theme.Accent}]{Markup.Escape(file.DisplayPath)}[/] [{theme.Muted}]({file.Scope}, {file.Content.Length:N0} chars)[/]");
        }
    }

    private async Task ShowSessionsAsync(CancellationToken cancellationToken)
    {
        var sessions = await session.Store.ListAsync(15, cancellationToken).ConfigureAwait(false);

        if (sessions.Count == 0)
        {
            AnsiConsole.MarkupLine($"[{theme.Muted}]No saved sessions for this workspace.[/]");
            return;
        }

        var table = new Table().Border(TableBorder.None)
            .AddColumn(new TableColumn("").PadRight(2))
            .AddColumn(new TableColumn("").PadRight(2))
            .AddColumn("");

        foreach (var saved in sessions)
        {
            var current = saved.Id == session.Session.Id;

            table.AddRow(
                current ? $"[{theme.Accent}]●[/]" : " ",
                $"[{theme.ToolName}]{Markup.Escape(saved.Id)}[/]",
                $"[{theme.Muted}]{saved.UpdatedAt.LocalDateTime:yyyy-MM-dd HH:mm}  {Markup.Escape(saved.Title)}[/]");
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"[{theme.Muted}]Resume one with: autocode --resume <id>[/]");
    }

    private async Task BuildIndexAsync(CancellationToken cancellationToken)
    {
        var message = await AnsiConsole.Status()
            .StartAsync("Indexing…", async ctx =>
            {
                var progress = new Progress<string>(status => ctx.Status(Markup.Escape(status)));
                return await session.BuildIndexAsync(progress, cancellationToken).ConfigureAwait(false);
            })
            .ConfigureAwait(false);

        AnsiConsole.MarkupLine($"[{theme.Success}]{Markup.Escape(message)}[/]");
    }

    private async Task ExportAsync(string? argument, CancellationToken cancellationToken)
    {
        var path = string.IsNullOrWhiteSpace(argument)
            ? Path.Combine(session.WorkspaceRoot, $"autocode-{session.Session.Id}.md")
            : Core.Utilities.WorkspacePath.Resolve(session.WorkspaceRoot, argument);

        var markdown = Transcript.ToMarkdown(session);
        await File.WriteAllTextAsync(path, markdown, cancellationToken).ConfigureAwait(false);

        AnsiConsole.MarkupLine($"[{theme.Success}]Transcript written to {Markup.Escape(Core.Utilities.WorkspacePath.Relative(session.WorkspaceRoot, path))}.[/]");
    }

    private void SetLanguage(string? argument)
    {
        var language = argument?.Trim().ToLowerInvariant();

        if (language is not ("en" or "id"))
        {
            AnsiConsole.MarkupLine($"[{theme.Error}]Use /language en or /language id.[/]");
            return;
        }

        session.Options.Language = language;
        session.RebuildLoop();

        AnsiConsole.MarkupLine(language == "id"
            ? $"[{theme.Success}]Bahasa antarmuka diatur ke Bahasa Indonesia.[/]"
            : $"[{theme.Success}]Interface language set to English.[/]");
    }

    /// <summary>A skill invoked as <c>/name</c> expands into a prompt rather than running code.</summary>
    private async Task<SlashResult> TryRunSkillAsync(string name, string? argument, CancellationToken cancellationToken)
    {
        var skill = session.Skills.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

        if (skill is null)
        {
            AnsiConsole.MarkupLine($"[{theme.Error}]Unknown command '/{Markup.Escape(name)}'. Try /help.[/]");
            return SlashResult.Handled;
        }

        PendingPrompt = await skill.RenderAsync(argument, cancellationToken).ConfigureAwait(false);
        return SlashResult.SendPrompt;
    }

    private const string InitPrompt =
        """
        Analyse this codebase and create an AUTOCODE.md file at the workspace root that will give a
        future Auto Code session what it needs to be productive here quickly.

        Include:
        1. The commands that actually matter — build, test, lint, run — including how to run a single test.
        2. The high-level architecture: the parts that require reading several files to understand,
           and how they fit together.

        Leave out anything discoverable in seconds: a file listing, obvious conventions, or generic
        advice about writing good code. If a README, .cursorrules or .github/copilot-instructions.md
        exists, fold in what is genuinely useful from it.

        If AUTOCODE.md or CLAUDE.md already exists, read it and suggest improvements rather than
        overwriting what is already correct.
        """;
}
