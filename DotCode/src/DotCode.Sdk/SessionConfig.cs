using System.Text.Json;
using DotCode.Abstractions;
using DotCode.Engine.Permissions;
using DotCode.Providers;

namespace DotCode.Sdk;

/// <summary>Built-in tool names (for <see cref="SessionConfig.AvailableTools"/> and permission rules).</summary>
public enum BuiltinTool
{
    Read, Write, Edit, NotebookEdit, Glob, Grep, Bash, PowerShell, BashOutput, KillShell, WebFetch, WebSearch,
    TodoWrite, Agent, Skill, AskUserQuestion, ExitPlanMode, LSP,
}

public static class BuiltinToolExtensions
{
    /// <summary>A permission rule, e.g. <c>BuiltinTool.Bash.Rule("npm test:*")</c> → <c>Bash(npm test:*)</c>.</summary>
    public static string Rule(this BuiltinTool tool, string specifier) => $"{tool}({specifier})";
}

/// <summary>An MCP server for a session.</summary>
public sealed class McpServerSpec
{
    public string? Type { get; init; }
    public string? Command { get; init; }
    public IReadOnlyList<string>? Args { get; init; }
    public IReadOnlyDictionary<string, string>? Env { get; init; }
    public string? Url { get; init; }
    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    /// <summary>A server started as a child process.</summary>
    public static McpServerSpec Stdio(string command, params string[] args) => new() { Type = "stdio", Command = command, Args = args };
    /// <summary>A remote server (streamable HTTP).</summary>
    public static McpServerSpec Http(string url, IReadOnlyDictionary<string, string>? headers = null) => new() { Type = "http", Url = url, Headers = headers };
    /// <summary>A remote server (legacy SSE).</summary>
    public static McpServerSpec Sse(string url, IReadOnlyDictionary<string, string>? headers = null) => new() { Type = "sse", Url = url, Headers = headers };

    internal void Write(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        if (Type is not null) w.WriteString("type", Type);
        if (Command is not null) w.WriteString("command", Command);
        if (Args is not null) { w.WriteStartArray("args"); foreach (var a in Args) w.WriteStringValue(a); w.WriteEndArray(); }
        if (Url is not null) w.WriteString("url", Url);
        if (Env is not null) { w.WriteStartObject("env"); foreach (var (k, v) in Env) w.WriteString(k, v); w.WriteEndObject(); }
        if (Headers is not null) { w.WriteStartObject("headers"); foreach (var (k, v) in Headers) w.WriteString(k, v); w.WriteEndObject(); }
        w.WriteEndObject();
    }
}

public enum SystemMessageMode { Append, Replace }

/// <summary>Customizes the system prompt.</summary>
public sealed record SystemMessageConfig(string Content, SystemMessageMode Mode = SystemMessageMode.Append);

/// <summary>Context passed to handlers.</summary>
public sealed record Invocation(string SessionId);

/// <summary>The decision when the agent leaves plan mode.</summary>
public sealed record ExitPlanModeResult(bool Approved, bool AcceptEdits = false, string? Feedback = null)
{
    public static ExitPlanModeResult Approve() => new(true);
    /// <summary>Approve and continue in acceptEdits mode.</summary>
    public static ExitPlanModeResult ApproveAndAcceptEdits() => new(true, AcceptEdits: true);
    /// <summary>Keep planning; <paramref name="feedback"/> is returned to the model.</summary>
    public static ExitPlanModeResult Reject(string? feedback = null) => new(false, Feedback: feedback);
}

/// <summary>Ready-made permission handlers.</summary>
public static class PermissionHandler
{
    /// <summary>Approves every permission request.</summary>
    public static readonly Func<PermissionRequest, Invocation, CancellationToken, Task<PermissionDecision>> ApproveAll =
        (_, _, _) => Task.FromResult(PermissionDecision.ApproveOnce());

    /// <summary>Rejects every permission request (same as no handler).</summary>
    public static readonly Func<PermissionRequest, Invocation, CancellationToken, Task<PermissionDecision>> RejectAll =
        (_, _, _) => Task.FromResult(PermissionDecision.Reject("Rejected by the SDK host."));
}

