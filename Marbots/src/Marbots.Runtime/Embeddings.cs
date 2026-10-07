using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Marbots.Abstractions;
using Marbots.Providers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Marbots.Runtime;

public static class EmbeddingProviders
{
    /// <summary>"hash" (default, offline), "none", or "provider/model" (an OpenAI-compatible embeddings endpoint).</summary>
    public static IEmbeddingProvider Create(MarbotsOptions options, IServiceProvider sp)
    {
        var setting = (options.EmbeddingModel ?? "hash").Trim();
        if (setting.Equals("none", StringComparison.OrdinalIgnoreCase)) return new NoEmbeddings();
        if (setting.Equals("hash", StringComparison.OrdinalIgnoreCase) || setting.Length == 0) return new HashingEmbeddings();
        var slash = setting.IndexOf('/');
        var provider = slash > 0 ? options.Providers.FirstOrDefault(p => p.Name.Equals(setting[..slash], StringComparison.OrdinalIgnoreCase)) : null;
        if (provider is null || provider.Kind == "mock")
        {
            sp.GetService<ILoggerFactory>()?.CreateLogger("Marbots.Embeddings").LogWarning("Embedding model '{Model}' not usable; using offline hashing embeddings", setting);
            return new HashingEmbeddings();
        }
        return new EndpointEmbeddings(provider, setting[(slash + 1)..], sp.GetRequiredService<IHttpClientFactory>().CreateClient("marbots-llm"),
            sp.GetService<ILoggerFactory>()?.CreateLogger<EndpointEmbeddings>());
    }
}

/// <summary>Turns embeddings off (memory search stays lexical).</summary>
public sealed class NoEmbeddings : IEmbeddingProvider
{
    public string Model => "none";
    public ValueTask<float[]?> EmbedAsync(string text, CancellationToken cancellationToken = default) => ValueTask.FromResult<float[]?>(null);
}

/// <summary>
/// Offline embeddings by feature hashing of words, word pairs and character trigrams (384 dimensions). Not semantic in
/// the neural sense, but it matches inflections, compounds and typos that keyword search misses, with no network.
/// </summary>
public sealed class HashingEmbeddings : IEmbeddingProvider
{
    public const int Dimensions = 1024;
    public string Model => "hash-1024-v2";

    private static readonly HashSet<string> Stop = new(StringComparer.Ordinal)
    {
        "the", "and", "for", "are", "was", "with", "that", "this", "from", "via", "into", "what", "which", "yang", "dan", "untuk", "dengan", "dari", "ini", "itu", "pada",
    };

    private static readonly char[] Separators = [' ', '\t', '\n', '\r', ',', '.', ';', ':', '?', '!', '(', ')', '"', '\'', '/', '\\', '-', '_'];

    public ValueTask<float[]?> EmbedAsync(string text, CancellationToken cancellationToken = default) => ValueTask.FromResult<float[]?>(Embed(text));

    public static float[] Embed(string text)
    {
        var v = new float[Dimensions];
        void Add(string feature, float weight)
        {
            var h = Fnv1a(feature);
            v[(int)(h % Dimensions)] += (h & 0x80000000) == 0 ? weight : -weight;
        }
        foreach (var raw in text.ToLowerInvariant().Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            if (raw.Length < 3 || Stop.Contains(raw)) continue;
            Add("w:" + raw, 1f);
            // Character 3- and 4-grams, each word contributing equally whatever its length.
            var padded = "^" + raw + "$";
            var grams = new List<string>();
            for (var n = 3; n <= 4; n++)
                for (var j = 0; j + n <= padded.Length; j++) grams.Add(padded.Substring(j, n));
            var w = 2f / MathF.Sqrt(grams.Count);
            foreach (var g in grams) Add("c:" + g, w);
        }
        var norm = Math.Sqrt(v.Sum(x => (double)x * x));
        if (norm > 0) for (var i = 0; i < v.Length; i++) v[i] = (float)(v[i] / norm);
        return v;
    }

    private static uint Fnv1a(string s)
    {
        var h = 2166136261u;
        foreach (var b in Encoding.UTF8.GetBytes(s)) { h ^= b; h *= 16777619u; }
        return h;
    }
}

/// <summary>Embeddings from an OpenAI-compatible <c>/embeddings</c> endpoint (OpenAI, Azure OpenAI, Ollama, …).</summary>
public sealed class EndpointEmbeddings(ProviderConfig provider, string model, HttpClient http, ILogger<EndpointEmbeddings>? log) : IEmbeddingProvider
{
    private readonly Uri _uri = new(OpenAiCompatibleProvider.BuildChatUri(provider).ToString().Replace("/chat/completions", "/embeddings", StringComparison.Ordinal));

    public string Model => $"{provider.Name}/{model}";

    public async ValueTask<float[]?> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        using var msg = new HttpRequestMessage(HttpMethod.Post, _uri)
        {
            Content = new StringContent(JsonSerializer.Serialize(new Dictionary<string, string> { ["model"] = model, ["input"] = text.Length > 8000 ? text[..8000] : text },
                EmbeddingJson.Default.DictionaryStringString), Encoding.UTF8, "application/json"),
        };
        if (provider.Kind == "azure-openai") msg.Headers.Add("api-key", provider.ApiKey);
        else if (!string.IsNullOrEmpty(provider.ApiKey)) msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", provider.ApiKey);
        try
        {
            using var resp = await http.SendAsync(msg, cancellationToken);
            if (!resp.IsSuccessStatusCode)
            {
                log?.LogWarning("Embeddings request failed: {Status}", (int)resp.StatusCode);
                return null;
            }
            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            var arr = doc.RootElement.GetProperty("data")[0].GetProperty("embedding");
            var v = new float[arr.GetArrayLength()];
            var i = 0;
            foreach (var x in arr.EnumerateArray()) v[i++] = x.GetSingle();
            return v;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
        {
            log?.LogWarning(ex, "Embeddings request failed");
            return null;
        }
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class EmbeddingJson : System.Text.Json.Serialization.JsonSerializerContext;
