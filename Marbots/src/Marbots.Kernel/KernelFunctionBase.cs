using Marbots.Abstractions;

namespace Marbots.Kernel;

/// <summary>Convenience base: descriptor + exception-to-result translation.</summary>
public abstract class KernelFunctionBase : IKernelFunction
{
    public abstract FunctionDescriptor Descriptor { get; }

    public async ValueTask<FunctionResult> InvokeAsync(FunctionCall call, FunctionExecutionContext context, CancellationToken cancellationToken)
    {
        try
        {
            return await ExecuteAsync(call, context, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException or HttpRequestException or TimeoutException or OperationCanceledException or System.Text.Json.JsonException)
        {
            return FunctionResult.Fail(ex.Message);
        }
    }

    protected abstract ValueTask<FunctionResult> ExecuteAsync(FunctionCall call, FunctionExecutionContext ctx, CancellationToken ct);

    protected static string Schema(params (string Name, string Type, string Description, bool Required)[] props)
    {
        var sb = new System.Text.StringBuilder("{\"type\":\"object\",\"properties\":{");
        for (var i = 0; i < props.Length; i++)
        {
            var p = props[i];
            if (i > 0) sb.Append(',');
            sb.Append('"').Append(p.Name).Append("\":{\"type\":\"").Append(p.Type).Append("\",\"description\":")
              .Append(System.Text.Json.JsonSerializer.Serialize(p.Description, KernelJson.Default.String)).Append('}');
        }
        sb.Append("},\"required\":[");
        var first = true;
        foreach (var p in props)
        {
            if (!p.Required) continue;
            if (!first) sb.Append(',');
            sb.Append('"').Append(p.Name).Append('"');
            first = false;
        }
        sb.Append("]}");
        return sb.ToString();
    }

    protected static string Truncate(string text, int max) =>
        text.Length <= max ? text : string.Concat(text.AsSpan(0, max), $"\n…[truncated {text.Length - max} chars]");
}

[System.Text.Json.Serialization.JsonSerializable(typeof(string))]
internal sealed partial class KernelJson : System.Text.Json.Serialization.JsonSerializerContext;

/// <summary>Resolves model-supplied paths inside the workspace and blocks traversal outside it.</summary>
public static class WorkspacePaths
{
    public static string Resolve(string workspace, string? relative)
    {
        var root = Path.GetFullPath(workspace);
        if (string.IsNullOrWhiteSpace(relative) || relative is "." or "/" or "./") return root;
        // Models often emit Windows-style paths; treat a backslash as a separator on every OS so "..\" can't slip through.
        relative = relative.Replace('\\', '/');
        var candidate = Path.GetFullPath(Path.IsPathRooted(relative) ? relative : Path.Combine(root, relative));
        var rootWithSep = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!candidate.Equals(root, StringComparison.OrdinalIgnoreCase) &&
            !candidate.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException($"Path '{relative}' is outside the workspace. Use paths relative to the workspace root.");
        return candidate;
    }

    public static string ToRelative(string workspace, string full) =>
        Path.GetRelativePath(Path.GetFullPath(workspace), full).Replace('\\', '/');
}
