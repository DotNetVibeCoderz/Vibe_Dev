// Auto Code — Gravicode Studios (Kang Fadhil)

using System.Text.Json;
using AutoCode.Cli.Ui;
using AutoCode.Core.Configuration;
using AutoCode.Core.Sessions;
using AutoCode.Providers;
using Microsoft.Extensions.AI;
using Spectre.Console;

namespace AutoCode.Cli;

/// <summary>
/// The non-conversational commands: configuration, MCP inspection, session management and a
/// connectivity check. These never start the agent, so they work when the model does not.
/// </summary>
public static class Subcommands
{
    public static async Task<int> RunAsync(CommandLineOptions cli, Theme theme, CancellationToken cancellationToken)
    {
        var workspaceRoot = ConfigurationLoader.DiscoverWorkspaceRoot(
            cli.WorkingDirectory is { Length: > 0 } d ? Path.GetFullPath(d) : Environment.CurrentDirectory);

        return cli.Subcommand switch
        {
            "config" => await ConfigAsync(cli, workspaceRoot, theme, cancellationToken).ConfigureAwait(false),
            "mcp" => Mcp(workspaceRoot, theme),
            "sessions" => await SessionsAsync(cli, workspaceRoot, theme, cancellationToken).ConfigureAwait(false),
            "doctor" => await DoctorAsync(workspaceRoot, theme, cancellationToken).ConfigureAwait(false),
            _ => 1,
        };
    }

    private static async Task<int> ConfigAsync(
        CommandLineOptions cli,
        string workspaceRoot,
        Theme theme,
        CancellationToken cancellationToken)
    {
        var action = cli.SubcommandArgs.FirstOrDefault()?.ToLowerInvariant() ?? "show";
        var projectSettings = Path.Combine(ConfigurationLoader.WorkspaceHome(workspaceRoot), ConfigurationLoader.SettingsFileName);

        switch (action)
        {
            case "path":
                AnsiConsole.WriteLine(Path.Combine(ConfigurationLoader.UserHome, ConfigurationLoader.SettingsFileName));
                AnsiConsole.WriteLine(projectSettings);
                return 0;

            case "init":
                {
                    if (File.Exists(projectSettings))
                    {
                        AnsiConsole.MarkupLine($"[{theme.Warning}]{Markup.Escape(projectSettings)} already exists — leaving it alone.[/]");
                        return 0;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(projectSettings)!);
                    await File.WriteAllTextAsync(projectSettings, SampleSettings, cancellationToken).ConfigureAwait(false);

                    AnsiConsole.MarkupLine($"[{theme.Success}]Created {Markup.Escape(projectSettings)}[/]");
                    AnsiConsole.MarkupLine($"[{theme.Muted}]Put secrets in settings.local.json or in environment variables — not in this file.[/]");
                    return 0;
                }

            default:
                {
                    var options = ConfigurationLoader.Load(workspaceRoot);
                    var profile = options.ResolveActiveProfile();

                    var grid = new Grid().AddColumn(new GridColumn().PadRight(3)).AddColumn();
                    grid.AddRow($"[{theme.Muted}]Workspace[/]", Markup.Escape(workspaceRoot));
                    grid.AddRow($"[{theme.Muted}]User settings[/]", Markup.Escape(Path.Combine(ConfigurationLoader.UserHome, ConfigurationLoader.SettingsFileName)));
                    grid.AddRow($"[{theme.Muted}]Project settings[/]", Markup.Escape(projectSettings));
                    grid.AddRow($"[{theme.Muted}]Active provider[/]", Markup.Escape(profile?.Name ?? "(none configured)"));
                    grid.AddRow($"[{theme.Muted}]Model[/]", Markup.Escape(profile?.Model ?? "-"));
                    grid.AddRow($"[{theme.Muted}]Permission mode[/]", options.PermissionMode.ToString());
                    grid.AddRow($"[{theme.Muted}]Providers[/]", Markup.Escape(string.Join(", ", options.Providers.Keys)));

                    AnsiConsole.Write(new Panel(grid)
                    {
                        Header = new PanelHeader(" Configuration ", Justify.Left),
                        Border = BoxBorder.Rounded,
                        BorderStyle = new Style(theme.AccentColor),
                    });

                    return 0;
                }
        }
    }

    private static int Mcp(string workspaceRoot, Theme theme)
    {
        var options = ConfigurationLoader.Load(workspaceRoot);

        if (options.McpServers.Count == 0)
        {
            AnsiConsole.MarkupLine($"[{theme.Muted}]No MCP servers configured.[/]");
            AnsiConsole.MarkupLine($"[{theme.Muted}]Add them under \"mcpServers\" in .autocode/settings.json.[/]");
            return 0;
        }

        foreach (var (name, server) in options.McpServers)
        {
            var target = server.Transport.Equals("http", StringComparison.OrdinalIgnoreCase)
                ? server.Url ?? "(no url)"
                : $"{server.Command} {string.Join(' ', server.Args)}".Trim();

            AnsiConsole.MarkupLine(
                $"[{(server.Disabled ? theme.Muted : theme.Accent)}]●[/] [{theme.ToolName}]{Markup.Escape(name)}[/] " +
                $"[{theme.Muted}]{server.Transport} · {Markup.Escape(target)}{(server.Disabled ? " (disabled)" : "")}[/]");
        }

        return 0;
    }

