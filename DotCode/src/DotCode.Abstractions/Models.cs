using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotCode.Abstractions;

[JsonConverter(typeof(JsonStringEnumConverter<ReasoningEffort>))]
public enum ReasoningEffort { Off, Low, Medium, High, XHigh }

public sealed record ReasoningOptions(ReasoningEffort Effort, int? BudgetTokens = null)
{
    /// <summary>Default thinking budget for providers that use token budgets instead of effort levels.</summary>
    public int EffectiveBudget => BudgetTokens ?? Effort switch
    {
        ReasoningEffort.Low => 2048,
        ReasoningEffort.Medium => 8192,
        ReasoningEffort.High => 16384,
        ReasoningEffort.XHigh => 32768,
        _ => 0,
    };
}

[JsonConverter(typeof(JsonStringEnumConverter<ToolChoiceKind>))]
public enum ToolChoiceKind { Auto, Any, None, Tool }

public sealed record ToolChoice(ToolChoiceKind Kind, string? ToolName = null)
{
    public static readonly ToolChoice Auto = new(ToolChoiceKind.Auto);
    public static readonly ToolChoice Any = new(ToolChoiceKind.Any);
    public static readonly ToolChoice None = new(ToolChoiceKind.None);
}

/// <summary>Tool definition sent to the model. <see cref="InputSchema"/> is full JSON Schema; adapters sanitize per provider profile.</summary>
public sealed record ToolSchema(string Name, string Description, JsonElement InputSchema);

public sealed record SystemBlock(string Text, bool Cache = false);

public sealed record ModelRequest
{
    public required string Model { get; init; }
    public required IReadOnlyList<Message> Messages { get; init; }
    public IReadOnlyList<ToolSchema> Tools { get; init; } = [];
    public IReadOnlyList<SystemBlock> System { get; init; } = [];
    public int MaxOutputTokens { get; init; } = 16000;
    public double? Temperature { get; init; }
    public ReasoningOptions? Reasoning { get; init; }
    public ToolChoice ToolChoice { get; init; } = ToolChoice.Auto;
    /// <summary>Enable provider prompt caching hints (explicit breakpoints where supported).</summary>
    public bool PromptCaching { get; init; } = true;
    /// <summary>Provider-specific escape hatch, merged into the request body.</summary>
    public IReadOnlyDictionary<string, JsonElement>? ProviderOptions { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<StopReason>))]
public enum StopReason { EndTurn, ToolUse, MaxTokens, StopSequence, Refusal, Aborted, Error }

public sealed record Usage
{
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public long CacheReadTokens { get; init; }
    public long CacheWriteTokens { get; init; }
    public long ReasoningTokens { get; init; }

    public long TotalTokens => InputTokens + OutputTokens + CacheReadTokens + CacheWriteTokens;
    /// <summary>Tokens occupying the context window for the request that produced this usage.</summary>
    public long ContextTokens => InputTokens + CacheReadTokens + CacheWriteTokens + OutputTokens;

    public static Usage operator +(Usage a, Usage b) => new()
    {
        InputTokens = a.InputTokens + b.InputTokens,
        OutputTokens = a.OutputTokens + b.OutputTokens,
        CacheReadTokens = a.CacheReadTokens + b.CacheReadTokens,
        CacheWriteTokens = a.CacheWriteTokens + b.CacheWriteTokens,
        ReasoningTokens = a.ReasoningTokens + b.ReasoningTokens,
    };

    public static readonly Usage Zero = new();
}

/// <summary>Streaming events produced by a provider adapter. Deltas are for live UI; completed blocks
/// (<see cref="ContentBlockCompleted"/>) are authoritative and assembled by the engine into the assistant message.</summary>
public abstract record ModelEvent;
public sealed record MessageStarted(string? Id, string? Model) : ModelEvent;
public sealed record TextDelta(string Text) : ModelEvent;
public sealed record ThinkingDelta(string Text) : ModelEvent;
public sealed record ToolUseStarted(string Id, string Name) : ModelEvent;
public sealed record ToolInputDelta(string Id, string PartialJson) : ModelEvent;
public sealed record ContentBlockCompleted(ContentPart Part) : ModelEvent;
public sealed record UsageUpdated(Usage Usage) : ModelEvent;
public sealed record MessageStopped(StopReason Reason, Usage? Usage) : ModelEvent;

[JsonConverter(typeof(JsonStringEnumConverter<ReasoningSupport>))]
public enum ReasoningSupport { None, Budget, Effort, Always }

[JsonConverter(typeof(JsonStringEnumConverter<CachingSupport>))]
public enum CachingSupport { None, Implicit, ExplicitBreakpoints }

[JsonConverter(typeof(JsonStringEnumConverter<JsonSchemaProfile>))]
public enum JsonSchemaProfile { Full, Strict, OpenApiSubset, Minimal }

public sealed record ModelCapabilities
{
    public int ContextWindow { get; init; } = 128_000;
    public int MaxOutputTokens { get; init; } = 16_000;
    public bool Tools { get; init; } = true;
    public bool ParallelToolCalls { get; init; } = true;
    public bool Vision { get; init; }
    public bool Pdf { get; init; }
    public ReasoningSupport Reasoning { get; init; } = ReasoningSupport.None;
    public CachingSupport Caching { get; init; } = CachingSupport.None;
    public bool Streaming { get; init; } = true;
    public bool StructuredOutput { get; init; }
    public JsonSchemaProfile SchemaProfile { get; init; } = JsonSchemaProfile.Full;
    public bool TokenCountingEndpoint { get; init; }
    /// <summary>USD per million tokens (estimates; overridable in settings).</summary>
    public decimal InputPricePerMTok { get; init; }
    public decimal OutputPricePerMTok { get; init; }
    public decimal CacheReadPricePerMTok { get; init; }
    public decimal CacheWritePricePerMTok { get; init; }

    public decimal EstimateCost(Usage u) =>
        (u.InputTokens * InputPricePerMTok + u.OutputTokens * OutputPricePerMTok +
         u.CacheReadTokens * CacheReadPricePerMTok + u.CacheWriteTokens * CacheWritePricePerMTok) / 1_000_000m;
}

public sealed record ModelInfo(string ProviderId, string Id, string? DisplayName = null)
{
    public string QualifiedId => $"{ProviderId}:{Id}";
}

/// <summary>Structured provider failure. <see cref="Retryable"/> drives retry/backoff and fallback chains.</summary>
public sealed class ModelProviderException(string providerId, string code, string message, bool retryable, int? statusCode = null, TimeSpan? retryAfter = null, Exception? inner = null)
    : Exception(message, inner)
{
    public string ProviderId { get; } = providerId;
    /// <summary>rate_limit | overloaded | server_error | auth | invalid_request | context_length | network | timeout | unknown</summary>
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;
    public int? StatusCode { get; } = statusCode;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}
