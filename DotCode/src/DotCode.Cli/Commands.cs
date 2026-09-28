using System.Text.Json.Nodes;
using DotCode.Engine;
using DotCode.Engine.Agent;
using DotCode.Engine.Configuration;
using DotCode.Engine.Extensibility;
using DotCode.Engine.Mcp;
using DotCode.Engine.Sessions;
using DotCode.Engine.Util;

namespace DotCode.Cli;

/// <summary>Non-interactive subcommands: serve, mcp, plugin, config, models, sessions, theme, doctor.</summary>
public static class Commands
{
    public static async Task<int> RunAsync(string command, CliOptions options, CancellationToken ct)
    {
        var a = options.CommandArgs;
        var cwd = options.Runtime.Cwd;
        switch (command)
        {
            case "serve":
                return await DotCode.Protocol.AgentServer.RunAsync(options.Runtime, a, ct);

            case "mcp":
                return await Mcp(a, cwd, options, ct);

            case "plugin" or "plugins":
                return Plugin(a, cwd);

            case "config":
                return Config(a, cwd);

            case "audit":
                return Audit(a, cwd);

            case "theme":
                if (a.Count >= 2 && a[0] == "set")
                {
                    SettingsLoader.SetValue(DotCodePaths.UserSettings, "theme", JsonValue.Create(a[1]));
                    Console.WriteLine($"Theme set to {a[1]}");
                    return 0;
                }
                foreach (var t in DotCode.Tui.Themes.Theme.BuiltinNames()) Console.WriteLine(t);
                return 0;

            case "models":
            {
                await using var runtime = AgentRuntime.Create(options.Runtime, connectMcp: false);
                Console.WriteLine("Configured providers:");
                foreach (var (name, cfg) in runtime.Router.Providers.Where(p => p.Key != "mock"))
                    Console.WriteLine($"  {name,-16} type={cfg.Type}{(cfg.BaseUrl is { } u ? $"  url={u}" : "")}");
                Console.WriteLine($"\nMain model: {runtime.MainModelReference}");
                Console.WriteLine("\nAvailable models:");
                foreach (var m in await runtime.Router.ListAllModelsAsync(ct))
                    Console.WriteLine($"  {m.QualifiedId}");
                return 0;
            }

            case "sessions":
                foreach (var s in SessionStore.List(cwd))
                    Console.WriteLine($"{s.Id[..8]}  {s.Modified.LocalDateTime:yyyy-MM-dd HH:mm}  {s.MessageCount,4} msgs  {(s.Title ?? TextUtil.FirstLine(s.FirstPrompt, 60))}");
                return 0;

            case "doctor":
                return await Doctor(options, ct);

            default:
                Console.Error.WriteLine($"Unknown command: {command}");
                return 2;
        }
    }