    private static async Task<int> SessionsAsync(
        CommandLineOptions cli,
        string workspaceRoot,
        Theme theme,
        CancellationToken cancellationToken)
    {
        var store = new SessionStore(workspaceRoot);
        var action = cli.SubcommandArgs.FirstOrDefault()?.ToLowerInvariant() ?? "list";

        if (action == "rm" && cli.SubcommandArgs.Count > 1)
        {
            store.Delete(cli.SubcommandArgs[1]);
            AnsiConsole.MarkupLine($"[{theme.Success}]Deleted session {Markup.Escape(cli.SubcommandArgs[1])}.[/]");
            return 0;
        }

        var sessions = await store.ListAsync(30, cancellationToken).ConfigureAwait(false);

        if (sessions.Count == 0)
        {
            AnsiConsole.MarkupLine($"[{theme.Muted}]No saved sessions for {Markup.Escape(workspaceRoot)}.[/]");
            return 0;
        }

        var table = new Table().Border(TableBorder.None)
            .AddColumn(new TableColumn("").PadRight(2))
            .AddColumn(new TableColumn("").PadRight(2))
            .AddColumn("");

        foreach (var session in sessions)
        {
            table.AddRow(
                $"[{theme.ToolName}]{Markup.Escape(session.Id)}[/]",
                $"[{theme.Muted}]{session.UpdatedAt.LocalDateTime:yyyy-MM-dd HH:mm}[/]",
                $"[{theme.Muted}]{Markup.Escape(session.Title)}[/]");
        }

        AnsiConsole.Write(table);
        return 0;
    }

    /// <summary>Checks that the environment and the configured provider actually work.</summary>
    private static async Task<int> DoctorAsync(string workspaceRoot, Theme theme, CancellationToken cancellationToken)
    {
        var options = ConfigurationLoader.Load(workspaceRoot);
        var failures = 0;

        void Check(bool ok, string label, string detail)
        {
            if (!ok) failures++;

            AnsiConsole.MarkupLine(
                $"[{(ok ? theme.Success : theme.Error)}]{(ok ? "✔" : "✗")}[/] {Markup.Escape(label)} [{theme.Muted}]{Markup.Escape(detail)}[/]");
        }

        Check(true, "Runtime", $".NET {Environment.Version}");
        Check(Directory.Exists(workspaceRoot), "Workspace", workspaceRoot);
        Check(options.Providers.Count > 0, "Providers", options.Providers.Count > 0
            ? string.Join(", ", options.Providers.Keys)
            : "none configured — run 'autocode config init'");

        var profile = options.ResolveActiveProfile();

        if (profile is null)
        {
            AnsiConsole.MarkupLine($"\n[{theme.Error}]No provider could be resolved.[/]");
            return 1;
        }

        Check(profile.ResolveApiKey() is not null, $"API key for '{profile.Name}'",
            profile.ResolveApiKey() is not null ? "present" : $"missing ({profile.ApiKey})");

        Check(!string.IsNullOrWhiteSpace(profile.Model), "Model", profile.Model);

        if (options.EnableSemanticIndex)
            CheckEmbeddings(options, profile, Check);

        // A real round trip: everything above can be right while the endpoint is still unreachable.
        try
        {
            using var client = ProviderFactory.CreateChatClient(profile);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));

            var response = await client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "Reply with the single word: ok")],
                new ChatOptions { MaxOutputTokens = 16 },
                timeout.Token).ConfigureAwait(false);

            Check(response.Text.Length > 0, "Provider round trip", $"replied \"{response.Text.Trim()}\"");
        }
        catch (Exception ex)
        {
            Check(false, "Provider round trip", ex.Message);
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(failures == 0
            ? $"[{theme.Success}]Everything checks out.[/]"
            : $"[{theme.Error}]{failures} check(s) failed.[/]");

        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// Verifies the embedding backend can actually be constructed. A file path that does not exist
    /// or a model that is not pulled only surfaces when the index is first built, which is usually
    /// long after the user configured it.
    /// </summary>
    private static void CheckEmbeddings(
        AutoCodeOptions options,
        ProviderProfile profile,
        Action<bool, string, string> check)
    {
        var backend = options.Embeddings.Resolve(profile);

        if (backend.Kind == EmbeddingProviderKind.None)
        {
            check(false, "Embeddings",
                $"the semantic index is on but provider '{profile.Name}' supplies no embeddings — set embeddings.kind");
            return;
        }

        try
        {
            using var generator = backend.Kind == EmbeddingProviderKind.Onnx
                ? Providers.Onnx.OnnxEmbeddingGenerator.Create(backend)
                : EmbeddingFactory.Create(backend);

            check(generator is not null, "Embeddings", backend.Describe());
        }
        catch (Exception ex)
        {
            check(false, "Embeddings", ex.Message);
        }
    }

    private const string SampleSettings =
        """
        {
          "$comment": "Auto Code project settings — Gravicode Studios (Kang Fadhil). Secrets belong in settings.local.json or env vars.",

          "activeProvider": "openai",

          "providers": {
            "openai": {
              "model": "gpt-4.1",
              "apiKey": "env:OPENAI_API_KEY"
            },
            "ollama": {
              "model": "qwen2.5-coder:14b",
              "endpoint": "http://localhost:11434/v1"
            }
          },

          "permissionMode": "ask",
          "permissions": {
            "allow": ["Read", "Glob", "Grep", "List", "Bash(git status)", "Bash(git diff:*)"],
            "deny": ["Bash(rm -rf:*)", "Write(.env)", "Read(.env)"]
          },

          "verifyCommands": ["dotnet build", "dotnet test"],

          "enableSemanticIndex": false,
          "embeddings": {
            "$comment": "Kind: Auto follows the chat provider. Ollama needs a local server; Onnx needs no server at all.",
            "kind": "Auto",
            "model": "nomic-embed-text",
            "endpoint": "http://localhost:11434"
          },

          "language": "en",

          "mcpServers": {},

          "hooks": {},

          "teams": {}
        }
        """;
}
