// Auto Code — Gravicode Studios (Kang Fadhil)

using System.ClientModel;
using System.ClientModel.Primitives;
using AutoCode.Core.Configuration;
using Microsoft.Extensions.AI;
using OpenAI;

namespace AutoCode.Providers;

/// <summary>
/// Turns a <see cref="ProviderProfile"/> into a live <see cref="IChatClient"/>.
///
/// EN: this is the only place in Auto Code that knows a vendor by name. Everything downstream —
/// the agent loop, tools, subagents — works purely against <see cref="IChatClient"/>, which is what
/// makes "configure any model from the app or app.config" more than a slogan.
/// ID: hanya di sinilah Auto Code mengenal nama vendor. Seluruh lapisan di atasnya bekerja murni
/// melalui <see cref="IChatClient"/>, sehingga model apa pun bisa dikonfigurasi tanpa mengubah kode.
/// </summary>
public static class ProviderFactory
{
    /// <summary>Creates the chat client for a profile's main model.</summary>
    public static IChatClient CreateChatClient(ProviderProfile profile, HttpClient? http = null) =>
        CreateChatClient(profile, profile.Model, http);

    /// <summary>Creates a chat client pinned to a specific model id on the same endpoint.</summary>
    public static IChatClient CreateChatClient(ProviderProfile profile, string model, HttpClient? http = null)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (string.IsNullOrWhiteSpace(model))
            throw new InvalidOperationException($"Provider '{profile.Name}' has no model configured.");

        return profile.Kind switch
        {
            ProviderKind.Anthropic => new AnthropicChatClient(WithModel(profile, model), http),
            ProviderKind.Gemini => new GeminiChatClient(WithModel(profile, model), http),
            _ => CreateOpenAICompatible(profile, model, http),
        };
    }

    /// <summary>
    /// Creates the cheaper client used for background work — titles, compaction summaries and
    /// quick classification — falling back to the main model when no small model is configured.
    /// </summary>
    public static IChatClient CreateSmallChatClient(ProviderProfile profile, HttpClient? http = null) =>
        CreateChatClient(profile, string.IsNullOrWhiteSpace(profile.SmallModel) ? profile.Model : profile.SmallModel, http);

    private static IChatClient CreateOpenAICompatible(ProviderProfile profile, string model, HttpClient? http) =>
        BuildOpenAIClient(profile, http).GetChatClient(model).AsIChatClient();

    private static OpenAIClient BuildOpenAIClient(ProviderProfile profile, HttpClient? http = null)
    {
        var options = new OpenAIClientOptions
        {
            NetworkTimeout = TimeSpan.FromSeconds(profile.TimeoutSeconds),
        };

        if (profile.ResolveEndpoint() is { Length: > 0 } endpoint)
            options.Endpoint = new Uri(endpoint);

        if (http is not null)
            options.Transport = new HttpClientPipelineTransport(http);

        foreach (var (name, value) in profile.Headers)
            options.AddPolicy(new StaticHeaderPolicy(name, value), PipelinePosition.PerCall);

        // Local runtimes such as Ollama and LM Studio ignore the key but the client still requires one.
        var apiKey = profile.ResolveApiKey() ?? "not-required";

        return new OpenAIClient(new ApiKeyCredential(apiKey), options);
    }

    private static ProviderProfile WithModel(ProviderProfile profile, string model)
    {
        if (string.Equals(profile.Model, model, StringComparison.Ordinal))
            return profile;

        var clone = profile.Clone();
        clone.Model = model;
        return clone;
    }

    /// <summary>Adds a fixed header to every request — used for gateways like OpenRouter.</summary>
    private sealed class StaticHeaderPolicy(string name, string value) : PipelinePolicy
    {
        public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int index)
        {
            message.Request.Headers.Set(name, value);
            ProcessNext(message, pipeline, index);
        }

        public override ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int index)
        {
            message.Request.Headers.Set(name, value);
            return ProcessNextAsync(message, pipeline, index);
        }
    }
}
