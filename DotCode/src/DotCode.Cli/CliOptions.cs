using DotCode.Engine.Agent;

namespace DotCode.Cli;

/// <summary>Parsed command line. Flag names mirror Claude Code's CLI so muscle memory and scripts carry over.</summary>
public sealed class CliOptions
{
    public string? Command { get; set; }
    public List<string> CommandArgs { get; } = [];
    public string? Prompt { get; set; }
    public bool Print { get; set; }
    public string OutputFormat { get; set; } = "text";
    public string InputFormat { get; set; } = "text";
    public bool Continue { get; set; }
    public bool Resume { get; set; }
    public string? ResumeId { get; set; }
    public bool ForkSession { get; set; }
    public string? Theme { get; set; }
    public bool Version { get; set; }
    public bool Help { get; set; }
    public bool IncludePartialMessages { get; set; }
    public RuntimeOptions Runtime { get; } = new();

    public static CliOptions Parse(string[] args)
    {
        var o = new CliOptions();
        var positional = new List<string>();
        var subcommands = new HashSet<string> { "serve", "mcp", "plugin", "plugins", "config", "models", "doctor", "sessions", "update", "version", "help", "theme", "audit" };
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"Missing value for {a}");
            string? Optional() => i + 1 < args.Length && !args[i + 1].StartsWith('-') ? args[++i] : null;
            List<string> Many()
            {
                var list = new List<string>();
                while (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)) list.Add(args[++i]);
                return list;
            }

            // --flag=value form
            if (a.StartsWith("--", StringComparison.Ordinal) && a.Contains('='))
            {
                var eq = a.IndexOf('=');
                var rest = new List<string>(args[..i]) { a[..eq], a[(eq + 1)..] };
                rest.AddRange(args[(i + 1)..]);
                args = [.. rest];
                a = args[i];
            }

            switch (a)
            {
                case "-p" or "--print": o.Print = true; break;
                case "-c" or "--continue": o.Continue = true; break;
                case "-r" or "--resume": o.Resume = true; o.ResumeId = Optional(); break;
                case "--fork-session": o.ForkSession = true; break;
                case "-v" or "--version": o.Version = true; break;
                case "-h" or "--help": o.Help = true; break;
                case "--output-format": o.OutputFormat = Next(); break;
                case "--input-format": o.InputFormat = Next(); break;
                case "--include-partial-messages": o.IncludePartialMessages = true; break;
                case "--model" or "-m": o.Runtime.Model = Next(); break;
                case "--fallback-model": o.Runtime.FallbackModel = Next(); break;
                case "--permission-mode": o.Runtime.PermissionMode = Next(); break;
                case "--dangerously-skip-permissions" or "--yolo": o.Runtime.DangerouslySkipPermissions = true; break;
                case "--allow-dangerously-skip-permissions": o.Runtime.AllowDangerouslySkipPermissions = true; break;
                case "--allowedTools" or "--allowed-tools": o.Runtime.AllowedTools.AddRange(Many()); break;
                case "--disallowedTools" or "--disallowed-tools": o.Runtime.DisallowedTools.AddRange(Many()); break;
                case "--tools": o.Runtime.Tools = [.. Many().SelectMany(x => x.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))]; break;
                case "--add-dir": o.Runtime.AddDirs.AddRange(Many()); break;
                case "--system-prompt": o.Runtime.SystemPrompt = Next(); break;
                case "--system-prompt-file": o.Runtime.SystemPrompt = File.ReadAllText(Next()); break;
                case "--append-system-prompt": o.Runtime.AppendSystemPrompt = Next(); break;
                case "--append-system-prompt-file": o.Runtime.AppendSystemPrompt = File.ReadAllText(Next()); break;
                case "--settings":
                    var s = Next();
                    if (s.TrimStart().StartsWith('{')) o.Runtime.SettingsJson = s; else o.Runtime.SettingsPath = s;
                    break;
                case "--mcp-config": o.Runtime.McpConfigs.AddRange(Many()); break;
                case "--strict-mcp-config": o.Runtime.StrictMcpConfig = true; break;
                case "--no-mcp": o.Runtime.NoMcp = true; break;
                case "--max-turns": o.Runtime.MaxTurns = int.Parse(Next()); break;
                case "--effort": o.Runtime.Effort = Next(); break;
                case "--output-style": o.Runtime.OutputStyle = Next(); break;
                case "--theme": o.Theme = Next(); break;
                case "--verbose": o.Runtime.Verbose = true; break;
                case "-d" or "--debug": o.Runtime.Debug = true; break;
                case "--no-session-persistence": o.Runtime.PersistSession = false; break;
                case "--record": o.Runtime.RecordTo = Next(); break;
                case "--cwd": o.Runtime.Cwd = Path.GetFullPath(Next()); break;
                default:
                    // A subcommand owns every argument after it (e.g. "mcp add --scope project ...").
                    if (!a.StartsWith('-') && positional.Count == 0 && !o.Print && subcommands.Contains(a))
                    {
                        o.Command = a;
                        o.CommandArgs.AddRange(args[(i + 1)..]);
                        return o;
                    }
                    if (a.StartsWith('-') && a.Length > 1) throw new ArgumentException($"Unknown option: {a}. Run 'dotcode --help'.");
                    positional.Add(a);
                    break;
            }
        }
        if (positional.Count > 0 && o.Command is null && subcommands.Contains(positional[0]) && !o.Print)
        {
            o.Command = positional[0];
            o.CommandArgs.AddRange(positional.Skip(1));
        }
        else if (positional.Count > 0)
        {
            o.Prompt = string.Join(' ', positional);
        }
        return o;
    }

    public const string HelpText = """
        Usage: dotcode [options] [command] [prompt]

        DotCode — agentic coding in your terminal with any LLM.
        Built by Gravicode Studios, led by Kang Fadhil.

        Starts an interactive session by default; use -p/--print for non-interactive output.

        Arguments:
          prompt                                   Your prompt

        Options:
          -p, --print                              Print response and exit (useful for pipes)
          --output-format <format>                 text (default) | json | stream-json   (with --print)
          --input-format <format>                  text (default) | stream-json          (with --print)
          -c, --continue                           Continue the most recent conversation
          -r, --resume [sessionId]                 Resume a conversation (picker when no id)
          --fork-session                           When resuming, create a new session id
          -m, --model <model>                      Model for the session: provider:model, alias (sonnet, gpt…) or role
          --fallback-model <model>                 Model to fall back to when the main model is overloaded
          --permission-mode <mode>                 default | acceptEdits | auto | plan | bypassPermissions
          --dangerously-skip-permissions           Bypass all permission checks (sandboxes/CI only!)
          --allow-dangerously-skip-permissions     Make bypass mode available in the Shift+Tab cycle
          --allowedTools <rules...>                Allow rules, e.g. "Bash(git *)" Edit
          --disallowedTools <rules...>             Deny rules, e.g. "Bash(rm *)"
          --tools <names...>                       Restrict the built-in tool set
          --add-dir <dirs...>                      Additional working directories
          --system-prompt <text>                   Replace the default system prompt
          --append-system-prompt <text>            Append to the default system prompt
          --settings <file-or-json>                Load extra settings (file path or inline JSON)
          --mcp-config <files...>                  Load MCP servers from JSON files or strings
          --strict-mcp-config                      Only use MCP servers from --mcp-config
          --max-turns <n>                          Limit agentic model calls per prompt
          --effort <level>                         Reasoning effort: off | low | medium | high | xhigh
          --output-style <name>                    default | explanatory | learning | custom
          --theme <name>                           UI theme (see 'dotcode theme list')
          --no-session-persistence                 Do not save the transcript
          --verbose                                Verbose output (full tool results)
          -v, --version                            Show version
          -h, --help                               Show help

        Commands:
          serve [--stdio|--port <n>]               Run the JSON-RPC agent server (used by the SDKs and IDEs)
          mcp list|add|add-json|get|remove         Manage MCP servers
          plugin install|list|remove|enable|disable|marketplace …   Manage plugins
          config list|get|set                      View or change settings
          models                                   List models from configured providers
          sessions                                 List recent sessions for this directory
          theme list|set <name>                    List or set the UI theme
          doctor                                   Check the installation and configuration
          audit verify|path [file]                 Verify the hash chain of the audit log
        """;
}
