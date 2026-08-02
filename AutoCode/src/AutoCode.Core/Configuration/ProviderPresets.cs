// Auto Code — Gravicode Studios (Kang Fadhil)

namespace AutoCode.Core.Configuration;

/// <summary>
/// Known-vendor defaults so a user only has to supply an API key.
/// EN: any endpoint not listed here still works — declare it manually with <c>Kind: OpenAICompatible</c>.
/// ID: endpoint yang tidak terdaftar tetap bisa dipakai — cukup deklarasikan manual dengan <c>Kind: OpenAICompatible</c>.
/// </summary>
public static class ProviderPresets
{
    public static IReadOnlyDictionary<string, ProviderProfile> All { get; } =
        new Dictionary<string, ProviderProfile>(StringComparer.OrdinalIgnoreCase)
        {
            ["openai"] = new()
            {
                Name = "openai",
                Kind = ProviderKind.OpenAICompatible,
                Endpoint = "https://api.openai.com/v1",
                ApiKey = "env:OPENAI_API_KEY",
                Model = "gpt-4.1",
                SmallModel = "gpt-4.1-mini",
                EmbeddingModel = "text-embedding-3-small",
                ContextWindow = 1_000_000,
                InputCostPerMillionTokens = 2.00m,
                OutputCostPerMillionTokens = 8.00m,
            },
            ["anthropic"] = new()
            {
                Name = "anthropic",
                Kind = ProviderKind.Anthropic,
                Endpoint = "https://api.anthropic.com/v1",
                ApiKey = "env:ANTHROPIC_API_KEY",
                Model = "claude-sonnet-4-5",
                SmallModel = "claude-haiku-4-5",
                ContextWindow = 200_000,
                EnableExtendedThinking = false,
                InputCostPerMillionTokens = 3.00m,
                OutputCostPerMillionTokens = 15.00m,
            },
            ["gemini"] = new()
            {
                Name = "gemini",
                Kind = ProviderKind.Gemini,
                Endpoint = "https://generativelanguage.googleapis.com/v1beta",
                ApiKey = "env:GEMINI_API_KEY",
                Model = "gemini-2.5-pro",
                SmallModel = "gemini-2.5-flash",
                EmbeddingModel = "text-embedding-004",
                EmbeddingDimensions = 768,
                ContextWindow = 1_000_000,
                InputCostPerMillionTokens = 1.25m,
                OutputCostPerMillionTokens = 10.00m,
            },
            ["deepseek"] = new()
            {
                Name = "deepseek",
                Kind = ProviderKind.OpenAICompatible,
                Endpoint = "https://api.deepseek.com/v1",
                ApiKey = "env:DEEPSEEK_API_KEY",
                Model = "deepseek-chat",
                SmallModel = "deepseek-chat",
                ContextWindow = 128_000,
                InputCostPerMillionTokens = 0.27m,
                OutputCostPerMillionTokens = 1.10m,
            },
            ["qwen"] = new()
            {
                Name = "qwen",
                Kind = ProviderKind.OpenAICompatible,
                Endpoint = "https://dashscope.aliyuncs.com/compatible-mode/v1",
                ApiKey = "env:DASHSCOPE_API_KEY",
                Model = "qwen-max",
                SmallModel = "qwen-turbo",
                EmbeddingModel = "text-embedding-v3",
                ContextWindow = 131_072,
                InputCostPerMillionTokens = 1.60m,
                OutputCostPerMillionTokens = 6.40m,
            },
            ["ollama"] = new()
            {
                Name = "ollama",
                Kind = ProviderKind.OpenAICompatible,
                Endpoint = "http://localhost:11434/v1",
                ApiKey = "ollama",
                Model = "qwen2.5-coder:14b",
                SmallModel = "qwen2.5-coder:7b",
                EmbeddingModel = "nomic-embed-text",
                EmbeddingDimensions = 768,
                ContextWindow = 32_768,
                SupportsParallelToolCalls = false,
            },
            ["lmstudio"] = new()
            {
                Name = "lmstudio",
                Kind = ProviderKind.OpenAICompatible,
                Endpoint = "http://localhost:1234/v1",
                ApiKey = "lm-studio",
                Model = "local-model",
                ContextWindow = 32_768,
                SupportsParallelToolCalls = false,
            },
            ["openrouter"] = new()
            {
                Name = "openrouter",
                Kind = ProviderKind.OpenAICompatible,
                Endpoint = "https://openrouter.ai/api/v1",
                ApiKey = "env:OPENROUTER_API_KEY",
                Model = "anthropic/claude-sonnet-4.5",
                ContextWindow = 200_000,
            },
            ["groq"] = new()
            {
                Name = "groq",
                Kind = ProviderKind.OpenAICompatible,
                Endpoint = "https://api.groq.com/openai/v1",
                ApiKey = "env:GROQ_API_KEY",
                Model = "llama-3.3-70b-versatile",
                ContextWindow = 131_072,
            },
            ["mistral"] = new()
            {
                Name = "mistral",
                Kind = ProviderKind.OpenAICompatible,
                Endpoint = "https://api.mistral.ai/v1",
                ApiKey = "env:MISTRAL_API_KEY",
                Model = "mistral-large-latest",
                ContextWindow = 128_000,
            },
            ["xai"] = new()
            {
                Name = "xai",
                Kind = ProviderKind.OpenAICompatible,
                Endpoint = "https://api.x.ai/v1",
                ApiKey = "env:XAI_API_KEY",
                Model = "grok-4",
                ContextWindow = 256_000,
            },
            ["azure-openai"] = new()
            {
                Name = "azure-openai",
                Kind = ProviderKind.OpenAICompatible,
                Endpoint = "env:AZURE_OPENAI_ENDPOINT",
                ApiKey = "env:AZURE_OPENAI_API_KEY",
                Model = "gpt-4.1",
                ContextWindow = 1_000_000,
            },
        };