/// <summary>An attachment sent with a message.</summary>
public abstract record Attachment
{
    /// <summary>A file the agent should read.</summary>
    public sealed record File(string Path) : Attachment;
    /// <summary>Base64 image data (image/png, image/jpeg, image/gif or image/webp).</summary>
    public sealed record Image(string Base64Data, string MediaType = "image/png") : Attachment;
}

/// <summary>One user message. A <see cref="string"/> converts to it.</summary>
public sealed class MessageOptions
{
    public required string Prompt { get; init; }
    public IReadOnlyList<Attachment> Attachments { get; init; } = [];

    public static implicit operator MessageOptions(string prompt) => new() { Prompt = prompt };
}

/// <summary>Per-session configuration. Anything omitted falls back to the user's DotCode settings.</summary>
public class SessionConfig
{
    /// <summary>provider:model, alias or role (e.g. "anthropic:claude-sonnet-4-5", "openai:gpt-5", "ollama:qwen3-coder").</summary>
    public string? Model { get; init; }
    public string? FallbackModel { get; init; }
    /// <summary>Working directory of the session (default: the client's <see cref="DotCodeClientOptions.Cwd"/>).</summary>
    public string? WorkingDirectory { get; init; }
    public PermissionMode? PermissionMode { get; init; }
    public ReasoningEffort? ReasoningEffort { get; init; }
    public SystemMessageConfig? SystemMessage { get; init; }
    /// <summary>Custom tools implemented by your application (<see cref="DotCodeTool.DefineTool{TArgs}(string, string, Func{TArgs, ToolResult}, System.Text.Json.Serialization.Metadata.JsonTypeInfo{TArgs})"/>).</summary>
    public IReadOnlyList<DotCodeTool> Tools { get; init; } = [];
    /// <summary>Restricts the built-in tools (null = all).</summary>
    public IReadOnlyList<BuiltinTool>? AvailableTools { get; init; }
    /// <summary>Permission rules to pre-approve, e.g. <c>BuiltinTool.Bash.Rule("npm test:*")</c>.</summary>
    public IReadOnlyList<string> AllowedTools { get; init; } = [];
    /// <summary>Tools to remove / rules to deny.</summary>
    public IReadOnlyList<string> ExcludedTools { get; init; } = [];
    public IReadOnlyDictionary<string, McpServerSpec>? McpServers { get; init; }
    /// <summary>Skip MCP servers from settings files.</summary>
    public bool DisableMcp { get; init; }
    /// <summary>Named providers (bring your own key), referenced as <c>"&lt;name&gt;:&lt;model&gt;"</c>.</summary>
    public IReadOnlyDictionary<string, ProviderConfig>? Providers { get; init; }
    /// <summary>Advanced: raw settings JSON merged over the settings files (prefer the typed options).</summary>
    public string? SettingsJson { get; init; }
    public int? MaxTurns { get; init; }
    /// <summary>Save the transcript so the session can be resumed (default true).</summary>
    public bool PersistSession { get; init; } = true;
    /// <summary>Run the session in a fresh git worktree (<c>.dotcode/worktrees/&lt;name&gt;</c>); removed on close when unchanged.</summary>
    public bool Worktree { get; init; }
    /// <summary>Worktree name (implies <see cref="Worktree"/>; an existing worktree with this name is reused).</summary>
    public string? WorktreeName { get; init; }

    /// <summary>Called when a tool needs approval (see <see cref="PermissionHandler"/>). Without it the session is deny-by-default.</summary>
    public Func<PermissionRequest, Invocation, CancellationToken, Task<PermissionDecision>>? OnPermissionRequest { get; init; }
    /// <summary>Answers the model's AskUserQuestion tool.</summary>
    public Func<IReadOnlyList<UserQuestion>, Invocation, CancellationToken, Task<IReadOnlyList<UserQuestionAnswer>>>? OnUserInputRequest { get; init; }
    /// <summary>Reviews the plan (markdown) when the agent leaves plan mode (default: approve).</summary>
    public Func<string, Invocation, CancellationToken, Task<ExitPlanModeResult>>? OnExitPlanMode { get; init; }
    /// <summary>Receives every event, including those emitted while the session is created.</summary>
    public Action<AgentEvent>? OnEvent { get; init; }

    internal string? EffortSetting => ReasoningEffort?.ToString().ToLowerInvariant();

