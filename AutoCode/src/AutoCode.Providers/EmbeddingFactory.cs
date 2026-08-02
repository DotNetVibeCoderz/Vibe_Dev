// Auto Code — Gravicode Studios (Kang Fadhil)

using System.ClientModel;
using AutoCode.Core.Configuration;
using Microsoft.Extensions.AI;
using OllamaSharp;
using OpenAI;

namespace AutoCode.Providers;

/// <summary>
/// Builds the embedding generator behind the semantic code index.
///
/// EN: this is separate from <see cref="ProviderFactory"/> because embeddings and chat are separate
/// decisions. A session can reason with a hosted model while indexing the repository with a local
/// one — which is the sensible default for a code index, since embedding a whole repository through
/// a paid API costs real money to produce a result a local model matches.
/// ID: terpisah dari <see cref="ProviderFactory"/> karena embedding dan chat adalah dua keputusan
/// berbeda. Sebuah sesi bisa bernalar dengan model daring sambil mengindeks repositori dengan model
/// lokal — pilihan yang masuk akal, karena mengindeks seluruh repositori lewat API berbayar
/// menghabiskan biaya nyata untuk hasil yang setara dengan model lokal.
/// </summary>
public static class EmbeddingFactory
{
    /// <summary>
    /// Creates the generator for the resolved settings, or null when embeddings are unavailable.
    /// </summary>
    /// <remarks>
    /// <see cref="EmbeddingProviderKind.Onnx"/> is not handled here: it lives in
    /// <c>AutoCode.Providers.Onnx</c> so that its native runtime stays out of builds that do not
    /// use it. The composition root wires that case.
    /// </remarks>
    public static IEmbeddingGenerator<string, Embedding<float>>? Create(EmbeddingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return options.Kind switch
        {
            EmbeddingProviderKind.Ollama => CreateOllama(options),
            EmbeddingProviderKind.OpenAICompatible => CreateOpenAICompatible(options),
            _ => null,
        };
    }

    /// <summary>
    /// Ollama's native <c>/api/embed</c>, rather than its OpenAI-compatibility shim.
    ///
    /// EN: OllamaSharp already implements <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/>, so
    /// this is a direct use rather than an adapter. The native endpoint is preferred because the
    /// compatibility layer has historically been the less exercised of the two, and it adds nothing
    /// here — there is no key to send and no OpenAI-specific option worth carrying.
    /// ID: OllamaSharp sudah mengimplementasikan antarmuka embedding Microsoft.Extensions.AI,
    /// sehingga ini pemakaian langsung, bukan adapter. Endpoint native dipilih karena lapisan
    /// kompatibilitas OpenAI tidak memberi keuntungan apa pun di sini.
    /// </summary>
    private static IEmbeddingGenerator<string, Embedding<float>> CreateOllama(EmbeddingOptions options)
    {
        var endpoint = options.ResolveEndpoint() ?? "http://localhost:11434";
        var model = string.IsNullOrWhiteSpace(options.Model) ? "nomic-embed-text" : options.Model;

        // OllamaSharp appends its own /api path segment, so the base URL must not carry one.
        return new OllamaApiClient(new Uri(TrimOpenAISuffix(endpoint)), model);
    }

    private static IEmbeddingGenerator<string, Embedding<float>> CreateOpenAICompatible(EmbeddingOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Model))
        {
            throw new InvalidOperationException(
                "The OpenAI-compatible embedding backend needs a model. Set embeddings.model, " +
                "for example \"text-embedding-3-small\".");
        }

        var clientOptions = new OpenAIClientOptions();

        if (options.ResolveEndpoint() is { Length: > 0 } endpoint)
            clientOptions.Endpoint = new Uri(endpoint);

        // Local servers ignore the key but the client still requires one to be present.
        var apiKey = options.ResolveApiKey() ?? "not-required";

        return new OpenAIClient(new ApiKeyCredential(apiKey), clientOptions)
            .GetEmbeddingClient(options.Model)
            .AsIEmbeddingGenerator();
    }

    /// <summary>
    /// Accepts an Ollama endpoint written either way. Users copy <c>http://localhost:11434/v1</c>
    /// from the chat provider configuration; the native API is rooted one level up.
    /// </summary>
    private static string TrimOpenAISuffix(string endpoint)
    {
        var trimmed = endpoint.TrimEnd('/');

        return trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^3]
            : trimmed;
    }
}