    /// <summary>Returns a mutable copy of a preset, or null when the name is unknown.</summary>
    public static ProviderProfile? TryCreate(string name) =>
        All.TryGetValue(name, out var preset) ? preset.Clone() : null;

    /// <summary>
    /// Overlays user-supplied values onto a preset when the profile names a known vendor.
    /// Only fields the user actually set survive; everything else falls back to the preset.
    /// </summary>
    public static ProviderProfile ApplyPreset(string name, ProviderProfile user)
    {
        var preset = TryCreate(name);
        if (preset is null)
        {
            user.Name = string.IsNullOrEmpty(user.Name) ? name : user.Name;
            return user;
        }

        preset.Name = string.IsNullOrEmpty(user.Name) ? name : user.Name;
        if (user.Kind != ProviderKind.OpenAICompatible) preset.Kind = user.Kind;
        if (!string.IsNullOrWhiteSpace(user.Endpoint)) preset.Endpoint = user.Endpoint;
        if (!string.IsNullOrWhiteSpace(user.ApiKey)) preset.ApiKey = user.ApiKey;
        if (!string.IsNullOrWhiteSpace(user.Model)) preset.Model = user.Model;
        if (!string.IsNullOrWhiteSpace(user.SmallModel)) preset.SmallModel = user.SmallModel;
        if (!string.IsNullOrWhiteSpace(user.EmbeddingModel)) preset.EmbeddingModel = user.EmbeddingModel;
        if (user.EmbeddingDimensions != 1536) preset.EmbeddingDimensions = user.EmbeddingDimensions;
        if (user.Temperature.HasValue) preset.Temperature = user.Temperature;
        if (user.MaxOutputTokens.HasValue) preset.MaxOutputTokens = user.MaxOutputTokens;
        if (user.ContextWindow != 128_000) preset.ContextWindow = user.ContextWindow;
        if (user.EnableExtendedThinking) preset.EnableExtendedThinking = true;
        if (user.ThinkingBudgetTokens != 8_000) preset.ThinkingBudgetTokens = user.ThinkingBudgetTokens;
        if (!user.SupportsParallelToolCalls) preset.SupportsParallelToolCalls = false;
        if (user.InputCostPerMillionTokens > 0) preset.InputCostPerMillionTokens = user.InputCostPerMillionTokens;
        if (user.OutputCostPerMillionTokens > 0) preset.OutputCostPerMillionTokens = user.OutputCostPerMillionTokens;
        if (user.TimeoutSeconds != 600) preset.TimeoutSeconds = user.TimeoutSeconds;
        foreach (var (k, v) in user.Headers) preset.Headers[k] = v;

        return preset;
    }
}
