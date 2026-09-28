using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DotCode.Providers;

/// <summary>Configuration for one named provider entry under <c>"providers"</c> in settings.json.</summary>
public sealed class ProviderConfig
{
    /// <summary>anthropic | openai | azure | gemini | deepseek | ollama | openai-compatible | mock</summary>
    public string Type { get; set; } = "openai";
    public string? BaseUrl { get; set; }
    public string? ApiKey { get; set; }
    /// <summary>OpenAI family only: "responses" or "chat" (default chat for compat endpoints, responses for openai).</summary>
    public string? Api { get; set; }
    public Dictionary<string, string>? Headers { get; set; }
    /// <summary>Quirk profile for OpenAI-compatible servers (deepseek, openrouter, lmstudio, vllm, litellm, groq, together, azure).</summary>
    public string? Profile { get; set; }
    public OpenAIQuirks? Quirks { get; set; }
    /// <summary>Ollama context window override (Ollama's default is small and silently truncates).</summary>
    public int? NumCtx { get; set; }
    public string? KeepAlive { get; set; }
    /// <summary>Models advertised by this provider when the API cannot list them.</summary>
    public List<string>? Models { get; set; }
    /// <summary>Per-model capability overrides (context window, prices, vision...).</summary>
    public Dictionary<string, ModelOverride>? ModelOverrides { get; set; }
    public int? TimeoutSeconds { get; set; }
    /// <summary>Scripted provider: path to a script JSON file (tests / SDK conformance).</summary>
    public string? Script { get; set; }
    /// <summary>Anthropic: send "Authorization: Bearer" instead of x-api-key (gateways).</summary>
    public bool? UseBearerAuth { get; set; }
    /// <summary>Anthropic beta headers.</summary>
    public List<string>? Betas { get; set; }
}

public sealed class ModelOverride
{
    public int? ContextWindow { get; set; }
    public int? MaxOutputTokens { get; set; }
    public bool? Tools { get; set; }
    public bool? Vision { get; set; }
    public string? Reasoning { get; set; }
    public decimal? InputPrice { get; set; }
    public decimal? OutputPrice { get; set; }
    public decimal? CacheReadPrice { get; set; }
}

/// <summary>Declarative differences between OpenAI-compatible servers, so users can adapt without code changes.</summary>
public sealed class OpenAIQuirks
{
    public bool? SupportsStreamUsage { get; set; }
    /// <summary>"system" | "developer" | "user"</summary>
    public string? RoleForSystem { get; set; }
    /// <summary>"max_tokens" | "max_completion_tokens"</summary>
    public string? MaxTokensParam { get; set; }
    public bool? ParallelTools { get; set; }
    /// <summary>Delta field carrying reasoning text: "reasoning_content" (DeepSeek, vLLM) or "reasoning" (OpenRouter).</summary>
    public string? ReasoningField { get; set; }
    /// <summary>Echo reasoning back on assistant tool-call messages within the current turn (DeepSeek thinking + tools).</summary>
    public bool? SendReasoningBack { get; set; }
    public bool? SupportsTemperature { get; set; }
    public bool? SupportsReasoningEffort { get; set; }
    /// <summary>Header used for the key: "authorization" (Bearer) or "api-key" (Azure).</summary>
    public string? AuthHeader { get; set; }

    public OpenAIQuirks MergeOver(OpenAIQuirks b) => new()
    {
        SupportsStreamUsage = SupportsStreamUsage ?? b.SupportsStreamUsage,
        RoleForSystem = RoleForSystem ?? b.RoleForSystem,
        MaxTokensParam = MaxTokensParam ?? b.MaxTokensParam,
        ParallelTools = ParallelTools ?? b.ParallelTools,
        ReasoningField = ReasoningField ?? b.ReasoningField,
        SendReasoningBack = SendReasoningBack ?? b.SendReasoningBack,
        SupportsTemperature = SupportsTemperature ?? b.SupportsTemperature,
        SupportsReasoningEffort = SupportsReasoningEffort ?? b.SupportsReasoningEffort,
        AuthHeader = AuthHeader ?? b.AuthHeader,
    };

    public static OpenAIQuirks ForProfile(string? profile) => profile?.ToLowerInvariant() switch
    {
        "openai" => new() { SupportsStreamUsage = true, RoleForSystem = "developer", MaxTokensParam = "max_completion_tokens", ParallelTools = true, SupportsReasoningEffort = true },
        "azure" => new() { SupportsStreamUsage = true, RoleForSystem = "developer", MaxTokensParam = "max_completion_tokens", ParallelTools = true, SupportsReasoningEffort = true, AuthHeader = "api-key" },
        "deepseek" => new() { SupportsStreamUsage = true, RoleForSystem = "system", MaxTokensParam = "max_tokens", ParallelTools = false, ReasoningField = "reasoning_content", SendReasoningBack = true, SupportsReasoningEffort = false },
        "openrouter" => new() { SupportsStreamUsage = true, RoleForSystem = "system", MaxTokensParam = "max_tokens", ReasoningField = "reasoning" },
        "vllm" or "sglang" => new() { SupportsStreamUsage = true, RoleForSystem = "system", MaxTokensParam = "max_tokens", ReasoningField = "reasoning_content" },
        "lmstudio" or "ollama" or "llamacpp" => new() { SupportsStreamUsage = false, RoleForSystem = "system", MaxTokensParam = "max_tokens", ParallelTools = false, ReasoningField = "reasoning_content" },
        "groq" or "together" or "fireworks" or "litellm" or "mistral" => new() { SupportsStreamUsage = true, RoleForSystem = "system", MaxTokensParam = "max_tokens", ReasoningField = "reasoning" },
        _ => new() { SupportsStreamUsage = true, RoleForSystem = "system", MaxTokensParam = "max_tokens", ReasoningField = "reasoning_content" },
    };
}

public static partial class ConfigValue
{
    [GeneratedRegex(@"\$\{env:([A-Za-z_][A-Za-z0-9_]*)(?::-([^}]*))?\}|\$\{([A-Za-z_][A-Za-z0-9_]*)\}")]
    private static partial Regex EnvPattern();

    /// <summary>Expands <c>${env:NAME}</c>, <c>${env:NAME:-default}</c> and <c>${NAME}</c> references.</summary>
    public static string? Expand(string? value)
    {
        if (string.IsNullOrEmpty(value) || !value.Contains("${", StringComparison.Ordinal)) return value;
        return EnvPattern().Replace(value, m =>
        {
            var name = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[3].Value;
            var v = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrEmpty(v) && m.Groups[2].Success) v = m.Groups[2].Value;
            return v ?? "";
        });
    }
}

/// <summary>Reference to a model as <c>provider:model</c>; a bare id resolves against the default provider.</summary>
public readonly record struct ModelRef(string Provider, string Model)
{
    public static ModelRef Parse(string value, string defaultProvider)
    {
        var i = value.IndexOf(':');
        // "qwen3:8b" style Ollama tags contain a colon: only treat the prefix as provider when it is a known provider name.
        if (i > 0) return new(value[..i], value[(i + 1)..]);
        return new(defaultProvider, value);
    }

    public override string ToString() => $"{Provider}:{Model}";
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ProviderConfig))]
[JsonSerializable(typeof(Dictionary<string, ProviderConfig>))]
public sealed partial class ProvidersJsonContext : JsonSerializerContext;
