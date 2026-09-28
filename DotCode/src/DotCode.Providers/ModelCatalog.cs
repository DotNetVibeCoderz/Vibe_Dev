using DotCode.Abstractions;

namespace DotCode.Providers;

/// <summary>Built-in capability and price catalog, matched by model-id prefix. Values are planning estimates and
/// can be overridden per model in settings (<c>providers.&lt;name&gt;.modelOverrides</c>).</summary>
public static class ModelCatalog
{
    private sealed record Entry(string Prefix, ModelCapabilities Caps);

    private static readonly Entry[] Entries =
    [
        // Anthropic
        new("claude-opus", new() { ContextWindow = 200_000, MaxOutputTokens = 32_000, Vision = true, Pdf = true, Reasoning = ReasoningSupport.Budget, Caching = CachingSupport.ExplicitBreakpoints, TokenCountingEndpoint = true, InputPricePerMTok = 5m, OutputPricePerMTok = 25m, CacheReadPricePerMTok = 0.5m, CacheWritePricePerMTok = 6.25m }),
        new("claude-sonnet", new() { ContextWindow = 200_000, MaxOutputTokens = 64_000, Vision = true, Pdf = true, Reasoning = ReasoningSupport.Budget, Caching = CachingSupport.ExplicitBreakpoints, TokenCountingEndpoint = true, InputPricePerMTok = 3m, OutputPricePerMTok = 15m, CacheReadPricePerMTok = 0.3m, CacheWritePricePerMTok = 3.75m }),
        new("claude-haiku", new() { ContextWindow = 200_000, MaxOutputTokens = 64_000, Vision = true, Pdf = true, Reasoning = ReasoningSupport.Budget, Caching = CachingSupport.ExplicitBreakpoints, TokenCountingEndpoint = true, InputPricePerMTok = 1m, OutputPricePerMTok = 5m, CacheReadPricePerMTok = 0.1m, CacheWritePricePerMTok = 1.25m }),
        new("claude-fable", new() { ContextWindow = 200_000, MaxOutputTokens = 64_000, Vision = true, Pdf = true, Reasoning = ReasoningSupport.Budget, Caching = CachingSupport.ExplicitBreakpoints, TokenCountingEndpoint = true, InputPricePerMTok = 3m, OutputPricePerMTok = 15m, CacheReadPricePerMTok = 0.3m, CacheWritePricePerMTok = 3.75m }),
        new("claude", new() { ContextWindow = 200_000, MaxOutputTokens = 32_000, Vision = true, Pdf = true, Reasoning = ReasoningSupport.Budget, Caching = CachingSupport.ExplicitBreakpoints, TokenCountingEndpoint = true, InputPricePerMTok = 3m, OutputPricePerMTok = 15m, CacheReadPricePerMTok = 0.3m, CacheWritePricePerMTok = 3.75m }),
        // OpenAI
        new("gpt-5-nano", new() { ContextWindow = 400_000, MaxOutputTokens = 128_000, Vision = true, Pdf = true, Reasoning = ReasoningSupport.Effort, Caching = CachingSupport.Implicit, InputPricePerMTok = 0.05m, OutputPricePerMTok = 0.4m, CacheReadPricePerMTok = 0.005m }),
        new("gpt-5-mini", new() { ContextWindow = 400_000, MaxOutputTokens = 128_000, Vision = true, Pdf = true, Reasoning = ReasoningSupport.Effort, Caching = CachingSupport.Implicit, InputPricePerMTok = 0.25m, OutputPricePerMTok = 2m, CacheReadPricePerMTok = 0.025m }),
        new("gpt-5", new() { ContextWindow = 400_000, MaxOutputTokens = 128_000, Vision = true, Pdf = true, Reasoning = ReasoningSupport.Effort, Caching = CachingSupport.Implicit, InputPricePerMTok = 1.25m, OutputPricePerMTok = 10m, CacheReadPricePerMTok = 0.125m }),
        new("gpt-4.1", new() { ContextWindow = 1_000_000, MaxOutputTokens = 32_000, Vision = true, Caching = CachingSupport.Implicit, InputPricePerMTok = 2m, OutputPricePerMTok = 8m, CacheReadPricePerMTok = 0.5m }),
        new("gpt-4o", new() { ContextWindow = 128_000, MaxOutputTokens = 16_000, Vision = true, Caching = CachingSupport.Implicit, InputPricePerMTok = 2.5m, OutputPricePerMTok = 10m, CacheReadPricePerMTok = 1.25m }),
        new("o3", new() { ContextWindow = 200_000, MaxOutputTokens = 100_000, Vision = true, Reasoning = ReasoningSupport.Effort, Caching = CachingSupport.Implicit, InputPricePerMTok = 2m, OutputPricePerMTok = 8m, CacheReadPricePerMTok = 0.5m }),
        new("o4", new() { ContextWindow = 200_000, MaxOutputTokens = 100_000, Vision = true, Reasoning = ReasoningSupport.Effort, Caching = CachingSupport.Implicit, InputPricePerMTok = 1.1m, OutputPricePerMTok = 4.4m, CacheReadPricePerMTok = 0.275m }),
        // Google
        new("gemini-2.5-flash-lite", new() { ContextWindow = 1_000_000, MaxOutputTokens = 65_536, Vision = true, Pdf = true, Reasoning = ReasoningSupport.Budget, Caching = CachingSupport.Implicit, SchemaProfile = JsonSchemaProfile.OpenApiSubset, TokenCountingEndpoint = true, InputPricePerMTok = 0.1m, OutputPricePerMTok = 0.4m }),
        new("gemini-", new() { ContextWindow = 1_000_000, MaxOutputTokens = 65_536, Vision = true, Pdf = true, Reasoning = ReasoningSupport.Budget, Caching = CachingSupport.Implicit, SchemaProfile = JsonSchemaProfile.OpenApiSubset, TokenCountingEndpoint = true, InputPricePerMTok = 1.25m, OutputPricePerMTok = 10m, CacheReadPricePerMTok = 0.31m }),
        // DeepSeek
        new("deepseek-v4-flash-vision", new() { ContextWindow = 128_000, MaxOutputTokens = 32_000, Vision = true, Reasoning = ReasoningSupport.Always, Caching = CachingSupport.Implicit, ParallelToolCalls = false, InputPricePerMTok = 0.28m, OutputPricePerMTok = 0.42m, CacheReadPricePerMTok = 0.028m }),
        new("deepseek-reasoner", new() { ContextWindow = 128_000, MaxOutputTokens = 64_000, Reasoning = ReasoningSupport.Always, Caching = CachingSupport.Implicit, ParallelToolCalls = false, InputPricePerMTok = 0.28m, OutputPricePerMTok = 0.42m, CacheReadPricePerMTok = 0.028m }),
        new("deepseek", new() { ContextWindow = 128_000, MaxOutputTokens = 32_000, Reasoning = ReasoningSupport.Always, Caching = CachingSupport.Implicit, ParallelToolCalls = false, InputPricePerMTok = 0.28m, OutputPricePerMTok = 0.42m, CacheReadPricePerMTok = 0.028m }),
        // Common open-weight models (usually via Ollama / compat servers)
        new("qwen", new() { ContextWindow = 32_768, MaxOutputTokens = 8_192, Reasoning = ReasoningSupport.None }),
        new("llama", new() { ContextWindow = 32_768, MaxOutputTokens = 8_192 }),
        new("mistral", new() { ContextWindow = 32_768, MaxOutputTokens = 8_192 }),
        new("gpt-oss", new() { ContextWindow = 131_072, MaxOutputTokens = 32_768, Reasoning = ReasoningSupport.Effort }),
    ];

