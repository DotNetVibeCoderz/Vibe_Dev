// Auto Code — Gravicode Studios (Kang Fadhil)

namespace AutoCode.Core.Configuration;

/// <summary>
/// One configured model endpoint. Users may define as many as they like and switch at runtime
/// with <c>/model</c> or <c>/provider</c>, or pin one via configuration.
/// </summary>
public sealed class ProviderProfile
{
    /// <summary>Profile key, e.g. <c>openai</c>, <c>deepseek</c>, <c>ollama-local</c>.</summary>
    public string Name { get; set; } = "";

    /// <summary>Wire protocol. Defaults to OpenAI-compatible.</summary>
    public ProviderKind Kind { get; set; } = ProviderKind.OpenAICompatible;

    /// <summary>Base URL of the endpoint. Required for every provider except stock OpenAI/Anthropic/Gemini.</summary>
    public string? Endpoint { get; set; }

    /// <summary>
    /// API key. May be a literal, or <c>env:VAR_NAME</c> to read from the environment at resolve time —
    /// the latter keeps secrets out of committed settings files.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>Default model id used for the main agent loop.</summary>
    public string Model { get; set; } = "";

    /// <summary>Cheaper/faster model used for background work (titles, compaction, quick classification).</summary>
    public string? SmallModel { get; set; }

    /// <summary>Model id used to embed source chunks for the semantic index. Optional.</summary>
    public string? EmbeddingModel { get; set; }

    /// <summary>Dimension of <see cref="EmbeddingModel"/> output. Defaults to 1536.</summary>
    public int EmbeddingDimensions { get; set; } = 1536;

    /// <summary>Sampling temperature for the main loop.</summary>
    public float? Temperature { get; set; }

    /// <summary>Hard cap on output tokens per turn.</summary>
    public int? MaxOutputTokens { get; set; }

    /// <summary>Total context window in tokens; drives the auto-compaction threshold.</summary>
    public int ContextWindow { get; set; } = 128_000;

    /// <summary>Enable extended reasoning / thinking when the endpoint supports it.</summary>
    public bool EnableExtendedThinking { get; set; }

    /// <summary>Thinking token budget when <see cref="EnableExtendedThinking"/> is set.</summary>
    public int ThinkingBudgetTokens { get; set; } = 8_000;

    /// <summary>Some endpoints (several Ollama builds, older vLLM) reject parallel tool calls.</summary>
    public bool SupportsParallelToolCalls { get; set; } = true;

    /// <summary>USD per one million input tokens; used by the cost tracker. 0 disables cost display.</summary>
    public decimal InputCostPerMillionTokens { get; set; }

    /// <summary>USD per one million output tokens.</summary>
    public decimal OutputCostPerMillionTokens { get; set; }

    /// <summary>Extra headers sent on every request (e.g. <c>HTTP-Referer</c> for OpenRouter).</summary>
    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Request timeout in seconds.</summary>
    public int TimeoutSeconds { get; set; } = 600;

    /// <summary>
    /// Resolves <see cref="ApiKey"/>, honouring the <c>env:</c> indirection.
    /// Returns null when unset or when the referenced variable is absent.
    /// </summary>
    public string? ResolveApiKey() => ResolveIndirect(ApiKey);

    /// <summary>Resolves <see cref="Endpoint"/>, honouring the <c>env:</c> indirection.</summary>
    public string? ResolveEndpoint() => ResolveIndirect(Endpoint);

    /// <summary>Expands a <c>env:VAR</c> reference; passes literals through unchanged.</summary>
    public static string? ResolveIndirect(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (!value.StartsWith("env:", StringComparison.OrdinalIgnoreCase))
            return value;

        var variable = value[4..].Trim();
        var resolved = Environment.GetEnvironmentVariable(variable);
        return string.IsNullOrWhiteSpace(resolved) ? null : resolved;
    }

    public ProviderProfile Clone() => new()
    {
        Name = Name,
        Kind = Kind,
        Endpoint = Endpoint,
        ApiKey = ApiKey,
        Model = Model,
        SmallModel = SmallModel,
        EmbeddingModel = EmbeddingModel,
        EmbeddingDimensions = EmbeddingDimensions,
        Temperature = Temperature,
        MaxOutputTokens = MaxOutputTokens,
        ContextWindow = ContextWindow,
        EnableExtendedThinking = EnableExtendedThinking,
        ThinkingBudgetTokens = ThinkingBudgetTokens,
        SupportsParallelToolCalls = SupportsParallelToolCalls,
        InputCostPerMillionTokens = InputCostPerMillionTokens,
        OutputCostPerMillionTokens = OutputCostPerMillionTokens,
        Headers = new Dictionary<string, string>(Headers, StringComparer.OrdinalIgnoreCase),
        TimeoutSeconds = TimeoutSeconds,
    };
}
