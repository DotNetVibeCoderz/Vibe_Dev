using System.Text.Json;
using DotCode.Abstractions;
using DotCode.Engine.Configuration;

namespace DotCode.Engine.Extensibility;

public enum ExtensionScope { Builtin, User, Project, Plugin, Mcp }

/// <summary>Skill: a folder with SKILL.md (frontmatter name/description) loaded on demand via the Skill tool or /name.</summary>
public sealed record SkillDefinition(string Name, string Description, string Body, string BaseDir, ExtensionScope Scope, string? PluginName = null)
{
    public List<string>? AllowedTools { get; init; }
    public string? Model { get; init; }
    public string QualifiedName => PluginName is null ? Name : $"{PluginName}:{Name}";
}

/// <summary>Custom slash command: markdown prompt template with $ARGUMENTS / $1..$9 placeholders.</summary>
public sealed record CommandDefinition(string Name, string Description, string Body, string SourcePath, ExtensionScope Scope, string? PluginName = null)
{
    public string? ArgumentHint { get; init; }
    public List<string>? AllowedTools { get; init; }
    public string? Model { get; init; }
    public string QualifiedName => PluginName is null ? Name : $"{PluginName}:{Name}";
}

/// <summary>Subagent definition: system prompt + tool allowlist + model, from agents/*.md.</summary>
public sealed record AgentDefinition(string Name, string Description, string SystemPrompt, ExtensionScope Scope, string? PluginName = null)
{
    /// <summary>null = all tools (minus Agent).</summary>
    public List<string>? Tools { get; init; }
    public List<string>? DisallowedTools { get; init; }
    /// <summary>"inherit", a role (fast/planner/subagent) or provider:model.</summary>
    public string? Model { get; init; }
    public string? Color { get; init; }
    public string QualifiedName => PluginName is null ? Name : $"{PluginName}:{Name}";
}

public sealed record OutputStyleDefinition(string Name, string Description, string Prompt, bool KeepCodingInstructions, ExtensionScope Scope);

public sealed record PluginInfo(string Name, string Version, string Description, string RootDir, string? Marketplace, bool Enabled)
{
    public string Id => Marketplace is null ? Name : $"{Name}@{Marketplace}";
}

