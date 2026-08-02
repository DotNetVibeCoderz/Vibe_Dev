// Auto Code — Gravicode Studios (Kang Fadhil)

using System.Text.Json;
using AutoCode.Core.Abstractions;

namespace AutoCode.Tools;

/// <summary>
/// Shared plumbing for built-in tools: schema parsing, workspace-relative naming and
/// uniform failure handling so a throwing tool never takes the loop down with it.
/// </summary>
public abstract class ToolBase : IAgentTool
{
    private JsonElement? _schema;

    public abstract string Name { get; }

    public abstract string Description { get; }

    public abstract ToolCapability Capability { get; }

    /// <summary>JSON Schema source for the tool's arguments, written inline by each tool.</summary>
    protected abstract string SchemaJson { get; }

    public JsonElement InputSchema => _schema ??= JsonDocument.Parse(SchemaJson).RootElement.Clone();

    public virtual string Summarize(JsonElement arguments) => Name;

    public async ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        try
        {
            return await ExecuteAsync(invocation, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ToolArgumentException ex)
        {
            return ToolResult.Fail($"Invalid arguments for {Name}: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            return ToolResult.Fail($"{Name} was denied access: {ex.Message}");
        }
        catch (IOException ex)
        {
            return ToolResult.Fail($"{Name} failed with an I/O error: {ex.Message}");
        }
        catch (Exception ex)
        {
            // The model gets the message and can adapt; the loop keeps running.
            return ToolResult.Fail($"{Name} failed: {ex.Message}");
        }
    }

    protected abstract ValueTask<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken);

    /// <summary>Reads a string property straight off a raw argument element, for <see cref="Summarize"/>.</summary>
    protected static string? Peek(JsonElement arguments, string property) =>
        arguments.ValueKind == JsonValueKind.Object &&
        arguments.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
