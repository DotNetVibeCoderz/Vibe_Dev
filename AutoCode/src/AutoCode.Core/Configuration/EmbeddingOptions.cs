// Auto Code — Gravicode Studios (Kang Fadhil)

namespace AutoCode.Core.Configuration;

/// <summary>
/// Where embeddings for the semantic code index come from.
///
/// EN: embeddings are configured separately from chat because the two rarely want the same backend.
/// Anthropic has no embedding endpoint at all, and even when a provider offers one, sending every
/// source file in a repository to a paid API is usually the wrong trade when a local model produces
/// a perfectly good code index for free.
/// ID: embedding dikonfigurasi terpisah dari chat karena keduanya jarang cocok memakai backend yang
/// sama. Anthropic tidak punya endpoint embedding sama sekali, dan meskipun sebuah provider
/// menyediakannya, mengirim seluruh berkas sumber ke API berbayar biasanya bukan pilihan tepat bila
/// model lokal sudah cukup.
/// </summary>
public enum EmbeddingProviderKind
{
    /// <summary>Derive from the active chat profile. Falls back to <see cref="None"/> when it has no embedding model.</summary>
    Auto = 0,

    /// <summary>An OpenAI-shaped <c>/embeddings</c> endpoint.</summary>
    OpenAICompatible = 1,

    /// <summary>Ollama's native <c>/api/embed</c>, via OllamaSharp.</summary>
    Ollama = 2,

    /// <summary>A local ONNX sentence-transformer. No server, no network, no key.</summary>
    Onnx = 3,

    /// <summary>Disable embeddings; the semantic index is unavailable.</summary>
    None = 4,
}

/// <summary>Settings for the embedding backend behind <c>CodeSearch</c> and <c>/index</c>.</summary>
public sealed class EmbeddingOptions
{
    /// <summary>Which backend to use. <see cref="EmbeddingProviderKind.Auto"/> follows the chat provider.</summary>
    public EmbeddingProviderKind Kind { get; set; } = EmbeddingProviderKind.Auto;

    /// <summary>
    /// Base URL. Defaults to the chat provider's endpoint for <see cref="EmbeddingProviderKind.OpenAICompatible"/>,
    /// and to <c>http://localhost:11434</c> for <see cref="EmbeddingProviderKind.Ollama"/>.
    /// </summary>
    public string? Endpoint { get; set; }

    /// <summary>API key, or <c>env:NAME</c>. Defaults to the chat provider's key. Unused by Ollama and ONNX.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Model id. Ignored by <see cref="EmbeddingProviderKind.Onnx"/>, which loads a file instead.</summary>
    public string? Model { get; set; }

    /// <summary>
    /// Expected vector width. Leave at 0 to learn it from the first batch — which is almost always
    /// what you want, because a stated dimension that disagrees with the model is a silent failure:
    /// every similarity comparison is skipped and the index returns nothing.
    /// </summary>
    public int Dimensions { get; set; }

    /// <summary>Path to the <c>.onnx</c> model file. Required for <see cref="EmbeddingProviderKind.Onnx"/>.</summary>
    public string? ModelPath { get; set; }

    /// <summary>Path to the model's <c>vocab.txt</c>. Required for <see cref="EmbeddingProviderKind.Onnx"/>.</summary>
    public string? VocabPath { get; set; }

    /// <summary>Token limit per chunk for the ONNX backend. Sentence-transformer models are typically 512.</summary>
    public int MaxTokens { get; set; } = 512;

    /// <summary>Lower-case input before tokenising. True for the common <c>-uncased</c> models.</summary>
    public bool LowerCase { get; set; } = true;

    /// <summary>Number of texts embedded per request. Lower it if a local server runs out of memory.</summary>
    public int BatchSize { get; set; } = 64;

    /// <summary>Whether the backend was configured at all, or is simply following the chat provider.</summary>
    public bool IsExplicit => Kind != EmbeddingProviderKind.Auto;

    /// <summary>Resolves <see cref="ApiKey"/>, honouring the <c>env:</c> indirection.</summary>
    public string? ResolveApiKey() => ProviderProfile.ResolveIndirect(ApiKey);

    /// <summary>Resolves <see cref="Endpoint"/>, honouring the <c>env:</c> indirection.</summary>
    public string? ResolveEndpoint() => ProviderProfile.ResolveIndirect(Endpoint);

    /// <summary>
    /// Produces the effective settings for a session, filling anything unset from the chat profile.
    /// </summary>
    public EmbeddingOptions Resolve(ProviderProfile profile)
    {
        var resolved = new EmbeddingOptions
        {
            Kind = Kind,
            Endpoint = Endpoint,
            ApiKey = ApiKey,
            Model = Model,
            Dimensions = Dimensions,
            ModelPath = ModelPath,
            VocabPath = VocabPath,
            MaxTokens = MaxTokens,
            LowerCase = LowerCase,
            BatchSize = BatchSize,
        };

        if (resolved.Kind == EmbeddingProviderKind.Auto)
        {
            // Follow the chat provider, but only where that actually makes sense. Anthropic and
            // Gemini profiles fall through to None rather than pretending to have an endpoint.
            resolved.Kind = profile.Kind == ProviderKind.OpenAICompatible &&
                            !string.IsNullOrWhiteSpace(profile.EmbeddingModel)
                ? EmbeddingProviderKind.OpenAICompatible
                : EmbeddingProviderKind.None;
        }

        resolved.Model ??= profile.EmbeddingModel;

        if (resolved.Dimensions == 0 && profile.EmbeddingDimensions != 1536)
            resolved.Dimensions = profile.EmbeddingDimensions;

        switch (resolved.Kind)
        {
            case EmbeddingProviderKind.OpenAICompatible:
                resolved.Endpoint ??= profile.Endpoint;
                resolved.ApiKey ??= profile.ApiKey;
                break;

            case EmbeddingProviderKind.Ollama:
                resolved.Endpoint ??= "http://localhost:11434";
                resolved.Model ??= "nomic-embed-text";
                break;
        }

        return resolved;
    }

    /// <summary>Human-readable description of the backend, for <c>/status</c> and <c>doctor</c>.</summary>
    public string Describe() => Kind switch
    {
        EmbeddingProviderKind.OpenAICompatible => $"OpenAI-compatible · {Model} · {Endpoint}",
        EmbeddingProviderKind.Ollama => $"Ollama · {Model} · {Endpoint}",
        EmbeddingProviderKind.Onnx => $"ONNX (offline) · {Path.GetFileName(ModelPath) ?? "?"}",
        EmbeddingProviderKind.None => "disabled",
        _ => "auto",
    };
}
