using System.Text;
using System.Text.Json.Nodes;
using DotCode.Abstractions;
using DotCode.Engine;
using DotCode.Engine.Agent;
using DotCode.Engine.Configuration;
using DotCode.Engine.Extensibility;
using DotCode.Engine.Mcp;
using DotCode.Engine.Permissions;
using DotCode.Engine.Sessions;
using DotCode.Engine.Util;
using DotCode.Tui.Components;
using DotCode.Tui.Rendering;
using DotCode.Tui.Themes;

namespace DotCode.Tui;

internal sealed partial class App
{
    private static readonly (string Name, string Description, bool NeedsArgs)[] Builtins =
    [
        ("add-dir", "Add a new working directory", true),
        ("agents", "List available subagents", false),
        ("bashes", "List and manage background shells", false),
        ("clear", "Clear conversation history and free up context", false),
        ("compact", "Clear history but keep a summary in context. Optional: /compact [instructions]", false),
        ("config", "Open settings (theme, glyphs, spinner, verbosity, auto-compact…)", false),
        ("context", "Visualize current context usage as a colored grid", false),
        ("cost", "Show the total cost and duration of the current session", false),
        ("doctor", "Diagnose and verify your DotCode installation and settings", false),
        ("effort", "Set reasoning effort: off | low | medium | high | xhigh", false),
        ("exit", "Exit the REPL", false),
        ("export", "Export the current conversation to a markdown file", false),
        ("help", "Show help and available commands", false),
        ("hooks", "Show configured hooks", false),
        ("init", "Initialize a new DOTCODE.md file with codebase documentation", false),
        ("mcp", "Manage MCP servers (status, tools, reconnect)", false),
        ("memory", "Show memory files; /memory add <text> to append", false),
        ("model", "Set the model for this session (any configured provider)", false),
        ("output-style", "Set the output style: default | explanatory | learning", false),
        ("permissions", "Manage allow & deny tool permission rules", false),
        ("plugin", "Manage plugins: list | install <src> | remove <name> | marketplace add <src>", false),
        ("resume", "Resume a conversation", false),
        ("review", "Review the current code changes", false),
        ("sandbox", "Show the shell sandbox status; /sandbox on | off to toggle it for this project", false),
        ("rewind", "Restore the code and/or conversation to a previous point", false),
        ("security-review", "Complete a security review of the pending changes", false),
        ("skills", "List available skills", false),
        ("status", "Show DotCode status: version, model, account, connectivity, tools", false),
        ("theme", "Change the theme (colors, glyphs, spinner)", false),
        ("todos", "List current todo items", false),
        ("vim", "Toggle vim keybindings for the prompt (esc = NORMAL, i = INSERT)", false),
        ("about", "About DotCode — Gravicode Studios", false),
    ];

    private IEnumerable<(string Name, string Description, bool NeedsArgs)> AllCommands()
    {
        foreach (var b in Builtins) yield return b;
        foreach (var (name, cmd) in _runtime.Extensions.Commands)
            yield return (name, $"{cmd.Description} ({cmd.Scope.ToString().ToLowerInvariant()}{(cmd.PluginName is null ? "" : ":" + cmd.PluginName)})", cmd.ArgumentHint is not null);
        foreach (var (name, skill) in _runtime.Extensions.Skills)
            yield return (name, $"{TextUtil.FirstLine(skill.Description, 70)} (skill)", false);
        foreach (var s in _runtime.Mcp.Servers.Where(s => s.Client is not null))
            foreach (var p in s.Client!.Prompts)
                yield return ($"mcp__{s.Name}__{p.Name}", $"{p.Description} (MCP)", p.Arguments.Count > 0);
    }

    private bool TryBuiltinCommand(string name, string args, string raw)
    {
        var w = _screen.ContentWidth;
        var t = _theme;
        void Echo() => Commit(_blocks.User(raw, w));
        void Out(IEnumerable<string> lines)
        {
            var list = lines.ToList();
            for (var i = 0; i < list.Count; i++) list[i] = (i == 0 ? _blocks.ResultPrefix(true) : "     ") + list[i];
            Commit(list, spacing: false);
        }

        switch (name.ToLowerInvariant())
        {
            case "exit" or "quit":
                _exit = true;
                return true;

            case "clear" or "reset" or "new":
                _session.Clear();
                _screen.ClearScreen();
                _anyCommitted = false;
                Commit(Banner.Render(t, _session, w), spacing: false);
                return true;

            case "help":
                Echo();
                var help = new List<string>
                {
                    t.C(t.B($"DotCode v{AppInfo.Version}"), t.Brand) + t.Dim(" — " + AppInfo.Credit),
                    "",
                    "Always review DotCode's responses, especially when running code. DotCode has read access to files",
                    "in the current directory and can run commands and edit files with your permission.",
                    "",
                    t.B("Usage modes:"),
                    $"  • REPL: {t.B("dotcode")} (interactive session)",
                    $"  • Non-interactive: {t.B("dotcode -p \"question\"")}   · SDK server: {t.B("dotcode serve")}",
                    "",
                    t.B("Common tasks:"),
                    "  • Ask questions about your codebase   > How does foo.cs work?",
                    "  • Edit files                          > Update bar.ts to …",
                    "  • Fix errors                          > dotnet build",
                    "  • Run commands                        > !npm test",
                    "",
                    t.B("Interactive mode commands:"),
                };
                foreach (var (n, d, _) in AllCommands().OrderBy(c => c.Name))
                    help.Add("  " + TextWidth.Pad(t.B("/" + n), 24) + t.Dim(" - " + TextUtil.FirstLine(d, w - 34)));
                help.Add("");
                help.Add(t.Dim("Keyboard: shift+tab modes · esc interrupt · esc esc rewind · ctrl+o transcript · ctrl+r history search · ctrl+t todos · ? shortcuts"));
                Out(help);
                return true;