    private static async Task<int> Mcp(List<string> a, string cwd, CliOptions options, CancellationToken ct)
    {
        var root = DotCodePaths.FindProjectRoot(cwd);
        var sub = a.FirstOrDefault() ?? "list";
        switch (sub)
        {
            case "add":
            {
                // dotcode mcp add [--scope user|project] [--transport stdio|http] [-e KEY=VAL] [-H "K: V"] <name> <command-or-url> [args...]
                var scope = "user";
                string transport = "stdio";
                var env = new Dictionary<string, string>();
                var headers = new Dictionary<string, string>();
                var rest = new List<string>();
                for (var i = 1; i < a.Count; i++)
                {
                    switch (a[i])
                    {
                        case "-s" or "--scope": scope = a[++i]; break;
                        case "-t" or "--transport": transport = a[++i]; break;
                        case "-e" or "--env":
                            var kv = a[++i].Split('=', 2);
                            env[kv[0]] = kv.Length > 1 ? kv[1] : "";
                            break;
                        case "-H" or "--header":
                            var hv = a[++i].Split(':', 2);
                            headers[hv[0].Trim()] = hv.Length > 1 ? hv[1].Trim() : "";
                            break;
                        case "--": rest.AddRange(a.Skip(i + 1)); i = a.Count; break;
                        default: rest.Add(a[i]); break;
                    }
                }
                if (rest.Count < 2) { Console.Error.WriteLine("Usage: dotcode mcp add [--scope user|project] [--transport stdio|http] <name> <command|url> [args...]"); return 2; }
                var config = transport is "http" or "sse"
                    ? new McpServerConfig { Type = transport, Url = rest[1], Headers = headers.Count > 0 ? headers : null }
                    : new McpServerConfig { Type = "stdio", Command = rest[1], Args = rest.Skip(2).ToList(), Env = env.Count > 0 ? env : null };
                var path = McpManager.AddServer(scope, root, rest[0], config);
                Console.WriteLine($"Added {transport} MCP server {rest[0]} to {scope} config ({path})");
                return 0;
            }
            case "add-json":
            {
                if (a.Count < 3) { Console.Error.WriteLine("Usage: dotcode mcp add-json [--scope user|project] <name> '<json>'"); return 2; }
                var scope = a.Contains("--scope") ? a[a.IndexOf("--scope") + 1] : "user";
                var args = a.Where((x, i) => x != "--scope" && (i == 0 || a[i - 1] != "--scope")).Skip(1).ToList();
                var cfg = System.Text.Json.JsonSerializer.Deserialize(args[1], SettingsJsonContext.Default.McpServerConfig)!;
                Console.WriteLine($"Added MCP server {args[0]} ({McpManager.AddServer(scope, root, args[0], cfg)})");
                return 0;
            }
            case "remove":
                if (a.Count < 2) { Console.Error.WriteLine("Usage: dotcode mcp remove <name>"); return 2; }
                Console.WriteLine(McpManager.RemoveServer(root, a[1]) ? $"Removed MCP server {a[1]}" : $"No MCP server named {a[1]}");
                return 0;
            case "list" or "get":
            {
                options.Runtime.NoMcp = true;
                await using var runtime = AgentRuntime.Create(options.Runtime, connectMcp: false);
                var configs = McpManager.CollectConfigs(runtime.Settings, runtime.ProjectRoot, runtime.Extensions.PluginMcpServers);
                foreach (var extra in options.Runtime.McpConfigs)
                {
                    var json = File.Exists(extra) ? File.ReadAllText(extra) : extra;
                    if (System.Text.Json.JsonSerializer.Deserialize(json, SettingsJsonContext.Default.McpConfigFile)?.McpServers is { } servers)
                        foreach (var (n, s) in servers) configs[n] = (s, "cli");
                }
                if (configs.Count == 0) { Console.WriteLine("No MCP servers configured. Use `dotcode mcp add` to add a server."); return 0; }
                Console.WriteLine("Checking MCP server health...\n");
                var manager = new McpManager();
                var selected = sub == "get" && a.Count > 1 ? configs.Where(c => c.Key == a[1]).ToDictionary(c => c.Key, c => c.Value) : configs;
                await manager.ConnectAllAsync(selected, cwd, ct);
                foreach (var s in manager.Servers)
                {
                    var target = s.Config.Url ?? $"{s.Config.Command} {string.Join(' ', s.Config.Args ?? [])}";
                    var status = s.Status switch
                    {
                        McpServerStatus.Connected => $"✓ Connected ({s.Client!.Tools.Count} tools)",
                        McpServerStatus.Disabled => "○ Disabled",
                        _ => $"✗ Failed: {s.Error}",
                    };
                    Console.WriteLine($"{s.Name} [{s.Scope}]: {target} - {status}");
                    if (sub == "get" && s.Client is not null)
                        foreach (var t in s.Client.Tools) Console.WriteLine($"    • {t.Name}: {TextUtil.FirstLine(t.Description, 90)}");
                }
                await manager.DisposeAsync();
                return 0;
            }
            default:
                Console.Error.WriteLine("Usage: dotcode mcp list|get <name>|add|add-json|remove");
                return 2;
        }
    }