    /// <summary>Raw settings merged with the typed providers (null when neither is set).</summary>
    internal string? MergedSettingsJson()
    {
        if (Providers is not { Count: > 0 }) return SettingsJson;
        return DotCodeJson.Build(w =>
        {
            w.WriteStartObject();
            JsonElement? existingProviders = null;
            if (SettingsJson is not null)
                foreach (var p in DotCodeJson.Parse(SettingsJson).EnumerateObject())
                {
                    if (p.NameEquals("providers")) { existingProviders = p.Value; continue; }
                    p.WriteTo(w);
                }
            w.WriteStartObject("providers");
            if (existingProviders is { ValueKind: JsonValueKind.Object } ep)
                foreach (var p in ep.EnumerateObject()) if (!Providers.ContainsKey(p.Name)) p.WriteTo(w);
            foreach (var (name, provider) in Providers)
            {
                w.WritePropertyName(name);
                JsonSerializer.Serialize(w, provider, ProvidersJsonContext.Default.ProviderConfig);
            }
            w.WriteEndObject();
            w.WriteEndObject();
        }).GetRawText();
    }

    internal string? McpServersJson() => McpServers is { Count: > 0 }
        ? DotCodeJson.Build(w =>
        {
            w.WriteStartObject();
            w.WriteStartObject("mcpServers");
            foreach (var (name, s) in McpServers) { w.WritePropertyName(name); s.Write(w); }
            w.WriteEndObject();
            w.WriteEndObject();
        }).GetRawText()
        : null;

    internal void Write(Utf8JsonWriter w, string? defaultCwd, string? resumeId = null, bool fork = false)
    {
        w.WriteStartObject();
        if (resumeId is not null) { w.WriteString("sessionId", resumeId); w.WriteBoolean("fork", fork); }
        if ((WorkingDirectory ?? defaultCwd) is { } cwd) w.WriteString("cwd", cwd);
        if (Model is not null) w.WriteString("model", Model);
        if (FallbackModel is not null) w.WriteString("fallbackModel", FallbackModel);
        if (PermissionMode is { } mode) w.WriteString("permissionMode", mode.ToSetting());
        if (SystemMessage is { Mode: SystemMessageMode.Replace } replace) w.WriteString("systemPrompt", replace.Content);
        else if (SystemMessage is { } append) w.WriteString("appendSystemPrompt", append.Content);
        if (MaxTurns is { } mt) w.WriteNumber("maxTurns", mt);
        if (EffortSetting is { } effort) w.WriteString("effort", effort);
        w.WriteBoolean("persistSession", PersistSession);
        if (DisableMcp) w.WriteBoolean("noMcp", true);
        if (WorktreeName is { Length: > 0 } wtName) w.WriteString("worktree", wtName);
        else if (Worktree) w.WriteBoolean("worktree", true);
        if (MergedSettingsJson() is { } settings) { w.WritePropertyName("settings"); DotCodeJson.Parse(settings).WriteTo(w); }
        WriteList(w, "allowedTools", AllowedTools);
        WriteList(w, "disallowedTools", ExcludedTools);
        if (AvailableTools is not null) WriteList(w, "tools", [.. AvailableTools.Select(t => t.ToString())]);
        if (Tools.Count > 0)
        {
            w.WriteStartArray("hostTools");
            foreach (var t in Tools)
            {
                w.WriteStartObject();
                w.WriteString("name", t.Name);
                w.WriteString("description", t.Description);
                w.WriteBoolean("readOnly", t.ReadOnly);
                w.WritePropertyName("inputSchema");
                t.InputSchema.WriteTo(w);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        if (McpServers is { Count: > 0 })
        {
            w.WriteStartObject("mcpServers");
            foreach (var (name, s) in McpServers) { w.WritePropertyName(name); s.Write(w); }
            w.WriteEndObject();
        }
        w.WriteEndObject();
    }

    private static void WriteList(Utf8JsonWriter w, string name, IReadOnlyList<string> items)
    {
        if (items.Count == 0) return;
        w.WriteStartArray(name);
        foreach (var i in items) w.WriteStringValue(i);
        w.WriteEndArray();
    }
}

/// <summary>Configuration for <see cref="DotCodeClient.ResumeSessionAsync"/>.</summary>
public sealed class ResumeSessionConfig : SessionConfig
{
    /// <summary>Continue under a new session id, leaving the original transcript untouched.</summary>
    public bool Fork { get; init; }
}
