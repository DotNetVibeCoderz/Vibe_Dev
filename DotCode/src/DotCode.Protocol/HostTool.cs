using System.Text.Json;
using DotCode.Abstractions;
using DotCode.Engine.Agent;
using DotCode.Engine.Tools;

namespace DotCode.Protocol;

/// <summary>A tool implemented by the SDK host application. The model calls it like any built-in tool; the server
/// forwards the call to the client with a <c>tool.call</c> request and returns the client's result.</summary>
public sealed class HostTool(string name, string description, JsonElement schema, bool readOnly, Func<string, JsonElement, CancellationToken, Task<(List<ContentPart> Content, bool IsError)>> invoke) : Tool
{
    public override string Name => name;
    public override string Description => description;
    public override JsonElement InputSchema => schema;
    public override bool IsReadOnly(JsonElement input) => readOnly;
    public override PermissionTarget GetPermissionTarget(JsonElement input, AgentSession s) => new(PermissionKind.None);
    public override string DisplayName(JsonElement input, AgentSession s)
    {
        var args = input.ValueKind == JsonValueKind.Object ? string.Join(", ", input.EnumerateObject().Take(2).Select(p => p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.GetRawText())) : "";
        return $"{name}({(args.Length > 60 ? args[..60] + "…" : args)})";
    }

    public override async Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken ct)
    {
        var (content, isError) = await invoke(context.ToolUseId, input, ct).ConfigureAwait(false);
        var text = string.Concat(content.OfType<TextPart>().Select(t => t.Text));
        return new ToolResult { Content = content, IsError = isError, Summary = isError ? "Error" : Engine.Util.TextUtil.FirstLine(text, 80) };
    }
}