            case "about":
                Echo();
                Out(
                [
                    t.C(t.B("DotCode"), t.Brand) + $" v{AppInfo.Version}",
                    "Agentic coding in your terminal with any LLM — a .NET 10 port of the Claude Code experience,",
                    "with a harness SDK for .NET, TypeScript, Python, Go and Java.",
                    "",
                    t.B(AppInfo.Credit),
                    t.Dim(AppInfo.CreditId),
                    t.Dim(AppInfo.Repository),
                ]);
                return true;

            case "compact":
                Echo();
                StartTurn(async ct => { await _session.CompactAsync(args.Length > 0 ? args : null, ct).ConfigureAwait(false); return (TurnResult?)null; }, showSpinner: true);
                return true;

            case "cost" or "usage":
                Echo();
                var cost = new List<string>
                {
                    t.Dim("Total cost:            ") + $"${_session.TotalCostUsd:0.0000}",
                    t.Dim("Total duration (API):  ") + TextUtil.FormatDuration(_session.ApiDuration),
                    t.Dim("Total duration (wall): ") + TextUtil.FormatDuration(_wall.Elapsed),
                    t.Dim("Total code changes:    ") + $"{_linesAdded} lines added, {_linesRemoved} lines removed",
                    t.Dim("Usage by model:"),
                };
                foreach (var (model, (usage, c)) in _session.UsageByModel)
                    cost.Add($"  {model}: {TextUtil.FormatTokens(usage.InputTokens)} input, {TextUtil.FormatTokens(usage.OutputTokens)} output, {TextUtil.FormatTokens(usage.CacheReadTokens)} cache read, {TextUtil.FormatTokens(usage.CacheWriteTokens)} cache write " + t.Dim($"(${c:0.0000})"));
                if (_session.UsageByModel.Count == 0) cost.Add(t.Dim("  (no API calls yet)"));
                Out(cost);
                return true;

            case "context":
                Echo();
                Out(ContextGrid(w - 6));
                return true;

            case "status":
                Echo();
                var mcpOk = _runtime.Mcp.Servers.Count(s => s.Status == McpServerStatus.Connected);
                Out(
                [
                    t.B("Version: ") + AppInfo.Version + t.Dim("  (" + AppInfo.Credit + ")"),
                    t.B("Session ID: ") + _session.Id,
                    t.B("cwd: ") + _runtime.Cwd,
                    t.B("Model: ") + _session.Model.Qualified + t.Dim($"  (context {TextUtil.FormatTokens(_session.Model.Capabilities.ContextWindow)}, effort {_session.Effort.ToString().ToLowerInvariant()})"),
                    t.B("Providers: ") + string.Join(", ", _runtime.Router.Providers.Keys.Where(k => k != "mock")),
                    t.B("Permission mode: ") + _session.Mode.ToSetting(),
                    t.B("Memory: ") + (_runtime.Memory.Count == 0 ? "none" : string.Join(", ", _runtime.Memory.Select(m => DotCodePaths.Display(m.Path, _runtime.Cwd)))),
                    t.B("MCP servers: ") + $"{mcpOk} connected, {_runtime.Mcp.Servers.Count - mcpOk} other",
                    t.B("Setting sources: ") + string.Join(", ", _runtime.Loader.Sources.Where(s => s.Exists).Select(s => s.Scope.ToString().ToLowerInvariant()).Distinct()),
                    t.B("Tools: ") + string.Join(", ", _session.GetTools().Select(x => x.Name).Take(40)),
                ]);
                return true;

            case "model":
                if (args.Length > 0)
                {
                    Echo();
                    try
                    {
                        _session.SetModel(args);
                        Out([$"Set model to {t.B(_session.Model.Qualified)}"]);
                    }
                    catch (InvalidOperationException ex) { Out([t.C(ex.Message, t.Error)]); }
                    return true;
                }
                OpenModelPicker();
                return true;

            case "effort":
                Echo();
                if (args.Length > 0)
                {
                    _session.Effort = args.ToLowerInvariant() switch
                    {
                        "off" or "none" => ReasoningEffort.Off,
                        "low" => ReasoningEffort.Low,
                        "high" => ReasoningEffort.High,
                        "xhigh" or "max" => ReasoningEffort.XHigh,
                        _ => ReasoningEffort.Medium,
                    };
                }
                Out([$"Reasoning effort: {t.B(_session.Effort.ToString().ToLowerInvariant())}" + (_session.Model.Capabilities.Reasoning == ReasoningSupport.None ? t.Dim(" (this model has no adjustable reasoning)") : "")]);
                return true;

            case "theme":
                OpenThemePicker();
                return true;

            case "config" or "settings":
                OpenConfig();
                return true;