/// <summary>Loads skills, commands, agents, output styles and plugins from user, project and plugin directories
/// (both <c>.dotcode</c> and Claude Code's <c>.claude</c> layouts). Project definitions override user ones.</summary>
public sealed class ExtensionRegistry
{
    public Dictionary<string, SkillDefinition> Skills { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, CommandDefinition> Commands { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, AgentDefinition> Agents { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, OutputStyleDefinition> OutputStyles { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<PluginInfo> Plugins { get; } = [];
    /// <summary>Hooks contributed by enabled plugins (merged into the hook runner).</summary>
    public Dictionary<string, List<HookMatcher>> PluginHooks { get; } = new(StringComparer.Ordinal);
    /// <summary>MCP servers contributed by enabled plugins.</summary>
    public Dictionary<string, McpServerConfig> PluginMcpServers { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Errors { get; } = [];

    public static ExtensionRegistry Load(string projectRoot, Settings settings)
    {
        var reg = new ExtensionRegistry();
        BuiltinAgents.Register(reg);
        BuiltinOutputStyles.Register(reg);

        foreach (var (dir, scope) in new[]
        {
            (DotCodePaths.UserCompatDir, ExtensionScope.User),
            (DotCodePaths.UserDir, ExtensionScope.User),
            (Path.Combine(projectRoot, DotCodePaths.CompatDirName), ExtensionScope.Project),
            (DotCodePaths.ProjectDir(projectRoot), ExtensionScope.Project),
        })
        {
            reg.LoadFrom(dir, scope, null);
        }

        foreach (var plugin in PluginManager.ListInstalled(settings))
        {
            reg.Plugins.Add(plugin);
            if (plugin.Enabled) reg.LoadPlugin(plugin);
        }
        return reg;
    }

    public void LoadFrom(string dir, ExtensionScope scope, string? pluginName)
    {
        if (!Directory.Exists(dir)) return;
        LoadSkills(Path.Combine(dir, "skills"), scope, pluginName);
        LoadCommands(Path.Combine(dir, "commands"), scope, pluginName);
        LoadAgents(Path.Combine(dir, "agents"), scope, pluginName);
        LoadOutputStyles(Path.Combine(dir, "output-styles"), scope);
    }

    private void LoadPlugin(PluginInfo plugin)
    {
        LoadFrom(plugin.RootDir, ExtensionScope.Plugin, plugin.Name);
        var manifest = PluginManager.ReadManifest(plugin.RootDir);
        var hooksPath = Path.Combine(plugin.RootDir, "hooks", "hooks.json");
        try
        {
            string? hooksJson = null;
            if (manifest?.Hooks is { ValueKind: JsonValueKind.Object } inline) hooksJson = inline.GetRawText();
            else if (manifest?.Hooks is { ValueKind: JsonValueKind.String } hp) hooksPath = Path.Combine(plugin.RootDir, hp.GetString()!);
            hooksJson ??= File.Exists(hooksPath) ? File.ReadAllText(hooksPath) : null;
            if (hooksJson is not null)
            {
                hooksJson = PluginManager.ExpandRoot(hooksJson, plugin.RootDir);
                var file = JsonSerializer.Deserialize(hooksJson, SettingsJsonContext.Default.HooksFile);
                var hooks = file?.Hooks ?? JsonSerializer.Deserialize(hooksJson, SettingsJsonContext.Default.DictionaryStringListHookMatcher);
                if (hooks is not null)
                    foreach (var (ev, matchers) in hooks)
                    {
                        if (!PluginHooks.TryGetValue(ev, out var list)) PluginHooks[ev] = list = [];
                        list.AddRange(matchers);
                    }
            }

            string? mcpJson = null;
            if (manifest?.McpServers is { ValueKind: JsonValueKind.Object } mcpInline) mcpJson = "{\"mcpServers\":" + mcpInline.GetRawText() + "}";
            else if (File.Exists(Path.Combine(plugin.RootDir, ".mcp.json"))) mcpJson = File.ReadAllText(Path.Combine(plugin.RootDir, ".mcp.json"));
            if (mcpJson is not null)
            {
                mcpJson = PluginManager.ExpandRoot(mcpJson, plugin.RootDir);
                var cfg = JsonSerializer.Deserialize(mcpJson, SettingsJsonContext.Default.McpConfigFile);
                if (cfg?.McpServers is not null)
                    foreach (var (name, server) in cfg.McpServers) PluginMcpServers[$"{plugin.Name}_{name}"] = server;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            Errors.Add($"plugin {plugin.Name}: {ex.Message}");
        }
    }

    private void LoadSkills(string dir, ExtensionScope scope, string? plugin)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var skillDir in Directory.EnumerateDirectories(dir))
        {
            var file = Path.Combine(skillDir, "SKILL.md");
            if (!File.Exists(file)) continue;
            try
            {
                var fm = Frontmatter.Parse(File.ReadAllText(file));
                var name = fm.Get("name") ?? Path.GetFileName(skillDir);
                var skill = new SkillDefinition(name, fm.Get("description") ?? "", fm.Body, skillDir, scope, plugin)
                {
                    AllowedTools = fm.GetList("allowed-tools"),
                    Model = fm.Get("model"),
                };
                Skills[skill.QualifiedName] = skill;
            }
            catch (IOException ex) { Errors.Add($"{file}: {ex.Message}"); }
        }
    }

    private void LoadCommands(string dir, ExtensionScope scope, string? plugin)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var file in Directory.EnumerateFiles(dir, "*.md", SearchOption.AllDirectories))
        {
            try
            {
                var fm = Frontmatter.Parse(File.ReadAllText(file));
                // Subdirectories namespace commands: commands/frontend/lint.md -> frontend:lint
                var rel = Path.GetRelativePath(dir, file);
                var name = Path.ChangeExtension(rel, null).Replace(Path.DirectorySeparatorChar, ':').Replace('/', ':');
                var description = fm.Get("description") ?? FirstLine(fm.Body);
                Commands[plugin is null ? name : $"{plugin}:{name}"] = new CommandDefinition(name, description, fm.Body, file, scope, plugin)
                {
                    ArgumentHint = fm.Get("argument-hint"),
                    AllowedTools = fm.GetList("allowed-tools"),
                    Model = fm.Get("model"),
                };
            }
            catch (IOException ex) { Errors.Add($"{file}: {ex.Message}"); }
        }
    }

    private void LoadAgents(string dir, ExtensionScope scope, string? plugin)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var file in Directory.EnumerateFiles(dir, "*.md", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var fm = Frontmatter.Parse(File.ReadAllText(file));
                var name = fm.Get("name") ?? Path.GetFileNameWithoutExtension(file);
                var agent = new AgentDefinition(name, fm.Get("description") ?? "", fm.Body, scope, plugin)
                {
                    Tools = fm.GetList("tools") is { Count: > 0 } t && !(t.Count == 1 && t[0] == "*") ? t : null,
                    DisallowedTools = fm.GetList("disallowedTools") ?? fm.GetList("disallowed-tools"),
                    Model = fm.Get("model"),
                    Color = fm.Get("color"),
                };
                Agents[agent.QualifiedName] = agent;
            }
            catch (IOException ex) { Errors.Add($"{file}: {ex.Message}"); }
        }
    }

