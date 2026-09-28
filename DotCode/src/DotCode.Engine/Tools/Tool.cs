using System.Text.Json;
using DotCode.Abstractions;
using DotCode.Engine.Agent;

namespace DotCode.Engine.Tools;

public enum PermissionKind
{
    /// <summary>Never needs permission (todo list, questions, plan exit).</summary>
    None,
    ReadFile,
    EditFile,
    Shell,
    Web,
    Mcp,
    Agent,
    Skill,
    Other,
}

/// <summary>What a tool call touches, used by the permission engine. <see cref="Value"/> is a path, command, URL
/// or name depending on <see cref="Kind"/>.</summary>
public sealed record PermissionTarget(PermissionKind Kind, string? Value = null);

public sealed class ToolResult
{
    public required List<ContentPart> Content { get; init; }
    public bool IsError { get; init; }
    /// <summary>One-line UI summary ("Read 214 lines", "Updated Program.cs with 3 additions").</summary>
    public string Summary { get; init; } = "";
    /// <summary>Unified diff (file mutations), rendered by the UI.</summary>
    public string? Diff { get; init; }
    /// <summary>What the UI shows as output; defaults to the text content.</summary>
    public string? DisplayOutput { get; init; }

    public string Text => string.Concat(Content.OfType<TextPart>().Select(t => t.Text));

    public static ToolResult Ok(string text, string? summary = null, string? diff = null, string? display = null) =>
        new() { Content = [new TextPart(text)], Summary = summary ?? "", Diff = diff, DisplayOutput = display };

    public static ToolResult Error(string message) =>
        new() { Content = [new TextPart(message)], IsError = true, Summary = message.Split('\n')[0] };
}

/// <summary>Per-call execution context.</summary>
public sealed class ToolContext
{
    public required AgentSession Session { get; init; }
    public required string ToolUseId { get; init; }
    public string Cwd => Session.Cwd;
    public string? ParentToolUseId => Session.ParentToolUseId;

    public void Progress(string text) =>
        Session.Emit(new ToolProgressEvent(ToolUseId, text));
}

/// <summary>Base class for all tools (built-in, MCP, SDK host tools). Metadata drives permissions and scheduling:
/// read-only, concurrency-safe calls in the same model response run in parallel; others run serially.</summary>
public abstract class Tool
{
    public abstract string Name { get; }
    public abstract string Description { get; }
    public abstract JsonElement InputSchema { get; }

    public virtual bool IsReadOnly(JsonElement input) => false;
    public virtual bool IsConcurrencySafe(JsonElement input) => IsReadOnly(input);
    public virtual bool IsEnabled(AgentSession session) => true;
    /// <summary>Max characters of result text sent to the model before truncation to a temp file.</summary>
    public virtual int MaxResultChars => 30_000;

    /// <summary>User-facing call label, e.g. <c>Read(src/App.cs)</c> or <c>Bash(npm test)</c>.</summary>
    public virtual string DisplayName(JsonElement input, AgentSession session) => Name;

    public virtual PermissionTarget GetPermissionTarget(JsonElement input, AgentSession session) => new(PermissionKind.Other);

    /// <summary>Input validation beyond schema-required fields; returns an error message for the model or null.</summary>
    public virtual string? Validate(JsonElement input, AgentSession session) => null;

    /// <summary>Permission dialog content (title, detail, diff preview).</summary>
    public virtual (string Title, string? Detail, string? Diff) DescribeForPermission(JsonElement input, AgentSession session) =>
        (DisplayName(input, session), null, null);

    public abstract Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken ct);

    public ToolSchema ToSchema() => new(Name, Description, InputSchema);

    /// <summary>Checks schema "required" properties are present.</summary>
    public string? CheckRequired(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object) return "Input must be a JSON object";
        if (InputSchema.GetProp("required") is { ValueKind: JsonValueKind.Array } req)
            foreach (var r in req.EnumerateArray())
                if (r.GetString() is { } name && (!input.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null))
                    return $"InputValidationError: required parameter `{name}` is missing";
        return null;
    }

    protected static JsonElement Schema(string json) => DotCodeJson.Parse(json);

    protected static string Str(JsonElement input, string name, string fallback = "") => input.GetString(name) ?? fallback;
}
