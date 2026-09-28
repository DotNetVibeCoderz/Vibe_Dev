using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotCode.Abstractions;

/// <summary>Source-generated serialization metadata (NativeAOT: no reflection-based serialization anywhere).</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(Message))]
[JsonSerializable(typeof(List<Message>))]
[JsonSerializable(typeof(ContentPart))]
[JsonSerializable(typeof(AgentEvent))]
[JsonSerializable(typeof(Usage))]
[JsonSerializable(typeof(ModelInfo))]
[JsonSerializable(typeof(List<ModelInfo>))]
[JsonSerializable(typeof(ModelCapabilities))]
[JsonSerializable(typeof(PermissionRequest))]
[JsonSerializable(typeof(PermissionDecision))]
[JsonSerializable(typeof(UserQuestion))]
[JsonSerializable(typeof(List<UserQuestion>))]
[JsonSerializable(typeof(List<UserQuestionAnswer>))]
[JsonSerializable(typeof(List<TodoItem>))]
[JsonSerializable(typeof(ToolSchema))]
[JsonSerializable(typeof(JsonElement))]
public sealed partial class AbstractionsJsonContext : JsonSerializerContext;

public static class DotCodeJson
{
    /// <summary>Shared options for reading user-authored JSON(C): comments and trailing commas allowed.</summary>
    public static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json, DocumentOptions);
        return doc.RootElement.Clone();
    }

    public static readonly JsonElement EmptyObject = Parse("{}");

    public static string? GetString(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static int? GetInt(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    public static bool? GetBool(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    public static JsonElement? GetProp(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v : null;

    /// <summary>Serializes any value through a writer callback into a detached <see cref="JsonElement"/>.</summary>
    public static JsonElement Build(Action<Utf8JsonWriter> write)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(buffer)) write(w);
        using var doc = JsonDocument.Parse(buffer.WrittenMemory);
        return doc.RootElement.Clone();
    }
}