            case "output-style":
                if (args.Length > 0)
                {
                    Echo();
                    _session.OutputStyle = args;
                    Out([$"Output style set to {t.B(args)}"]);
                    return true;
                }
                _modal = new SelectModal("Output style", _runtime.Extensions.OutputStyles.Values.Select(s => (s.Name, (string?)s.Description, (object?)s.Name)),
                    v =>
                    {
                        _session.OutputStyle = (string)v!;
                        SettingsLoader.SetValue(DotCodePaths.ProjectLocalSettings(_runtime.ProjectRoot), "outputStyle", JsonValue.Create((string)v!));
                        Commit([_blocks.ResultPrefix(true) + $"Output style set to {t.B((string)v!)}"]);
                        return true;
                    }, "This changes how DotCode communicates with you");
                return true;

            case "permissions" or "allowed-tools":
                Echo();
                var parts = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && parts[0] is "allow" or "deny" or "ask")
                {
                    var behavior = parts[0] switch { "deny" => PermissionBehavior.Deny, "ask" => PermissionBehavior.Ask, _ => PermissionBehavior.Allow };
                    _session.Permissions.AddSessionRule(parts[1], behavior);
                    SettingsLoader.AddPermissionRule(DotCodePaths.ProjectLocalSettings(_runtime.ProjectRoot), parts[0], parts[1]);
                    Out([$"Added {parts[0]} rule {t.B(parts[1])} (saved to .dotcode/settings.local.json)"]);
                    return true;
                }
                if (parts.Length == 2 && parts[0] == "remove")
                {
                    _session.Permissions.RemoveRule(parts[1]);
                    Out([$"Removed rule {parts[1]} for this session"]);
                    return true;
                }
                var perm = new List<string> { t.B("Permission mode: ") + _session.Mode.ToSetting(), "" };
                void Rules(string title, IReadOnlyList<PermissionRule> rules, Rgb color)
                {
                    perm.Add(t.C(title, color));
                    if (rules.Count == 0) perm.Add(t.Dim("  (none)"));
                    foreach (var r in rules) perm.Add($"  {r}" + t.Dim($"  [{r.Source}]"));
                }
                Rules("Allow", _session.Permissions.AllowRules, t.Success);
                Rules("Ask", _session.Permissions.AskRules, t.Warning);
                Rules("Deny", _session.Permissions.DenyRules, t.Error);
                perm.Add("");
                perm.Add(t.Dim("Working directories: " + string.Join(", ", _session.Permissions.WorkingDirectories)));
                perm.Add(t.Dim("Usage: /permissions allow|ask|deny <Rule>   ·   /permissions remove <Rule>"));
                Out(perm);
                return true;

            case "add-dir":
                Echo();
                if (args.Length == 0) { Out([t.C("Usage: /add-dir <path>", t.Error)]); return true; }
                var dir = DotCodePaths.Resolve(args, _runtime.Cwd);
                if (!Directory.Exists(dir)) { Out([t.C($"Directory not found: {dir}", t.Error)]); return true; }
                _session.Permissions.AddWorkingDirectory(dir);
                Out([$"Added {t.B(dir)} as a working directory for this session"]);
                return true;

            case "init":
                Echo();
                StartTurn(ct => _session.RunTurnAsync(InitPrompt, null, ct));
                return true;

            case "review":
                Echo();
                StartTurn(ct => _session.RunTurnAsync($"Review the current code changes in this repository. Run `git status` and `git diff` (and `git diff --staged`){(args.Length > 0 ? $" focusing on: {args}" : "")}. Report correctness bugs, risky changes, missing tests and simplifications, most severe first, with file:line references. Be concise; do not modify files.", null, ct));
                return true;

            case "security-review":
                Echo();
                StartTurn(ct => _session.RunTurnAsync("Perform a security review of the pending changes on the current branch (use git diff against the main branch and read the affected code). Look for injection, auth/authz flaws, secrets in code, unsafe deserialization, path traversal, SSRF, insecure crypto and dependency risks. For each finding give severity, file:line, exploit scenario and fix. Do not modify files.", null, ct));
                return true;

            case "memory":
                Echo();
                if (args.StartsWith("add ", StringComparison.Ordinal))
                {
                    var path = Engine.Context.MemoryLoader.AppendMemory(_runtime.ProjectRoot, args[4..], user: false);
                    _runtime.Reload();
                    Out([$"Saved to {DotCodePaths.Display(path, _runtime.Cwd)}"]);
                    return true;
                }
                var mem = new List<string> { t.B("Memory files") + t.Dim(" (loaded into every conversation)") };
                if (_runtime.Memory.Count == 0) mem.Add(t.Dim("  none — run /init to create DOTCODE.md"));
                foreach (var m in _runtime.Memory) mem.Add($"  {t.Dim($"[{m.Scope.ToString().ToLowerInvariant()}]")} {m.Path} {t.Dim($"({m.Content.Length} chars)")}");
                mem.Add("");
                mem.Add(t.Dim("Tip: start a prompt with # to add a memory, or /memory add <text>. Edit files directly for bigger changes."));
                Out(mem);
                return true;

            case "mcp":
                Echo();
                var sub = args.Split(' ', 2);
                if (sub[0] == "reconnect" && sub.Length > 1 && _runtime.Mcp.Servers.FirstOrDefault(s => s.Name == sub[1]) is { } server)
                {
                    StartTurn(async ct =>
                    {
                        if (server.Client is not null) await server.Client.DisposeAsync().ConfigureAwait(false);
                        await _runtime.Mcp.ConnectAsync(server, _runtime.Cwd, ct).ConfigureAwait(false);
                        _session.Emit(new NoticeEvent(NoticeLevel.Info, $"MCP server {server.Name}: {server.Status}{(server.Error is { } e ? " — " + e : "")}"));
                        return (TurnResult?)null;
                    }, showSpinner: true);
                    return true;
                }
                var mcp = new List<string> { t.B("MCP servers") };
                if (_runtime.Mcp.Servers.Count == 0) mcp.Add(t.Dim("  No MCP servers configured. Add one with: dotcode mcp add <name> <command> [args…]"));
                foreach (var s in _runtime.Mcp.Servers)
                {
                    var status = s.Status switch
                    {
                        McpServerStatus.Connected => t.C(t.Glyphs.Check + " connected", t.Success) + t.Dim($" · {s.Client!.Tools.Count} tools{(s.Client.Prompts.Count > 0 ? $" · {s.Client.Prompts.Count} prompts" : "")}"),
                        McpServerStatus.Pending => t.C("… connecting", t.Warning),
                        McpServerStatus.Disabled => t.Dim("○ disabled"),
                        _ => t.C(t.Glyphs.Cross + " failed", t.Error) + t.Dim(" · " + s.Error),
                    };
                    mcp.Add($"  {t.B(s.Name)} {t.Dim($"[{s.Scope}]")} {status}");
                    if (_verbose && s.Client is not null)
                        foreach (var tool in s.Client.Tools) mcp.Add(t.Dim($"      • {tool.Name}: {TextUtil.FirstLine(tool.Description, w - 20)}"));
                }
                mcp.Add(t.Dim("/mcp reconnect <name> · ctrl+o then /mcp to list tools"));
                Out(mcp);
                return true;

            case "agents":
                Echo();
                var agents = new List<string> { t.B("Subagents") + t.Dim(" (the model delegates tasks with the Agent tool)") };
                foreach (var (n, a) in _runtime.Extensions.Agents)
                    agents.Add($"  {t.B(n)} {t.Dim($"[{a.Scope.ToString().ToLowerInvariant()}]")} {TextUtil.FirstLine(a.Description, w - n.Length - 20)}");
                agents.Add(t.Dim("Create your own in .dotcode/agents/<name>.md (frontmatter: name, description, tools, model)"));
                Out(agents);
                return true;

            case "skills":
                Echo();
                var skills = new List<string> { t.B("Skills") };
                if (_runtime.Extensions.Skills.Count == 0) skills.Add(t.Dim("  No skills installed. Add folders with SKILL.md to .dotcode/skills/ or ~/.dotcode/skills/"));
                foreach (var (n, s) in _runtime.Extensions.Skills)
                    skills.Add($"  {t.B(n)} {t.Dim($"[{s.Scope.ToString().ToLowerInvariant()}]")} {TextUtil.FirstLine(s.Description, w - n.Length - 20)}");
                Out(skills);
                return true;

            case "hooks":
                Echo();
                var hooks = new List<string> { t.B("Hooks") };
                if (_runtime.Hooks.Hooks.Count == 0) hooks.Add(t.Dim("  No hooks configured (settings.json → \"hooks\")"));
                foreach (var (ev, matchers) in _runtime.Hooks.Hooks)
                    foreach (var m in matchers)
                        foreach (var h in m.Hooks)
                            hooks.Add($"  {t.B(ev)} {t.Dim("[" + (m.Matcher ?? "*") + "]")} {h.Command}");
                Out(hooks);
                return true;

            case "plugin" or "plugins":
                Echo();
                var pa = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                try
                {
                    if (pa.Length >= 2 && pa[0] is "install" or "add")
                    {
                        var p = PluginManager.Install(pa[1], _runtime.Cwd);
                        _runtime.Reload();
                        Out([t.C($"{t.Glyphs.Check} Installed {p.Name} v{p.Version}", t.Success), t.Dim(p.Description)]);
                        return true;
                    }
                    if (pa.Length >= 2 && pa[0] is "remove" or "uninstall")
                    {
                        Out([PluginManager.Uninstall(pa[1]) ? $"Removed {pa[1]}" : $"{pa[1]} is not installed"]);
                        _runtime.Reload();
                        return true;
                    }
                    if (pa.Length >= 3 && pa[0] == "marketplace" && pa[1] == "add")
                    {
                        var m = PluginManager.AddMarketplace(pa[2], _runtime.Cwd);
                        Out([t.C($"{t.Glyphs.Check} Added marketplace {m.Name} ({m.Plugins.Count} plugins)", t.Success)]);
                        return true;
                    }
                    if (pa.Length >= 2 && pa[0] is "enable" or "disable")
                    {
                        PluginManager.SetEnabled(pa[1], pa[0] == "enable");
                        _runtime.Reload();
                        Out([$"{pa[0]}d {pa[1]}"]);
                        return true;
                    }
                }
                catch (InvalidOperationException ex)
                {
                    Out([t.C(ex.Message, t.Error)]);
                    return true;
                }
                var plugins = new List<string> { t.B("Plugins") };
                if (_runtime.Extensions.Plugins.Count == 0) plugins.Add(t.Dim("  No plugins installed"));
                foreach (var p in _runtime.Extensions.Plugins)
                    plugins.Add($"  {(p.Enabled ? t.C(t.Glyphs.Dot, t.Success) : t.Dim("○"))} {t.B(p.Id)} v{p.Version} {t.Dim(p.Description)}");
                foreach (var mp in PluginManager.ListMarketplaces())
                {
                    plugins.Add("");
                    plugins.Add(t.B($"Marketplace {mp.Name}") + t.Dim($" ({mp.Source})"));
                    foreach (var e in mp.Plugins) plugins.Add($"  {e.Name}@{mp.Name} {t.Dim(e.Description)}");
                }
                plugins.Add(t.Dim("/plugin install <path|git-url|name@marketplace> · /plugin remove <name> · /plugin marketplace add <src>"));
                Out(plugins);
                return true;

            case "resume":
                if (args.Length > 0 && SessionStore.FindPath(_runtime.Cwd, args) is { } rp) { SwitchSession(rp); return true; }
                OpenResumePicker();
                return true;

            case "rewind" or "checkpoint":
                OpenRewind();
                return true;

            case "todos":
                Echo();
                ShowTodos();
                return true;

            case "sandbox":
            {
                Echo();
                var arg = args.Trim().ToLowerInvariant();
                if (arg is "on" or "off")
                {
                    SettingsLoader.SetValue(DotCodePaths.ProjectLocalSettings(_runtime.ProjectRoot), "sandbox.enabled", JsonValue.Create(arg == "on"));
                    _runtime.Reload();
                }
                var sb = _runtime.Settings.Sandbox;
                var kind = Engine.Sandbox.ShellSandbox.Available;
                var enabled = sb?.Enabled == true;
                var lines = new List<string>
                {
                    $"Sandbox: {(enabled ? t.C("enabled", t.Success) : t.Dim("disabled"))} · mechanism: {t.B(kind.ToString())}"
                    + (Engine.Sandbox.ShellSandbox.IsolatesFileSystem(kind) ? "" : kind == Engine.Sandbox.SandboxKind.JobObject ? t.Dim(" (process containment only, no file-system isolation)") : t.Dim(" (Linux: install bubblewrap; macOS: sandbox-exec)")),
                };
                if (enabled)
                {
                    var policy = Engine.Sandbox.ShellSandbox.BuildPolicy(_session, sb!);
                    lines.Add($"Auto-allow sandboxed commands: {(sb!.AutoAllowBashIfSandboxed != false && Engine.Sandbox.ShellSandbox.IsolatesFileSystem(kind) ? "yes" : "no")} · network: {(policy.DenyNetwork ? "blocked" : "allowed")} · escape hatch: {(sb.AllowUnsandboxedCommands != false ? "asks" : "disabled")}");
                    lines.Add(t.Dim("Writable: " + string.Join(", ", policy.Writable.Take(6).Select(p => DotCodePaths.Display(p, _runtime.Cwd))) + (policy.Writable.Count > 6 ? $" +{policy.Writable.Count - 6} caches" : "")));
                    lines.Add(t.Dim("Hidden: " + string.Join(", ", policy.Hidden.Select(p => DotCodePaths.Display(p, _runtime.Cwd)))));
                }
                lines.Add(t.Dim($"/sandbox {(enabled ? "off" : "on")} to toggle (saved to .dotcode/settings.local.json)"));
                Out(lines);
                return true;
            }

            case "vim":
            {
                Echo();
                var enable = _vim is null;
                SettingsLoader.SetValue(DotCodePaths.UserSettings, "tui.vim", JsonValue.Create(enable));
                _runtime.Reload();
                ApplyUiSettings();
                Out([enable ? Input.UiText.Current.VimOn : Input.UiText.Current.VimOff]);
                return true;
            }

            case "bashes":
                Echo();
                var ba = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (ba.Length == 2 && ba[0] == "kill")
                {
                    Out([_runtime.BackgroundShells.Kill(ba[1]) ? $"Killed {ba[1]}" : $"No shell {ba[1]}"]);
                    return true;
                }
                var shells = _runtime.BackgroundShells.Shells;
                Out(shells.Count == 0 ? [t.Dim("No background shells")] : shells.Select(s => $"{t.B(s.Id)} {t.Dim(s.Status)} {TextUtil.FirstLine(s.Command, w - 30)}"));
                return true;

            case "export":
                Echo();
                var file = args.Length > 0 ? DotCodePaths.Resolve(args, _runtime.Cwd) : Path.Combine(_runtime.Cwd, $"dotcode-conversation-{DateTime.Now:yyyyMMdd-HHmmss}.md");
                File.WriteAllText(file, ExportMarkdown());
                Out([$"Conversation exported to {t.B(DotCodePaths.Display(file, _runtime.Cwd))}"]);
                return true;

            case "doctor":
                Echo();
                Out(
                [
                    $"DotCode {AppInfo.Version} · .NET {Environment.Version} · {System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier}",
                    $"Bash: {ProcessRunner.BashPath ?? t.C("not found", t.Warning)}",
                    $"PowerShell: {ProcessRunner.PowerShellPath}",
                    $"ripgrep: {ProcessRunner.FindOnPath("rg") ?? t.Dim("not found (managed search)")}",
                    $"Terminal: {_screen.Width}x{_screen.Height}, colors: {Ansi.Depth}",
                    $"Settings errors: {(_runtime.Loader.Errors.Count == 0 ? t.C("none", t.Success) : string.Join("; ", _runtime.Loader.Errors))}",
                ]);
                return true;
        }
        return false;
    }

    private const string InitPrompt = """
        Please analyze this codebase and create a DOTCODE.md file, which will be given to future instances of DotCode to operate in this repository.

        What to add:
        1. Commands that will be commonly used, such as how to build, lint, and run tests, including how to run a single test.
        2. High-level code architecture and structure so that future instances can be productive more quickly. Focus on the "big picture" architecture that requires reading multiple files to understand.

        Usage notes:
        - If there's already a DOTCODE.md (or CLAUDE.md / AGENTS.md), suggest improvements to it instead.
        - Do not repeat yourself and do not include obvious instructions or generic development practices.
        - Avoid listing every component or file structure that can be easily discovered.
        - If there are Cursor rules (.cursor/rules/ or .cursorrules) or Copilot rules (.github/copilot-instructions.md), include the important parts.
        - If there is a README.md, include the important parts.
        - Do not make up information that is not in the files you read.
        - Prefix the file with:

        ```
        # DOTCODE.md

        This file provides guidance to DotCode when working with code in this repository.
        ```
        """;

    private List<string> ContextGrid(int width)
    {
        var t = _theme;
        var window = _session.Model.Capabilities.ContextWindow;
        var tools = _session.GetTools();
        long ToolTokens(IEnumerable<Engine.Tools.Tool> ts) => ts.Sum(x => TextUtil.EstimateTokens(x.Description) + TextUtil.EstimateTokens(x.InputSchema.GetRawText()));
        var system = TextUtil.EstimateTokens(PromptBuilder.CoreInstructions) + 400;
        var builtinTools = ToolTokens(tools.Where(x => !x.Name.StartsWith("mcp__", StringComparison.Ordinal)));
        var mcpTools = ToolTokens(tools.Where(x => x.Name.StartsWith("mcp__", StringComparison.Ordinal)));
        var memory = _runtime.Memory.Sum(m => TextUtil.EstimateTokens(m.Content));
        var messages = PromptBuilder.WindowMessages(_session).Sum(TextUtil.EstimateTokens);
        var total = system + builtinTools + mcpTools + memory + messages;
        if (_session.LastContextTokens > total) { messages += _session.LastContextTokens - total; total = _session.LastContextTokens; }
        var reserve = (long)(window * (1 - (_runtime.Settings.AutoCompactThreshold ?? 0.85)));
        var free = Math.Max(0, window - total - reserve);

        var cats = new (string Name, long Tokens, Rgb Color)[]
        {
            ("System prompt", system, t.Secondary), ("System tools", builtinTools, t.Suggestion), ("MCP tools", mcpTools, t.Bash),
            ("Memory files", memory, t.PlanMode), ("Messages", messages, t.Brand), ("Free space", free, t.Subtle), ("Autocompact buffer", reserve, t.Warning),
        };
        const int cells = 100;
        var grid = new List<Rgb>();
        foreach (var c in cats)
        {
            var n = (int)Math.Round(c.Tokens * (double)cells / window);
            if (c.Tokens > 0 && n == 0 && c.Name != "Free space") n = 1;
            for (var i = 0; i < n && grid.Count < cells; i++) grid.Add(c.Color);
        }
        while (grid.Count < cells) grid.Add(t.Subtle);
        var full = t.Ascii ? "#" : "⛁";
        var empty = t.Ascii ? "." : "⛶";
        var lines = new List<string> { t.B("Context Usage") };
        for (var row = 0; row < 10; row++)
        {
            var sb = new StringBuilder("  ");
            for (var col = 0; col < 10; col++)
            {
                var color = grid[row * 10 + col];
                sb.Append(t.C(color == t.Subtle ? empty : full, color)).Append(' ');
            }
            if (row < cats.Length)
            {
                var c = cats[row];
                sb.Append("  ").Append(t.C(full, c.Color)).Append(' ').Append(c.Name).Append(": ").Append(TextUtil.FormatTokens(c.Tokens)).Append(t.Dim($" tokens ({c.Tokens * 100.0 / window:0.0}%)"));
            }
            else if (row == 8) sb.Append("  ").Append(t.Dim($"{_session.Model.Qualified} · {TextUtil.FormatTokens(total)}/{TextUtil.FormatTokens(window)} tokens ({total * 100.0 / window:0}%)"));
            lines.Add(sb.ToString());
        }
        return lines;
    }

    private string ExportMarkdown()
    {
        var sb = new StringBuilder();
        sb.Append("# DotCode conversation\n\n");
        sb.Append($"- Session: `{_session.Id}`\n- Model: `{_session.Model.Qualified}`\n- Directory: `{_runtime.Cwd}`\n- Exported: {DateTime.Now:yyyy-MM-dd HH:mm}\n\n---\n\n");
        foreach (var m in _session.Messages)
        {
            if (m.IsMeta) continue;
            if (m.Role == Role.User)
            {
                foreach (var r in m.ToolResults) sb.Append("<details><summary>Tool result</summary>\n\n```\n").Append(TextUtil.Truncate(r.TextContent, 4000)).Append("\n```\n</details>\n\n");
                if (m.Text.Length > 0) sb.Append("## User\n\n").Append(m.Text).Append("\n\n");
            }
            else
            {
                if (m.Text.Length > 0) sb.Append("## DotCode\n\n").Append(m.Text).Append("\n\n");
                foreach (var tu in m.ToolUses) sb.Append($"**Tool:** `{tu.Name}` `{TextUtil.FirstLine(tu.Input.GetRawText(), 300)}`\n\n");
            }
        }
        sb.Append("\n---\n*").Append(AppInfo.Credit).Append("*\n");
        return sb.ToString();
    }

    // ------------------------------------------------------------------ pickers

    private void OpenModelPicker()
    {
        var current = _session.Model.Qualified;
        var items = new List<(string, string?, object?)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string reference, string? desc)
        {
            if (!seen.Add(reference)) return;
            items.Add((reference + (reference == current ? " " + _theme.Glyphs.Check : ""), desc, reference));
        }
        Add(current, "current");
        foreach (var role in ModelRouter.Roles)
            if (_runtime.Settings.Models?.GetValueOrDefault(role) is { } m) Add(m, $"{role} role");
        foreach (var (name, cfg) in _runtime.Router.Providers.Where(p => p.Key != "mock"))
            foreach (var m in cfg.Models ?? []) Add($"{name}:{m}", cfg.Type);
        var modal = new SelectModal("Select model", items, v =>
        {
            try
            {
                _session.SetModel((string)v!);
                Commit([_blocks.ResultPrefix(true) + $"Set model to {_theme.B(_session.Model.Qualified)}"]);
            }
            catch (InvalidOperationException ex) { Commit(_blocks.ErrorBlock(ex.Message, _screen.ContentWidth)); }
            return true;
        }, "Switch between any configured provider and model. Type to filter; /model <provider:model> for anything else.") { Filterable = true };
        _modal = modal;
        // Fetch live model lists in the background and reopen with the full catalog.
        _ = Task.Run(async () =>
        {
            var all = await _runtime.Router.ListAllModelsAsync(CancellationToken.None).ConfigureAwait(false);
            if (all.Count == 0) return;
            Post(new ActionUiEvent(() =>
            {
                if (_modal != modal) return;
                foreach (var m in all.Take(300)) Add(m.QualifiedId, m.DisplayName ?? m.ProviderId);
                _modal = new SelectModal("Select model", items, v =>
                {
                    try
                    {
                        _session.SetModel((string)v!);
                        Commit([_blocks.ResultPrefix(true) + $"Set model to {_theme.B(_session.Model.Qualified)}"]);
                    }
                    catch (InvalidOperationException ex) { Commit(_blocks.ErrorBlock(ex.Message, _screen.ContentWidth)); }
                    return true;
                }, $"{items.Count} models across {_runtime.Router.Providers.Count - 1} providers. Type to filter.") { Filterable = true };
            }));
        });
    }

    private void OpenThemePicker()
    {
        var original = _theme;
        var names = Theme.All().ToList();
        var index = Math.Max(0, names.FindIndex(n => n.Name == original.Name));
        _modal = new SelectModal("Theme", names.Select(n => (n.Display + (n.Name == original.Name ? " " + original.Glyphs.Check : ""), (string?)n.Name, (object?)n.Name)),
            v =>
            {
                ApplyTheme((string)v!);
                SettingsLoader.SetValue(DotCodePaths.UserSettings, "theme", JsonValue.Create((string)v!));
                Commit([_blocks.ResultPrefix(true) + $"Theme set to {_theme.B(_theme.DisplayName)}"]);
                return true;
            },
            "Choose the text style that looks best with your terminal (↑/↓ previews live):", index,
            onHighlight: v => ApplyTheme((string)v!),
            preview: (t, width) => _blocks.Box(
            [
                ..new Blocks(t).DiffLines("@@ -1,3 +1,3 @@\n function greet() {\n-  console.log(\"Hello, World!\");\n+  console.log(\"Hello, DotCode!\");\n }", width - 8),
                "",
                t.C(t.Glyphs.Dot, t.Success) + " " + t.B("Read") + "(src/app.ts)   " + SpinnerLine.Render(t, 3, "Pondering", TimeSpan.FromSeconds(4), 1200, true, false, null, 40),
            ], width - 2, t.Subtle),
            onCancel: () => ApplyTheme(original.Name));
    }

    private void ApplyTheme(string name)
    {
        _theme = Theme.Load(name, _runtime.Settings.Tui);
        _blocks.Theme = _theme;
    }

    private void OpenConfig()
    {
        var tui = _runtime.Settings.Tui ?? new TuiSettings();
        var items = new List<(string, string?, object?)>
        {
            ("Theme", _theme.DisplayName, "theme"),
            ("Glyphs (font)", (tui.Glyphs ?? "unicode") + "  · unicode | ascii | nerd", "glyphs"),
            ("Spinner", (tui.Spinner ?? "claude") + "  · claude | dots | line | star | bounce | arc | dotnet", "spinner"),
            ("Input style", (tui.Border ?? "lines") + "  · lines | rounded | single | double | heavy", "border"),
            ("Reduced motion", (tui.ReducedMotion == true).ToString().ToLowerInvariant(), "reducedMotion"),
            ("Show thinking", (tui.ShowThinking == true).ToString().ToLowerInvariant(), "showThinking"),
            ("Show tips", (tui.ShowTips != false).ToString().ToLowerInvariant(), "showTips"),
            ("Auto-compact", (_runtime.Settings.AutoCompact != false).ToString().ToLowerInvariant(), "autoCompact"),
            ("Verbose output", _verbose.ToString().ToLowerInvariant(), "verbose"),
            ("Language", (tui.Language ?? "en") + "  · en | id | auto", "language"),
            ("Vim mode", (tui.Vim == true).ToString().ToLowerInvariant(), "vim"),
            ("Notifications", (tui.Notifications ?? "bell") + "  · bell | osc9 | osc777 | off", "notifications"),
            ("Output style", _session.OutputStyle ?? "default", "outputStyle"),
            ("Default permission mode", _runtime.Settings.Permissions?.DefaultMode ?? "default", "defaultMode"),
        };
        _modal = new SelectModal("Settings", items, v =>
        {
            var key = (string)v!;
            string Cycle(string? cur, string[] options) => options[(Array.IndexOf(options, cur ?? options[0]) + 1) % options.Length];
            switch (key)
            {
                case "theme": OpenThemePicker(); return false;
                case "glyphs": Save("tui.glyphs", Cycle(tui.Glyphs, ["unicode", "ascii", "nerd"])); break;
                case "spinner": Save("tui.spinner", Cycle(tui.Spinner, ["claude", "dots", "line", "star", "bounce", "arc", "dotnet"])); break;
                case "border": Save("tui.border", Cycle(tui.Border, ["lines", "rounded", "single", "double", "heavy"])); break;
                case "reducedMotion": Save("tui.reducedMotion", tui.ReducedMotion != true); break;
                case "showThinking": Save("tui.showThinking", tui.ShowThinking != true); break;
                case "showTips": Save("tui.showTips", tui.ShowTips == false); break;
                case "autoCompact": Save("autoCompact", _runtime.Settings.AutoCompact == false); break;
                case "verbose": _verbose = !_verbose; break;
                case "language": Save("tui.language", Cycle(tui.Language, ["en", "id", "auto"])); break;
                case "vim": Save("tui.vim", tui.Vim != true); break;
                case "notifications": Save("tui.notifications", Cycle(tui.Notifications, ["bell", "osc9", "osc777", "off"])); break;
                case "outputStyle": TryBuiltinCommand("output-style", "", "/output-style"); return false;
                case "defaultMode": Save("permissions.defaultMode", Cycle(_runtime.Settings.Permissions?.DefaultMode, ["default", "acceptEdits", "auto", "plan"])); break;
            }
            ApplyUiSettings();
            ApplyTheme(_theme.Name);
            OpenConfig();
            return false;
        }, $"Saved to {DotCodePaths.UserSettings}. Enter to change a value, Esc to close.");

        void Save(string key, object value)
        {
            SettingsLoader.SetValue(DotCodePaths.UserSettings, key, value is bool b ? JsonValue.Create(b) : JsonValue.Create((string)value));
            _runtime.Reload();
        }
    }

    private void OpenResumePicker()
    {
        var sessions = SessionStore.List(_runtime.Cwd, 50).Where(s => s.Id != _session.Id).ToList();
        if (sessions.Count == 0) { Flash("No previous sessions in this directory"); return; }
        _modal = new SelectModal("Resume Session",
            sessions.Select(s => (TextWidth.Truncate((s.Title ?? s.FirstPrompt).Replace('\n', ' '), 50), (string?)$"{Banner.Ago(s.Modified)} · {s.MessageCount} msgs{(s.GitBranch is { } b ? " · " + b : "")}", (object?)s.Path)),
            v => { SwitchSession((string)v!); return true; }, "Choose a conversation to resume") { Filterable = true };
    }

    private void SwitchSession(string path)
    {
        var old = _session;
        _session = _runtime.ResumeSession(path);
        _session.Sink = new DelegateEventSink(e => _events.Writer.TryWrite(new AgentUiEvent(e)));
        _session.Interaction = this;
        _ = old.DisposeAsync();
        _screen.ClearScreen();
        _anyCommitted = false;
        Commit(Banner.Render(_theme, _session, _screen.ContentWidth), spacing: false);
        ReplayTranscript();
    }

    private void OpenRewind()
    {
        if (_busy) return;
        var turns = _session.UserTurns().Reverse().Take(30).ToList();
        if (turns.Count == 0) { Flash("Nothing to rewind to"); return; }
        _modal = new SelectModal("Rewind",
            turns.Select(m => (TextWidth.Truncate(m.Text.Replace('\n', ' '), 60), (string?)(_session.Checkpoints.HasChanges(m.Id) ? "code changed" : m.Timestamp.LocalDateTime.ToString("HH:mm")), (object?)m)),
            v =>
            {
                var target = (Message)v!;
                var hasCode = _session.Messages.SkipWhile(m => m.Id != target.Id).Any(m => _session.Checkpoints.HasChanges(m.Id));
                _modal = new SelectModal("Confirm rewind",
                [
                    ("Restore code and conversation", hasCode ? "revert file changes made since then" : "no code changes to revert", "both"),
                    ("Restore conversation", "keep file changes", "conversation"),
                    ("Restore code", "keep the conversation", "code"),
                    ("Never mind", null, "none"),
                ], choice =>
                {
                    var c = (string)choice!;
                    if (c == "none") return true;
                    var (removed, restored) = _session.Rewind(target.Id, c is "both" or "conversation", c is "both" or "code");
                    if (c is "both" or "conversation") _input.SetText(target.Text);
                    Commit(_blocks.Notice(NoticeLevel.Info, $"Rewound: {removed} messages removed, {restored.Count} files restored", _screen.ContentWidth));
                    return true;
                }, $"Rewind to: \"{TextWidth.Truncate(target.Text.Replace('\n', ' '), 60)}\"");
                return false;
            }, "Restore the conversation and/or code to the point before a previous message");
    }
}