    private static readonly ModelCapabilities Default = new() { ContextWindow = 128_000, MaxOutputTokens = 16_000 };

    public static ModelCapabilities Lookup(string modelId, ProviderConfig? config = null)
    {
        var id = modelId.ToLowerInvariant();
        // Strip vendor prefixes used by routers ("openai/gpt-5", "anthropic.claude-...").
        var slash = id.LastIndexOf('/');
        if (slash >= 0) id = id[(slash + 1)..];
        var caps = Default;
        foreach (var e in Entries)
        {
            if (id.StartsWith(e.Prefix, StringComparison.Ordinal) || id.Contains(e.Prefix, StringComparison.Ordinal))
            {
                caps = e.Caps;
                break;
            }
        }

        if (config?.Type is "ollama") caps = caps with { InputPricePerMTok = 0, OutputPricePerMTok = 0, CacheReadPricePerMTok = 0, Caching = CachingSupport.None, ContextWindow = config.NumCtx ?? Math.Min(caps.ContextWindow, 32_768) };
        if (config?.Type is "gemini") caps = caps with { SchemaProfile = JsonSchemaProfile.OpenApiSubset };
        if (config?.Type is "anthropic" or "bedrock" or "vertex" && caps.Caching != CachingSupport.ExplicitBreakpoints) caps = caps with { Caching = CachingSupport.ExplicitBreakpoints };

        if (config?.ModelOverrides is { } overrides && (overrides.TryGetValue(modelId, out var o) || overrides.TryGetValue("*", out o)))
        {
            caps = caps with
            {
                ContextWindow = o.ContextWindow ?? caps.ContextWindow,
                MaxOutputTokens = o.MaxOutputTokens ?? caps.MaxOutputTokens,
                Tools = o.Tools ?? caps.Tools,
                Vision = o.Vision ?? caps.Vision,
                Reasoning = o.Reasoning is { } r && Enum.TryParse<ReasoningSupport>(r, true, out var rs) ? rs : caps.Reasoning,
                InputPricePerMTok = o.InputPrice ?? caps.InputPricePerMTok,
                OutputPricePerMTok = o.OutputPrice ?? caps.OutputPricePerMTok,
                CacheReadPricePerMTok = o.CacheReadPrice ?? caps.CacheReadPricePerMTok,
            };
        }
        return caps;
    }
}
