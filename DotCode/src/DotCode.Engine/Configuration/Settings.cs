using System.Text.Json;
using System.Text.Json.Serialization;
using DotCode.Providers;

namespace DotCode.Engine.Configuration;

/// <summary>Merged settings (managed &gt; CLI &gt; local &gt; project &gt; user). Key names follow Claude Code's
/// settings.json where a concept exists in both, so existing project files keep working.</summary>
public sealed class Settings
{
    /// <summary>Main model, "provider:model" or alias.</summary>
    public string? Model { get; set; }
    /// <summary>Models per role: main, fast, planner, subagent, advisor.</summary>
    public Dictionary<string, string>? Models { get; set; }
    public Dictionary<string, ProviderConfig>? Providers { get; set; }
    public List<FallbackRule>? Fallback { get; set; }
    public PermissionSettings? Permissions { get; set; }
    public Dictionary<string, List<HookMatcher>>? Hooks { get; set; }
    public bool? DisableAllHooks { get; set; }
    public Dictionary<string, string>? Env { get; set; }
    public Dictionary<string, McpServerConfig>? McpServers { get; set; }
    public Dictionary<string, bool>? EnabledPlugins { get; set; }
    public string? Theme { get; set; }
    public TuiSettings? Tui { get; set; }
    public string? OutputStyle { get; set; }
    /// <summary>off | low | medium | high | xhigh</summary>
    public string? Effort { get; set; }
    public int? MaxOutputTokens { get; set; }
    public bool? AutoCompact { get; set; }
    public double? AutoCompactThreshold { get; set; }
    public int? CleanupPeriodDays { get; set; }
    public bool? IncludeCoAuthoredBy { get; set; }
    public StatusLineSettings? StatusLine { get; set; }
    public BudgetSettings? Budget { get; set; }
    /// <summary>Organization policy: only these provider names may be used.</summary>
    public List<string>? AllowedProviders { get; set; }
    public WebSearchSettings? WebSearch { get; set; }
    public int? MaxTurns { get; set; }
    public bool? RespectGitignore { get; set; }
    public List<string>? SpinnerVerbs { get; set; }
    public bool? PromptCaching { get; set; }
    public string? DefaultShell { get; set; }
    public bool? Telemetry { get; set; }
    /// <summary>OpenTelemetry OTLP/HTTP export (opt-in).</summary>
    public OtelSettings? Otel { get; set; }
    /// <summary>Hash-chained audit log of tool actions (opt-in, usually set in managed settings).</summary>
    public AuditSettings? Audit { get; set; }
}

public sealed class OtelSettings
{
    public bool? Enabled { get; set; }
    /// <summary>OTLP/HTTP base endpoint (default OTEL_EXPORTER_OTLP_ENDPOINT or http://localhost:4318).</summary>
    public string? Endpoint { get; set; }
    public Dictionary<string, string>? Headers { get; set; }
    public string? ServiceName { get; set; }
    /// <summary>Include user prompt text in span attributes (off by default).</summary>
    public bool? LogPrompts { get; set; }
}

public sealed class AuditSettings
{
    public bool? Enabled { get; set; }
    /// <summary>Log file (default ~/.dotcode/audit/audit-YYYY-MM.jsonl).</summary>
    public string? Path { get; set; }
    /// <summary>Also record user prompts (redacted). Default false.</summary>
    public bool? IncludePrompts { get; set; }
}

public sealed class FallbackRule
{
    /// <summary>Error codes: rate_limit, overloaded, server_error, network, timeout, 5xx.</summary>
    public List<string> On { get; set; } = [];
    public List<string> Chain { get; set; } = [];
}

public sealed class PermissionSettings
{
    public List<string>? Allow { get; set; }
    public List<string>? Deny { get; set; }
    public List<string>? Ask { get; set; }
    public string? DefaultMode { get; set; }
    public List<string>? AdditionalDirectories { get; set; }
    public bool? DisableBypassPermissionsMode { get; set; }
    /// <summary>Auto mode: a classifier model approves low-risk actions instead of prompting.</summary>
    public AutoModeSettings? AutoMode { get; set; }
}

