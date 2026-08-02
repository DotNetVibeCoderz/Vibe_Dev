// Auto Code — Gravicode Studios (Kang Fadhil)

using AutoCode.Core.Permissions;

namespace AutoCode.Cli;

/// <summary>How results are written when Auto Code runs non-interactively.</summary>
public enum OutputFormat
{
    /// <summary>Plain assistant text.</summary>
    Text = 0,

    /// <summary>A single JSON object with the result and accounting.</summary>
    Json = 1,

    /// <summary>Newline-delimited JSON, one object per agent event, as they happen.</summary>
    StreamJson = 2,
}

/// <summary>
/// Parsed command line.
///
/// EN: parsed by hand rather than with a parser library. Auto Code's surface is small and fixed, and
/// every millisecond of startup is one the user waits through on a tool they invoke dozens of times a day.
/// ID: di-parse manual, bukan dengan pustaka parser, karena permukaan CLI-nya kecil dan waktu startup
/// penting untuk alat yang dipanggil puluhan kali sehari.
/// </summary>
public sealed class CommandLineOptions
{
    public string? InitialPrompt { get; private set; }
    public bool Print { get; private set; }
    public OutputFormat OutputFormat { get; private set; } = OutputFormat.Text;
    public bool Continue { get; private set; }
    public string? ResumeSessionId { get; private set; }
    public string? Model { get; private set; }
    public string? Provider { get; private set; }
    public PermissionMode? PermissionMode { get; private set; }
    public List<string> AllowedTools { get; } = [];
    public List<string> DisallowedTools { get; } = [];
    public string? WorkingDirectory { get; private set; }
    public string? Language { get; private set; }
    public bool ShowHelp { get; private set; }
    public bool ShowVersion { get; private set; }
    public bool NoContextFiles { get; private set; }
    public string? Subcommand { get; private set; }
    public List<string> SubcommandArgs { get; } = [];
    public List<string> Errors { get; } = [];

    /// <summary>
    /// Adopts piped stdin as the prompt, so Auto Code composes with other tools:
    /// <c>git diff | autocode -p "review this change"</c>.
    /// </summary>
    public void SetPromptFromStdin(string stdin)
    {
        if (!string.IsNullOrWhiteSpace(stdin))
            InitialPrompt = stdin.Trim();
    }

    public static CommandLineOptions Parse(string[] args)
    {
        var options = new CommandLineOptions();
        var positional = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            string? Next(string flag)
            {
                if (i + 1 < args.Length && !args[i + 1].StartsWith('-'))
                    return args[++i];

                options.Errors.Add($"{flag} requires a value.");
                return null;
            }

            switch (arg)
            {
                case "-h" or "--help":
                    options.ShowHelp = true;
                    break;

                case "-v" or "--version":
                    options.ShowVersion = true;
                    break;

                case "-p" or "--print":
                    options.Print = true;
                    break;

                case "-c" or "--continue":
                    options.Continue = true;
                    break;

                case "-r" or "--resume":
                    options.ResumeSessionId = Next(arg) ?? "";
                    break;

                case "--model":
                    options.Model = Next(arg);
                    break;

                case "--provider":
                    options.Provider = Next(arg);
                    break;

                case "--cwd" or "--add-dir":
                    options.WorkingDirectory = Next(arg);
                    break;

                case "--lang" or "--language":
                    options.Language = Next(arg);
                    break;

                case "--no-context":
                    options.NoContextFiles = true;
                    break;

                case "--output-format":
                    {
                        var value = Next(arg);
                        options.OutputFormat = value?.ToLowerInvariant() switch
                        {
                            "json" => OutputFormat.Json,
                            "stream-json" => OutputFormat.StreamJson,
                            "text" or null => OutputFormat.Text,
                            _ => Invalid(options, value),
                        };
                        break;
                    }

                case "--permission-mode":
                    {
                        var value = Next(arg);
                        options.PermissionMode = value?.ToLowerInvariant().Replace("-", "") switch
                        {
                            "ask" or "default" => Core.Permissions.PermissionMode.Ask,
                            "acceptedits" => Core.Permissions.PermissionMode.AcceptEdits,
                            "plan" => Core.Permissions.PermissionMode.Plan,
                            "bypasspermissions" or "yolo" => Core.Permissions.PermissionMode.BypassPermissions,
                            null => null,
                            _ => InvalidMode(options, value),
                        };
                        break;
                    }

                case "--allowed-tools" or "--allowedTools":
                    Split(Next(arg), options.AllowedTools);
                    break;

                case "--disallowed-tools" or "--disallowedTools":
                    Split(Next(arg), options.DisallowedTools);
                    break;

                case "--dangerously-skip-permissions":
                    options.PermissionMode = Core.Permissions.PermissionMode.BypassPermissions;
                    break;

                default:
                    if (arg.StartsWith('-'))
                        options.Errors.Add($"Unknown option '{arg}'.");
                    else
                        positional.Add(arg);
                    break;
            }
        }

        if (positional.Count > 0 && IsSubcommand(positional[0]))
        {
            options.Subcommand = positional[0].ToLowerInvariant();
            options.SubcommandArgs.AddRange(positional.Skip(1));
        }
        else if (positional.Count > 0)
        {
            options.InitialPrompt = string.Join(' ', positional);
        }

        return options;
    }

    private static bool IsSubcommand(string value) =>
        value.ToLowerInvariant() is "config" or "mcp" or "doctor" or "sessions";

    private static void Split(string? value, List<string> target)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        target.AddRange(value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static OutputFormat Invalid(CommandLineOptions options, string? value)
    {
        options.Errors.Add($"Unknown output format '{value}'. Use text, json or stream-json.");
        return OutputFormat.Text;
    }

    private static PermissionMode? InvalidMode(CommandLineOptions options, string? value)
    {
        options.Errors.Add($"Unknown permission mode '{value}'. Use ask, acceptEdits, plan or bypassPermissions.");
        return null;
    }

    public const string HelpText =
        """
        Auto Code — agentic coding assistant for the terminal
        Gravicode Studios · Kang Fadhil

        USAGE
          autocode [options] [prompt]
          autocode <command> [args]

        COMMANDS
          config [init|show|path]     Inspect or scaffold configuration
          mcp [list]                  Show configured MCP servers
          sessions [list|rm <id>]     Manage saved sessions
          doctor                      Check the installation and provider connectivity

        OPTIONS
          -p, --print                 Run once, print the result, exit
              --output-format <fmt>   text (default), json, stream-json
          -c, --continue              Resume the most recent session in this workspace
          -r, --resume <id>           Resume a specific session
              --model <id>            Override the model for this run
              --provider <name>       Override the provider profile for this run
              --permission-mode <m>   ask | acceptEdits | plan | bypassPermissions
              --allowed-tools <list>  Comma-separated tool allow-list
              --disallowed-tools <l>  Comma-separated tool deny-list
              --dangerously-skip-permissions
                                      Run every tool without prompting (sandboxes only)
              --cwd <path>            Workspace root to operate in
              --lang <en|id>          Language for the interface and replies
              --no-context            Skip AUTOCODE.md / CLAUDE.md discovery
          -h, --help                  Show this help
          -v, --version               Show the version

        EXAMPLES
          autocode
          autocode "why does the build fail on CI?"
          autocode -p "list every TODO in src" --output-format json
          autocode --provider ollama --model qwen2.5-coder:14b
          autocode --permission-mode plan "how would you add OAuth here?"
        """;
}
