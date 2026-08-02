// Auto Code — Gravicode Studios (Kang Fadhil)

using System.Text.Json;

namespace AutoCode.Core.Abstractions;

/// <summary>
/// What a tool is capable of doing. Drives permission prompts and Plan-mode refusals —
/// a tool that only reads never needs approval, one that executes always does.
/// </summary>
[Flags]
public enum ToolCapability
{
    None = 0,
    ReadsFiles = 1 << 0,
    WritesFiles = 1 << 1,
    ExecutesCommands = 1 << 2,
    AccessesNetwork = 1 << 3,

    /// <summary>Tool changes state the user would want to approve first.</summary>
    Mutating = WritesFiles | ExecutesCommands,
}

/// <summary>Everything a tool needs to do its job, without reaching for globals.</summary>
public sealed class ToolInvocation
{
    public required string CallId { get; init; }

    /// <summary>Raw arguments as produced by the model.</summary>
    public required JsonElement Arguments { get; init; }

    /// <summary>Absolute path the session is rooted at. Tools must not escape it without permission.</summary>
    public required string WorkspaceRoot { get; init; }

    /// <summary>Shared per-session state (todo list, read-file tracking, semantic index, …).</summary>
    public required IAgentServices Services { get; init; }

    /// <summary>Set when the tool runs inside a subagent, so nested dispatch can be bounded.</summary>
    public string? AgentName { get; init; }

    /// <summary>Reads a required string argument.</summary>
    public string GetString(string name) =>
        TryGetString(name) ?? throw new ToolArgumentException($"Missing required argument '{name}'.");

    /// <summary>Reads an optional string argument.</summary>
    public string? TryGetString(string name) =>
        Arguments.ValueKind == JsonValueKind.Object &&
        Arguments.TryGetProperty(name, out var value) &&
        value.ValueKind is JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>Reads an optional integer argument.</summary>
    public int? TryGetInt(string name)
    {
        if (Arguments.ValueKind != JsonValueKind.Object || !Arguments.TryGetProperty(name, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var i) => i,
            JsonValueKind.String when int.TryParse(value.GetString(), out var s) => s,
            _ => null,
        };
    }

    /// <summary>Reads an optional boolean argument.</summary>
    public bool? TryGetBool(string name)
    {
        if (Arguments.ValueKind != JsonValueKind.Object || !Arguments.TryGetProperty(name, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var b) => b,
            _ => null,
        };
    }

    /// <summary>Reads an optional array argument as raw elements.</summary>
    public IReadOnlyList<JsonElement> TryGetArray(string name)
    {
        if (Arguments.ValueKind != JsonValueKind.Object ||
            !Arguments.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return [.. value.EnumerateArray()];
    }
}

/// <summary>Thrown when the model supplies arguments a tool cannot work with.</summary>
public sealed class ToolArgumentException(string message) : Exception(message);

/// <summary>Outcome of a tool call.</summary>
public sealed record ToolResult
{
    /// <summary>False when the tool failed; the model sees the error and can retry differently.</summary>
    public bool IsSuccess { get; init; } = true;

    /// <summary>Text handed back to the model.</summary>
    public string Content { get; init; } = "";

    /// <summary>Optional richer rendering for the terminal. Falls back to <see cref="Content"/>.</summary>
    public string? Display { get; init; }

    /// <summary>Structured detail for hooks and JSON output mode.</summary>
    public IReadOnlyDictionary<string, object?>? Metadata { get; init; }

    public static ToolResult Ok(string content, string? display = null) =>
        new() { IsSuccess = true, Content = content, Display = display };

    public static ToolResult Fail(string error) =>
        new() { IsSuccess = false, Content = error };
}

/// <summary>A callable capability exposed to the model.</summary>
public interface IAgentTool
{
    /// <summary>Name the model calls, e.g. <c>Read</c>.</summary>
    string Name { get; }

    /// <summary>Prompt-facing description; this is the tool's entire user manual as far as the model is concerned.</summary>
    string Description { get; }

    /// <summary>JSON Schema for <see cref="ToolInvocation.Arguments"/>.</summary>
    JsonElement InputSchema { get; }

    /// <summary>Drives approval prompts and Plan-mode gating.</summary>
    ToolCapability Capability { get; }

    /// <summary>False for tools that must not run alongside other tools (anything that mutates shared state).</summary>
    bool IsConcurrencySafe => (Capability & ToolCapability.Mutating) == 0;

    /// <summary>One-line human summary of a pending call, shown in the terminal and the approval prompt.</summary>
    string Summarize(JsonElement arguments) => Name;

    ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken);
}

/// <summary>Lookup and lifecycle for the tools available in a session.</summary>
public interface IToolRegistry
{
    IReadOnlyCollection<IAgentTool> Tools { get; }

    bool TryGet(string name, out IAgentTool tool);

    void Register(IAgentTool tool);

    /// <summary>Returns the subset visible to a subagent, honouring its allow-list.</summary>
    IReadOnlyList<IAgentTool> Filter(IReadOnlyCollection<string>? allowedNames);
}
