using DotCode.Abstractions;
using DotCode.Providers.Anthropic;
using DotCode.Providers.Gemini;
using DotCode.Providers.Ollama;
using DotCode.Providers.OpenAI;
using DotCode.Providers.Testing;

namespace DotCode.Providers;

public static class ProviderFactory
{
    public static readonly string[] KnownTypes = ["anthropic", "openai", "azure", "gemini", "deepseek", "ollama", "openai-compatible", "mock"];

    public static IModelProvider Create(string name, ProviderConfig config)
    {
        var type = (config.Type ?? name).ToLowerInvariant();
        return type switch
        {
            "anthropic" => new AnthropicProvider(name, config),
            "openai" or "azure" => string.Equals(config.Api, "chat", StringComparison.OrdinalIgnoreCase)
                ? new OpenAIChatProvider(name, config)
                : new OpenAIResponsesProvider(name, config),
            "deepseek" or "openai-compatible" or "compat" => string.Equals(config.Api, "responses", StringComparison.OrdinalIgnoreCase)
                ? new OpenAIResponsesProvider(name, config)
                : new OpenAIChatProvider(name, config),
            "gemini" or "google" => new GeminiProvider(name, config),
            "ollama" => new OllamaProvider(name, config),
            "mock" or "scripted" => config.Script is { } script && File.Exists(script)
                ? ScriptedProvider.FromFile(name, script)
                : new ScriptedProvider(name),
            _ => throw new ArgumentException($"Unknown provider type '{config.Type}' for provider '{name}'. Known: {string.Join(", ", KnownTypes)}"),
        };
    }

    /// <summary>Providers auto-configured from well-known environment variables when settings declare none.</summary>
    public static Dictionary<string, ProviderConfig> FromEnvironment()
    {
        var result = new Dictionary<string, ProviderConfig>(StringComparer.OrdinalIgnoreCase);
        if (Env("ANTHROPIC_API_KEY") is not null || Env("ANTHROPIC_AUTH_TOKEN") is not null)
            result["anthropic"] = new ProviderConfig { Type = "anthropic", BaseUrl = Env("ANTHROPIC_BASE_URL") };
        if (Env("OPENAI_API_KEY") is not null)
            result["openai"] = new ProviderConfig { Type = "openai", BaseUrl = Env("OPENAI_BASE_URL") };
        if (Env("AZURE_OPENAI_API_KEY") is not null && Env("AZURE_OPENAI_ENDPOINT") is { } azureEndpoint)
            result["azure"] = new ProviderConfig { Type = "azure", BaseUrl = azureEndpoint.TrimEnd('/') + (azureEndpoint.Contains("/openai/v1", StringComparison.Ordinal) ? "" : "/openai/v1") };
        if (Env("GEMINI_API_KEY") is not null || Env("GOOGLE_API_KEY") is not null)
            result["gemini"] = new ProviderConfig { Type = "gemini" };
        if (Env("DEEPSEEK_API_KEY") is not null)
            result["deepseek"] = new ProviderConfig { Type = "deepseek", BaseUrl = Env("DEEPSEEK_BASE_URL") };
        if (Env("OLLAMA_HOST") is not null)
            result["ollama"] = new ProviderConfig { Type = "ollama" };
        return result;
    }

    private static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;
}
