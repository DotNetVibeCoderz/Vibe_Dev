// Auto Code — Gravicode Studios (Kang Fadhil)

namespace AutoCode.Core.Configuration;

/// <summary>
/// Wire protocol used to talk to a model endpoint.
/// EN: Auto Code only ever speaks three dialects; every vendor maps onto one of them.
/// ID: Auto Code hanya berbicara tiga dialek; setiap vendor dipetakan ke salah satunya.
/// </summary>
public enum ProviderKind
{
    /// <summary>
    /// OpenAI Chat Completions wire format. Covers OpenAI, Azure OpenAI, DeepSeek, Qwen/DashScope,
    /// Ollama, LM Studio, vLLM, Groq, Together, OpenRouter, Mistral, xAI and anything else that
    /// exposes an OpenAI-compatible <c>/chat/completions</c> endpoint.
    /// </summary>
    OpenAICompatible = 0,

    /// <summary>Anthropic Messages API (<c>/v1/messages</c>), including extended thinking.</summary>
    Anthropic = 1,

    /// <summary>Google Gemini <c>generateContent</c> / <c>streamGenerateContent</c>.</summary>
    Gemini = 2,
}
