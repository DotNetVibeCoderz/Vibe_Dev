using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotCode.Abstractions;

/// <summary>Conversation role. Tool results travel inside <see cref="Role.User"/> messages (Anthropic style);
/// adapters translate to the provider's native layout.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<Role>))]
public enum Role { User, Assistant }

/// <summary>Hint that a prompt-cache breakpoint should be placed after this block (honoured by providers with explicit caching).</summary>
public sealed record CacheHint(string Ttl = "5m");

/// <summary>Provider-proprietary payload that must be round-tripped verbatim to the provider that produced it
/// (Anthropic thinking signatures, Gemini thought signatures, OpenAI reasoning items, DeepSeek reasoning_content).</summary>
public sealed record ProviderOpaque(string ProviderId, JsonElement Payload);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TextPart), "text")]
[JsonDerivedType(typeof(ImagePart), "image")]
[JsonDerivedType(typeof(DocumentPart), "document")]
[JsonDerivedType(typeof(ToolUsePart), "tool_use")]
[JsonDerivedType(typeof(ToolResultPart), "tool_result")]
[JsonDerivedType(typeof(ThinkingPart), "thinking")]
public abstract record ContentPart;

public sealed record TextPart(string Text) : ContentPart
{
    public CacheHint? Cache { get; init; }
}

/// <summary>Image payload, base64 encoded.</summary>
public sealed record ImagePart(string Base64Data, string MediaType) : ContentPart;

/// <summary>Document payload (e.g. PDF), base64 encoded.</summary>
public sealed record DocumentPart(string Base64Data, string MediaType, string? Name = null) : ContentPart;

public sealed record ToolUsePart(string Id, string Name, JsonElement Input) : ContentPart
{
    /// <summary>Provider metadata attached to the call (e.g. Gemini thought signature).</summary>
    public ProviderOpaque? Opaque { get; init; }
}

public sealed record ToolResultPart(string ToolUseId, IReadOnlyList<ContentPart> Content, bool IsError = false) : ContentPart
{
    public static ToolResultPart FromText(string toolUseId, string text, bool isError = false) =>
        new(toolUseId, [new TextPart(text)], isError);

    public string TextContent => string.Concat(Content.OfType<TextPart>().Select(t => t.Text));
}

/// <summary>Model reasoning. <see cref="Opaque"/> carries signatures/encrypted content; <see cref="Redacted"/> marks encrypted-only blocks.</summary>
public sealed record ThinkingPart(string Text) : ContentPart
{
    public ProviderOpaque? Opaque { get; init; }
    public bool Redacted { get; init; }
}

public sealed class Message
{
    public required Role Role { get; init; }
    public required List<ContentPart> Content { get; init; }
    public string Id { get; init; } = Guid.NewGuid().ToString("n");
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    /// <summary>Provider that produced an assistant message (used to decide whether opaque blocks can be replayed).</summary>
    public string? ProviderId { get; init; }
    public string? ModelId { get; init; }
    /// <summary>Hidden from the transcript UI (skill bodies, compaction summaries, system reminders).</summary>
    public bool IsMeta { get; init; }
    /// <summary>Marks a compaction summary: history before it is not sent to the model.</summary>
    public bool IsCompactSummary { get; init; }
    public Usage? Usage { get; init; }

    public static Message User(string text, bool isMeta = false) =>
        new() { Role = Role.User, Content = [new TextPart(text)], IsMeta = isMeta };

    public static Message User(IEnumerable<ContentPart> parts, bool isMeta = false) =>
        new() { Role = Role.User, Content = [.. parts], IsMeta = isMeta };

    public static Message Assistant(string text) =>
        new() { Role = Role.Assistant, Content = [new TextPart(text)] };

    [JsonIgnore] public IEnumerable<ToolUsePart> ToolUses => Content.OfType<ToolUsePart>();
    [JsonIgnore] public IEnumerable<ToolResultPart> ToolResults => Content.OfType<ToolResultPart>();
    [JsonIgnore] public string Text => string.Concat(Content.OfType<TextPart>().Select(t => t.Text));
    [JsonIgnore] public bool HasToolResults => Content.Any(c => c is ToolResultPart);
}
