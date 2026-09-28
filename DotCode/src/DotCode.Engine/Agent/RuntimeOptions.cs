namespace DotCode.Engine.Agent;

/// <summary>Startup options (mostly from CLI flags) that override settings.</summary>
public sealed class RuntimeOptions
{
    public string Cwd { get; set; } = Environment.CurrentDirectory;
    public string? Model { get; set; }
    public string? FallbackModel { get; set; }
    public string? PermissionMode { get; set; }
    /// <summary>--dangerously-skip-permissions: start in bypassPermissions mode.</summary>
    public bool DangerouslySkipPermissions { get; set; }
    /// <summary>--allow-dangerously-skip-permissions: make bypass available in the Shift+Tab cycle without starting in it.</summary>
    public bool AllowDangerouslySkipPermissions { get; set; }
    public List<string> AllowedTools { get; set; } = [];
    public List<string> DisallowedTools { get; set; } = [];
    /// <summary>--tools: restrict the built-in tool set (empty = all).</summary>
    public List<string>? Tools { get; set; }
    public List<string> AddDirs { get; set; } = [];
    public string? SystemPrompt { get; set; }
    public string? AppendSystemPrompt { get; set; }
    public string? SettingsPath { get; set; }
    public string? SettingsJson { get; set; }
    public List<string> McpConfigs { get; set; } = [];
    public bool StrictMcpConfig { get; set; }
    public int? MaxTurns { get; set; }
    public string? Effort { get; set; }
    public string? OutputStyle { get; set; }
    public bool PersistSession { get; set; } = true;
    public bool Verbose { get; set; }
    public bool Debug { get; set; }
    /// <summary>Skip loading MCP servers entirely (fast startup, tests).</summary>
    public bool NoMcp { get; set; }
    public string? RecordTo { get; set; }
}
