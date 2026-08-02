// Auto Code — Gravicode Studios (Kang Fadhil)

using AutoCode.Core.Permissions;

namespace AutoCode.Core.Configuration;

/// <summary>Root settings object, materialised by <see cref="ConfigurationLoader"/>.</summary>
public sealed class AutoCodeOptions
{
    /// <summary>Name of the <see cref="Providers"/> entry to use. Falls back to the first defined profile.</summary>
    public string? ActiveProvider { get; set; }

    /// <summary>All configured endpoints, keyed by profile name.</summary>
    public Dictionary<string, ProviderProfile> Providers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Default permission posture for new sessions.</summary>
    public PermissionMode PermissionMode { get; set; } = PermissionMode.Ask;

    /// <summary>Persistent allow/deny rules, e.g. <c>Bash(git status)</c>, <c>Write(src/**)</c>.</summary>
    public PermissionRules Permissions { get; set; } = new();

    /// <summary>Maximum agent loop iterations per user turn before the loop yields back for input.</summary>
    public int MaxTurnIterations { get; set; } = 100;

    /// <summary>Fraction of the context window at which auto-compaction fires.</summary>
    public double CompactionThreshold { get; set; } = 0.82;

    /// <summary>Enable the on-disk transcript so <c>--resume</c> and <c>--continue</c> work.</summary>
    public bool PersistSessions { get; set; } = true;

    /// <summary>Show a running USD estimate in the status line.</summary>
    public bool ShowCost { get; set; } = true;

    /// <summary>Stream assistant reasoning to the terminal when the model emits it.</summary>
    public bool ShowThinking { get; set; } = true;

    /// <summary>Extra context files loaded on top of the discovered AUTOCODE.md / CLAUDE.md chain.</summary>
    public List<string> ContextFiles { get; set; } = [];

    /// <summary>Directories scanned for skills, in addition to the built-in project/user locations.</summary>
    public List<string> SkillDirectories { get; set; } = [];

    /// <summary>Named subagent definitions loaded from configuration (files take precedence).</summary>
    public Dictionary<string, AgentDefinitionOptions> Agents { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Named agent teams that dispatch several subagents at once.</summary>
    public Dictionary<string, Agents.TeamDefinition> Teams { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Lifecycle hooks, keyed by event name (<c>PreToolUse</c>, <c>PostToolUse</c>, …).</summary>
    public Dictionary<string, List<HookOptions>> Hooks { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>MCP servers to launch/connect on startup, keyed by server name.</summary>
    public Dictionary<string, McpServerOptions> McpServers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Plugin directories to load (each may contribute skills, agents, hooks and commands).</summary>
    public List<string> Plugins { get; set; } = [];

    /// <summary>Tools the model may never call, by name or glob.</summary>
    public List<string> DisabledTools { get; set; } = [];

    /// <summary>Build/verify commands the agent runs during the verify phase, e.g. <c>dotnet build</c>.</summary>
    public List<string> VerifyCommands { get; set; } = [];

    /// <summary>Shell used by the Bash tool. Defaults to pwsh/powershell on Windows, /bin/bash elsewhere.</summary>
    public string? Shell { get; set; }

    /// <summary>Default Bash tool timeout in milliseconds.</summary>
    public int BashTimeoutMs { get; set; } = 120_000;

    /// <summary>Enable the embedding-backed semantic codebase index.</summary>
    public bool EnableSemanticIndex { get; set; }

    /// <summary>
    /// Where embeddings come from. Left unset, it follows the chat provider; set it to point the
    /// index at a local model while chatting with a hosted one.
    /// </summary>
    public EmbeddingOptions Embeddings { get; set; } = new();

    /// <summary>UI language for CLI chrome: <c>en</c> or <c>id</c>.</summary>
    public string Language { get; set; } = "en";

    /// <summary>Resolves the profile named by <see cref="ActiveProvider"/>, or the first one defined.</summary>
    public ProviderProfile? ResolveActiveProfile()
    {
        if (Providers.Count == 0)
            return null;

        if (!string.IsNullOrWhiteSpace(ActiveProvider) &&
            Providers.TryGetValue(ActiveProvider, out var named))
        {
            named.Name = string.IsNullOrEmpty(named.Name) ? ActiveProvider : named.Name;
            return named;
        }

        var first = Providers.First();
        first.Value.Name = string.IsNullOrEmpty(first.Value.Name) ? first.Key : first.Value.Name;
        return first.Value;
    }
}

/// <summary>Declarative subagent definition (configuration form; the file form lives in <c>.autocode/agents/*.md</c>).</summary>
public sealed class AgentDefinitionOptions
{
    public string Description { get; set; } = "";
    public string Prompt { get; set; } = "";
    public List<string> Tools { get; set; } = [];
    public string? Model { get; set; }
    public string? Provider { get; set; }
    public int MaxIterations { get; set; } = 40;
}

/// <summary>A single hook binding: run <see cref="Command"/> when the event fires and the matcher hits.</summary>
public sealed class HookOptions
{
    /// <summary>Glob matched against the tool name (<c>PreToolUse</c>/<c>PostToolUse</c>) or left empty for all.</summary>
    public string? Matcher { get; set; }

    /// <summary>Shell command to execute. Receives the hook payload as JSON on stdin.</summary>
    public string Command { get; set; } = "";

    /// <summary>Timeout in milliseconds.</summary>
    public int TimeoutMs { get; set; } = 30_000;

    /// <summary>When true a non-zero exit blocks the action (only meaningful for <c>Pre*</c> events).</summary>
    public bool Blocking { get; set; } = true;
}

/// <summary>MCP server launch/connection descriptor.</summary>
public sealed class McpServerOptions
{
    /// <summary><c>stdio</c> (default) or <c>http</c>.</summary>
    public string Transport { get; set; } = "stdio";

    /// <summary>Executable for stdio transport.</summary>
    public string? Command { get; set; }

    /// <summary>Arguments for stdio transport.</summary>
    public List<string> Args { get; set; } = [];

    /// <summary>Environment overrides for the spawned server.</summary>
    public Dictionary<string, string> Env { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Endpoint for http transport.</summary>
    public string? Url { get; set; }

    /// <summary>Extra headers for http transport.</summary>
    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Skip this server without removing its configuration.</summary>
    public bool Disabled { get; set; }
}