    private static int Plugin(List<string> a, string cwd)
    {
        try
        {
            switch (a.FirstOrDefault() ?? "list")
            {
                case "install" or "add" when a.Count > 1:
                    var p = PluginManager.Install(a[1], cwd);
                    Console.WriteLine($"✓ Installed plugin {p.Name} v{p.Version} → {p.RootDir}");
                    return 0;
                case "remove" or "uninstall" when a.Count > 1:
                    Console.WriteLine(PluginManager.Uninstall(a[1]) ? $"Removed plugin {a[1]}" : $"Plugin {a[1]} is not installed");
                    return 0;
                case "enable" when a.Count > 1:
                    Console.WriteLine(PluginManager.SetEnabled(a[1], true) ? $"Enabled {a[1]}" : $"Plugin {a[1]} is not installed");
                    return 0;
                case "disable" when a.Count > 1:
                    Console.WriteLine(PluginManager.SetEnabled(a[1], false) ? $"Disabled {a[1]}" : $"Plugin {a[1]} is not installed");
                    return 0;
                case "marketplace":
                    var sub = a.Count > 1 ? a[1] : "list";
                    if (sub == "add" && a.Count > 2)
                    {
                        var m = PluginManager.AddMarketplace(a[2], cwd);
                        Console.WriteLine($"✓ Added marketplace {m.Name} with {m.Plugins.Count} plugins");
                        return 0;
                    }
                    if (sub == "remove" && a.Count > 2)
                    {
                        Console.WriteLine(PluginManager.RemoveMarketplace(a[2]) ? $"Removed marketplace {a[2]}" : "Not found");
                        return 0;
                    }
                    foreach (var mp in PluginManager.ListMarketplaces())
                    {
                        Console.WriteLine($"{mp.Name}  ({mp.Source})");
                        foreach (var e in mp.Plugins) Console.WriteLine($"    {e.Name,-24} {e.Description}");
                    }
                    return 0;
                default:
                    var installed = PluginManager.ListInstalled();
                    if (installed.Count == 0) Console.WriteLine("No plugins installed. Install with: dotcode plugin install <path|git-url|name@marketplace>");
                    foreach (var pl in installed)
                        Console.WriteLine($"{(pl.Enabled ? "●" : "○")} {pl.Id,-30} v{pl.Version,-8} {pl.Description}");
                    return 0;
            }
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    private static int Config(List<string> a, string cwd)
    {
        var sub = a.FirstOrDefault() ?? "list";
        var global = a.Contains("-g") || a.Contains("--global");
        var args = a.Where(x => x is not ("-g" or "--global")).ToList();
        var path = global ? DotCodePaths.UserSettings : DotCodePaths.ProjectSettings(DotCodePaths.FindProjectRoot(cwd));
        switch (sub)
        {
            case "set" when args.Count >= 3:
                JsonNode? value;
                try { value = JsonNode.Parse(args[2]); }
                catch (System.Text.Json.JsonException) { value = JsonValue.Create(args[2]); }
                SettingsLoader.SetValue(path, args[1], value);
                Console.WriteLine($"Set {args[1]} in {path}");
                return 0;
            case "get" when args.Count >= 2:
            {
                var loader = new SettingsLoader(cwd);
                var settings = loader.Load();
                var node = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(settings, SettingsJsonContext.Default.Settings));
                foreach (var part in args[1].Split('.')) node = node?[part];
                Console.WriteLine(node?.ToJsonString() ?? "(not set)");
                return 0;
            }
            default:
            {
                var loader = new SettingsLoader(cwd);
                var settings = loader.Load();
                Console.WriteLine("Settings sources (low → high precedence):");
                foreach (var s in loader.Sources) Console.WriteLine($"  {(s.Exists ? "●" : "○")} {s.Scope,-8} {s.Path}");
                foreach (var e in loader.Errors) Console.WriteLine($"  ! {e}");
                Console.WriteLine();
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(settings, SettingsJsonContext.Default.Settings));
                return 0;
            }
        }
    }

