using System.Text.Json;
using DotCode.Abstractions;

namespace DotCode.Sdk;

/// <summary>A tool implemented by your application and offered to the model.</summary>
public sealed class DotCodeTool
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    /// <summary>JSON Schema for the tool input.</summary>
    public JsonElement InputSchema { get; init; } = DotCodeJson.Parse("{\"type\":\"object\",\"properties\":{}}");
    public bool ReadOnly { get; init; }
    public required Func<JsonElement, CancellationToken, Task<string>> Handler { get; init; }

    public static DotCodeTool Create(string name, string description, string inputSchemaJson, Func<JsonElement, CancellationToken, Task<string>> handler, bool readOnly = false) =>
        new() { Name = name, Description = description, InputSchema = DotCodeJson.Parse(inputSchemaJson), Handler = handler, ReadOnly = readOnly };

    public static DotCodeTool Create(string name, string description, string inputSchemaJson, Func<JsonElement, string> handler, bool readOnly = false) =>
        Create(name, description, inputSchemaJson, (input, _) => Task.FromResult(handler(input)), readOnly);
}

public sealed class McpServerSpec
{
    public string? Type { get; init; }
    public string? Command { get; init; }
    public IReadOnlyList<string>? Args { get; init; }
    public IReadOnlyDictionary<string, string>? Env { get; init; }
    public string? Url { get; init; }
    public IReadOnlyDictionary<string, string>? Headers { get; init; }
}

/// <summary>Per-session configuration. Anything omitted falls back to the user's DotCode settings.</summary>
public sealed class SessionOptions
{
    /// <summary>provider:model, alias or role (e.g. "anthropic:claude-sonnet-4-5", "openai:gpt-5", "ollama:qwen3-coder").</summary>
    public string? Model { get; init; }
    public string? FallbackModel { get; init; }
    public string? Cwd { get; init; }
    /// <summary>default | acceptEdits | plan | bypassPermissions</summary>
    public string? PermissionMode { get; init; }
    public string? SystemPrompt { get; init; }
    public string? AppendSystemPrompt { get; init; }
    public IReadOnlyList<string> AllowedTools { get; init; } = [];
    public IReadOnlyList<string> DisallowedTools { get; init; } = [];
    /// <summary>Restrict built-in tools to this list (null = all).</summary>
    public IReadOnlyList<string>? BuiltinTools { get; init; }
    public IReadOnlyList<DotCodeTool> Tools { get; init; } = [];
    public IReadOnlyDictionary<string, McpServerSpec>? McpServers { get; init; }
    /// <summary>Inline settings JSON merged on top of settings files (e.g. provider configuration for BYOK).</summary>
    public string? SettingsJson { get; init; }
    public int? MaxTurns { get; init; }
    public string? Effort { get; init; }
    public bool PersistSession { get; init; } = true;
    public bool NoMcp { get; init; }
    /// <summary>Run the session in a fresh git worktree (<c>.dotcode/worktrees/&lt;name&gt;</c>); removed on close when unchanged.</summary>
    public bool Worktree { get; init; }
    /// <summary>Worktree name (implies <see cref="Worktree"/>; an existing worktree with this name is reused).</summary>
    public string? WorktreeName { get; init; }

    /// <summary>Called when a tool needs approval. Without a handler the session is deny-by-default.</summary>
    public Func<PermissionRequest, CancellationToken, Task<PermissionDecision>>? OnPermissionRequest { get; init; }
    public Func<IReadOnlyList<UserQuestion>, CancellationToken, Task<IReadOnlyList<UserQuestionAnswer>>>? OnQuestion { get; init; }
    public Func<string, CancellationToken, Task<bool>>? OnPlanReview { get; init; }
    public Action<AgentEvent>? OnEvent { get; init; }

    internal void Write(Utf8JsonWriter w, string? defaultCwd, string? resumeId = null, bool fork = false)
    {
        w.WriteStartObject();
        if (resumeId is not null) { w.WriteString("sessionId", resumeId); w.WriteBoolean("fork", fork); }
        if ((Cwd ?? defaultCwd) is { } cwd) w.WriteString("cwd", cwd);
        if (Model is not null) w.WriteString("model", Model);
        if (FallbackModel is not null) w.WriteString("fallbackModel", FallbackModel);
        if (PermissionMode is not null) w.WriteString("permissionMode", PermissionMode);
        if (SystemPrompt is not null) w.WriteString("systemPrompt", SystemPrompt);
        if (AppendSystemPrompt is not null) w.WriteString("appendSystemPrompt", AppendSystemPrompt);
        if (MaxTurns is { } mt) w.WriteNumber("maxTurns", mt);
        if (Effort is not null) w.WriteString("effort", Effort);
        w.WriteBoolean("persistSession", PersistSession);
        if (NoMcp) w.WriteBoolean("noMcp", true);
        if (WorktreeName is { Length: > 0 } wtName) w.WriteString("worktree", wtName);
        else if (Worktree) w.WriteBoolean("worktree", true);
        if (SettingsJson is not null) { w.WritePropertyName("settings"); DotCodeJson.Parse(SettingsJson).WriteTo(w); }
        WriteList(w, "allowedTools", AllowedTools);
        WriteList(w, "disallowedTools", DisallowedTools);
        if (BuiltinTools is not null) WriteList(w, "tools", BuiltinTools);
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
            foreach (var (name, s) in McpServers)
            {
                w.WriteStartObject(name);
                if (s.Type is not null) w.WriteString("type", s.Type);
                if (s.Command is not null) w.WriteString("command", s.Command);
                if (s.Args is not null) WriteList(w, "args", s.Args);
                if (s.Url is not null) w.WriteString("url", s.Url);
                if (s.Env is not null) { w.WriteStartObject("env"); foreach (var (k, v) in s.Env) w.WriteString(k, v); w.WriteEndObject(); }
                if (s.Headers is not null) { w.WriteStartObject("headers"); foreach (var (k, v) in s.Headers) w.WriteString(k, v); w.WriteEndObject(); }
                w.WriteEndObject();
            }
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