public sealed class AutoModeSettings
{
    /// <summary>Include auto mode in the Shift+Tab cycle.</summary>
    public bool? Enabled { get; set; }
    /// <summary>Classifier model (defaults to models.classifier, then models.fast, then the main model).</summary>
    public string? Model { get; set; }
    /// <summary>Extra organization guidance appended to the classifier prompt (e.g. "never allow deploys").</summary>
    public string? Guidance { get; set; }
}

public sealed class HookMatcher
{
    /// <summary>Tool name regex/pipe list ("Edit|Write", "mcp__.*"); empty or "*" matches everything.</summary>
    public string? Matcher { get; set; }
    public List<HookCommand> Hooks { get; set; } = [];
}

public sealed class HookCommand
{
    public string Type { get; set; } = "command";
    public string Command { get; set; } = "";
    /// <summary>Seconds (default 60).</summary>
    public int? Timeout { get; set; }
}

public sealed class McpServerConfig
{
    /// <summary>stdio | http | sse</summary>
    public string? Type { get; set; }
    public string? Command { get; set; }
    public List<string>? Args { get; set; }
    public Dictionary<string, string>? Env { get; set; }
    public string? Url { get; set; }
    public Dictionary<string, string>? Headers { get; set; }
    public string? Cwd { get; set; }
    public bool? Disabled { get; set; }
    public int? Timeout { get; set; }

    [JsonIgnore] public string EffectiveType => Type ?? (Url is not null ? "http" : "stdio");
}

public sealed class TuiSettings
{
    /// <summary>unicode | ascii (for fonts without box/symbol glyphs)</summary>
    public string? Glyphs { get; set; }
    /// <summary>rounded | single | double | heavy | ascii</summary>
    public string? Border { get; set; }
    /// <summary>dots | claude | line | star | bounce</summary>
    public string? Spinner { get; set; }
    public bool? ReducedMotion { get; set; }
    public bool? ShowTips { get; set; }
    public bool? Vim { get; set; }
    public bool? ShowThinking { get; set; }
    public bool? Compact { get; set; }
    /// <summary>Custom accent color (hex), overriding the theme's brand color.</summary>
    public string? Accent { get; set; }
}

public sealed class StatusLineSettings
{
    public string Type { get; set; } = "command";
    public string? Command { get; set; }
    public int? Padding { get; set; }
}

public sealed class BudgetSettings
{
    public decimal? MaxUsdPerSession { get; set; }
    /// <summary>warn | stop</summary>
    public string? Action { get; set; }
}

public sealed class WebSearchSettings
{
    /// <summary>tavily | duckduckgo</summary>
    public string? Provider { get; set; }
    public string? ApiKey { get; set; }
}

public sealed class McpConfigFile
{
    public Dictionary<string, McpServerConfig>? McpServers { get; set; }
}

public sealed class PluginManifest
{
    public string? Name { get; set; }
    public string? Version { get; set; }
    public string? Description { get; set; }
    public JsonElement? Author { get; set; }
    public JsonElement? Commands { get; set; }
    public JsonElement? Agents { get; set; }
    public JsonElement? Skills { get; set; }
    public JsonElement? Hooks { get; set; }
    public JsonElement? McpServers { get; set; }
}

public sealed class HooksFile
{
    public Dictionary<string, List<HookMatcher>>? Hooks { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(Settings))]
[JsonSerializable(typeof(McpConfigFile))]
[JsonSerializable(typeof(PluginManifest))]
[JsonSerializable(typeof(HooksFile))]
[JsonSerializable(typeof(Dictionary<string, McpServerConfig>))]
[JsonSerializable(typeof(Dictionary<string, List<HookMatcher>>))]
public sealed partial class SettingsJsonContext : JsonSerializerContext;