    private void LoadOutputStyles(string dir, ExtensionScope scope)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var file in Directory.EnumerateFiles(dir, "*.md"))
        {
            try
            {
                var fm = Frontmatter.Parse(File.ReadAllText(file));
                var name = fm.Get("name") ?? Path.GetFileNameWithoutExtension(file);
                OutputStyles[name] = new OutputStyleDefinition(name, fm.Get("description") ?? "", fm.Body,
                    fm.Get("keep-coding-instructions") is "true", scope);
            }
            catch (IOException ex) { Errors.Add($"{file}: {ex.Message}"); }
        }
    }

    private static string FirstLine(string body) =>
        body.Split('\n').Select(l => l.Trim().TrimStart('#').Trim()).FirstOrDefault(l => l.Length > 0) is { } l ? (l.Length > 80 ? l[..80] + "…" : l) : "";

    /// <summary>Expands a command template with arguments ($ARGUMENTS, $1..$9).</summary>
    public static string ExpandArguments(string body, string args)
    {
        var positional = SplitArgs(args);
        var result = body.Replace("$ARGUMENTS", args, StringComparison.Ordinal);
        for (var i = 9; i >= 1; i--)
            result = result.Replace("$" + i, i <= positional.Count ? positional[i - 1] : "", StringComparison.Ordinal);
        if (!body.Contains("$ARGUMENTS", StringComparison.Ordinal) && !body.Contains("$1", StringComparison.Ordinal) && args.Length > 0)
            result += "\n\nARGUMENTS: " + args;
        return result;
    }

    private static List<string> SplitArgs(string args)
    {
        var list = new List<string>();
        var sb = new System.Text.StringBuilder();
        char quote = '\0';
        foreach (var c in args)
        {
            if (quote != '\0') { if (c == quote) quote = '\0'; else sb.Append(c); continue; }
            if (c is '"' or '\'') { quote = c; continue; }
            if (char.IsWhiteSpace(c)) { if (sb.Length > 0) { list.Add(sb.ToString()); sb.Clear(); } continue; }
            sb.Append(c);
        }
        if (sb.Length > 0) list.Add(sb.ToString());
        return list;
    }
}

public static class BuiltinAgents
{
    public static void Register(ExtensionRegistry reg)
    {
        reg.Agents["general-purpose"] = new AgentDefinition("general-purpose",
            "General-purpose agent for researching complex questions, searching for code, and executing multi-step tasks. Use it when a search may need several attempts, or to keep large intermediate results out of the main context.",
            """
            You are a subagent of DotCode, working on a task delegated by the main agent. Complete the task fully using the tools available, then reply with a concise, self-contained report of what you found or did (file paths with line numbers, key facts, decisions). The main agent only sees your final message, so include everything it needs. Do not ask the user questions.
            """, ExtensionScope.Builtin);

        reg.Agents["Explore"] = new AgentDefinition("Explore",
            "Fast read-only agent for exploring codebases: finding files by pattern, searching code for keywords, and answering questions about how code is structured. Specify breadth: \"quick\", \"medium\" or \"very thorough\".",
            """
            You are a read-only code exploration subagent of DotCode. Use Glob, Grep and Read (and read-only shell commands) to answer the question. Search broadly first, then read only the relevant excerpts. You cannot modify files. Finish with a concise report: relevant file paths (with line numbers), how the pieces connect, and a direct answer.
            """, ExtensionScope.Builtin)
        {
            Tools = ["Read", "Glob", "Grep", "Bash", "PowerShell", "WebFetch", "WebSearch"],
            Model = "fast",
        };

        reg.Agents["Plan"] = new AgentDefinition("Plan",
            "Software architect agent for designing implementation plans. Returns step-by-step plans, identifies critical files, and considers trade-offs. Read-only.",
            """
            You are a planning subagent of DotCode. Investigate the codebase with read-only tools and produce an implementation plan: goals, the files to change (with the reason for each), step-by-step changes, risks and how to verify. Be specific and concise. You cannot modify files.
            """, ExtensionScope.Builtin)
        {
            Tools = ["Read", "Glob", "Grep", "Bash", "PowerShell", "WebFetch", "WebSearch"],
            Model = "planner",
        };
    }
}

public static class BuiltinOutputStyles
{
    public static void Register(ExtensionRegistry reg)
    {
        reg.OutputStyles["default"] = new OutputStyleDefinition("default", "Concise software-engineering assistant (default)", "", true, ExtensionScope.Builtin);
        reg.OutputStyles["explanatory"] = new OutputStyleDefinition("explanatory", "Explains implementation choices and codebase patterns with educational insights",
            """
            # Output style: Explanatory
            In addition to completing the task, share brief educational insights about the codebase and your choices. Before and after writing code, add a short block formatted as:
            `★ Insight ─────────────────────────────────────`
            [2-3 key points specific to this code, not generic advice]
            `─────────────────────────────────────────────────`
            """, true, ExtensionScope.Builtin);
        reg.OutputStyles["learning"] = new OutputStyleDefinition("learning", "Collaborative learn-by-doing mode: asks you to write small pieces of code yourself",
            """
            # Output style: Learning
            Help the user learn while working. Share brief insights about decisions. For meaningful design decisions (5-10 lines of logic), add a `TODO(human)` marker in the code and ask the user to implement that piece, explaining the context, the trade-offs and what to consider. Keep everything else fully implemented.
            """, true, ExtensionScope.Builtin);
    }
}
