namespace DotCode.Abstractions;

/// <summary>A normalized LLM backend. One adapter per wire protocol (Anthropic Messages, OpenAI Chat/Responses,
/// Gemini, Ollama); vendors sharing a protocol are configured via quirk profiles rather than new adapters.</summary>
public interface IModelProvider
{
    /// <summary>Configured provider name (e.g. "anthropic", "azure", "deepseek").</summary>
    string Id { get; }

    ValueTask<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken ct);

    ModelCapabilities GetCapabilities(string modelId);

    /// <summary>Streams a single model response. Must emit exactly one <see cref="MessageStopped"/> on success,
    /// and throw <see cref="ModelProviderException"/> on failure.</summary>
    IAsyncEnumerable<ModelEvent> StreamAsync(ModelRequest request, CancellationToken ct);

    /// <summary>Exact token count when the provider exposes an endpoint; null otherwise.</summary>
    ValueTask<int?> CountTokensAsync(ModelRequest request, CancellationToken ct) => ValueTask.FromResult<int?>(null);
}