    private static int Audit(List<string> a, string cwd)
    {
        var sub = a.FirstOrDefault() ?? "verify";
        var file = a.Skip(1).FirstOrDefault();
        if (file is null)
        {
            var configured = new SettingsLoader(cwd).Load().Audit?.Path;
            file = DotCode.Engine.Observability.AuditLog.Create(new Settings { Audit = new AuditSettings { Enabled = true, Path = configured } })?.FilePath;
        }
        if (file is null) { Console.Error.WriteLine("error: no audit log path"); return 1; }
        if (sub == "path") { Console.WriteLine(file); return 0; }
        var result = DotCode.Engine.Observability.AuditLog.Verify(file);
        Console.WriteLine(result.Ok
            ? $"✓ {file}: {result.Message}"
            : $"✗ {file}: {(result.BrokenAtLine is { } l ? $"line {l}: " : "")}{result.Message} ({result.Entries} valid entries before it)");
        return result.Ok ? 0 : 1;
    }

    private static async Task<int> Doctor(CliOptions options, CancellationToken ct)
    {
        Console.WriteLine($"DotCode {AppInfo.Version} — {AppInfo.Credit}\n");
        Console.WriteLine($"  Runtime:        .NET {Environment.Version} ({System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier})");
        Console.WriteLine($"  Native AOT:     {(!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported ? "yes" : "no (JIT)")}");
        Console.WriteLine($"  Executable:     {Environment.ProcessPath}");
        Console.WriteLine($"  Config dir:     {DotCodePaths.UserDir}");
        Console.WriteLine($"  Bash:           {ProcessRunner.BashPath ?? "not found (Bash tool disabled)"}");
        Console.WriteLine($"  PowerShell:     {ProcessRunner.PowerShellPath}");
        Console.WriteLine($"  ripgrep:        {ProcessRunner.FindOnPath("rg") ?? "not found (managed search fallback)"}");
        Console.WriteLine($"  git:            {ProcessRunner.FindOnPath("git") ?? "not found"}");
        await using var runtime = AgentRuntime.Create(options.Runtime, connectMcp: false);
        Console.WriteLine($"  Project root:   {runtime.ProjectRoot}");
        Console.WriteLine($"  Main model:     {runtime.MainModelReference}");
        Console.WriteLine($"  Providers:      {string.Join(", ", runtime.Router.Providers.Keys.Where(k => k != "mock"))}");
        Console.WriteLine($"  Memory files:   {runtime.Memory.Count}");
        Console.WriteLine($"  Skills:         {runtime.Extensions.Skills.Count}   Commands: {runtime.Extensions.Commands.Count}   Agents: {runtime.Extensions.Agents.Count}   Plugins: {runtime.Extensions.Plugins.Count}");
        foreach (var e in runtime.Loader.Errors.Concat(runtime.Extensions.Errors)) Console.WriteLine($"  ! {e}");
        Console.WriteLine();
        try
        {
            var model = runtime.Router.Resolve(runtime.MainModelReference);
            Console.Write($"  Checking {model.Qualified} … ");
            var request = new DotCode.Abstractions.ModelRequest
            {
                Model = model.Model,
                Messages = [DotCode.Abstractions.Message.User("Reply with the single word: ok")],
                MaxOutputTokens = 2000,
            };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var text = new System.Text.StringBuilder();
            await foreach (var ev in model.Provider.StreamAsync(request, ct))
                if (ev is DotCode.Abstractions.TextDelta td) text.Append(td.Text);
            Console.WriteLine($"✓ {TextUtil.FirstLine(text.ToString().Trim(), 40)} ({sw.ElapsedMilliseconds}ms)");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"✗ {ex.Message}");
            return 1;
        }
    }
}
