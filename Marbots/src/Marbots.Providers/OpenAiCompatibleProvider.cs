using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Marbots.Abstractions;

namespace Marbots.Providers;

/// <summary>
/// Chat Completions client for OpenAI-compatible endpoints: Azure OpenAI (v1 API), OpenAI, DeepSeek,
/// Ollama, LM Studio, vLLM, etc. Uses Utf8JsonWriter / JsonDocument to avoid reflection serialization.
/// </summary>
public sealed class OpenAiCompatibleProvider : IModelProvider
{
    private readonly HttpClient _http;
    private readonly ProviderConfig _config;
    private readonly Uri _chatUri;

    public string Name => _config.Name;

    public OpenAiCompatibleProvider(HttpClient http, ProviderConfig config)
    {
        _http = http;
        _config = config;
        _chatUri = BuildChatUri(config);
    }

    public static Uri BuildChatUri(ProviderConfig config)
    {
        var ep = config.Endpoint.TrimEnd('/');
        if (ep.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)) return new Uri(ep);
        if (config.Kind == "azure-openai" && !ep.Contains("/openai", StringComparison.OrdinalIgnoreCase))
            ep += "/openai/v1";
        else if (config.Kind == "openai" && ep.Equals("https://api.openai.com", StringComparison.OrdinalIgnoreCase))
            ep += "/v1";
        return new Uri(ep + "/chat/completions");
    }

    public async Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken)
    {
        var body = BuildBody(request);
        for (var attempt = 0; ; attempt++)
        {
            using var msg = new HttpRequestMessage(HttpMethod.Post, _chatUri)
            {
                Content = new ByteArrayContent(body) { Headers = { ContentType = new MediaTypeHeaderValue("application/json") } }
            };
            if (_config.Kind == "azure-openai") msg.Headers.Add("api-key", _config.ApiKey);
            else if (!string.IsNullOrEmpty(_config.ApiKey)) msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.ApiKey);

            using var resp = await _http.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (resp.IsSuccessStatusCode)
            {
                await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
                if (request.OnTextDelta is not null) return await ReadStreamAsync(stream, request.OnTextDelta, cancellationToken);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                return Parse(doc.RootElement);
            }

            var transient = resp.StatusCode is HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError;
            if (transient && attempt < 3)
            {
                var delay = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempt) + Random.Shared.Next(250));
                await Task.Delay(delay, cancellationToken);
                continue;
            }
            var error = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new ModelProviderException($"{_config.Name} returned {(int)resp.StatusCode}: {Truncate(error, 600)}", transient);
        }
    }

    internal static byte[] BuildBody(ModelRequest request)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("model", request.Model);
            w.WriteStartArray("messages");
            foreach (var m in request.Messages)
            {
                w.WriteStartObject();
                w.WriteString("role", m.Role);
                if (m.Content is not null) w.WriteString("content", m.Content);
                else w.WriteNull("content");
                if (m.ToolCalls is { Count: > 0 })
                {
                    w.WriteStartArray("tool_calls");
                    foreach (var tc in m.ToolCalls)
                    {
                        w.WriteStartObject();
                        w.WriteString("id", tc.Id);
                        w.WriteString("type", "function");
                        w.WriteStartObject("function");
                        w.WriteString("name", tc.Name);
                        w.WriteString("arguments", tc.Arguments);
                        w.WriteEndObject();
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                }
                if (m.ToolCallId is not null) w.WriteString("tool_call_id", m.ToolCallId);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            if (request.Tools.Count > 0)
            {
                w.WriteStartArray("tools");
                foreach (var t in request.Tools)
                {
                    w.WriteStartObject();
                    w.WriteString("type", "function");
                    w.WriteStartObject("function");
                    w.WriteString("name", t.Name);
                    w.WriteString("description", t.Description);
                    w.WritePropertyName("parameters");
                    w.WriteRawValue(t.ParametersJson, skipInputValidation: false);
                    w.WriteEndObject();
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            if (request.MaxOutputTokens is { } max) w.WriteNumber("max_completion_tokens", max);
            if (request.OnTextDelta is not null)
            {
                w.WriteBoolean("stream", true);
                w.WriteStartObject("stream_options");
                w.WriteBoolean("include_usage", true);
                w.WriteEndObject();
            }
            w.WriteEndObject();
        }
        return buffer.ToArray();
    }

    /// <summary>
    /// Reads an SSE chat-completions stream: text deltas are reported immediately; tool-call fragments are assembled by
    /// index; the final chunk carries usage.
    /// </summary>
    internal static async Task<ModelResponse> ReadStreamAsync(Stream stream, Action<string> onDelta, CancellationToken ct)
    {
        var result = new ModelResponse();
        var text = new StringBuilder();
        var calls = new SortedDictionary<int, (StringBuilder Id, StringBuilder Name, StringBuilder Args)>();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = line.AsSpan(5).Trim();
            if (data.SequenceEqual("[DONE]")) break;
            using var doc = JsonDocument.Parse(data.ToString());
            var root = doc.RootElement;
            if (root.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.String) result.Model = model.GetString() ?? result.Model;
            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                long Get(string n) => usage.TryGetProperty(n, out var v) && v.TryGetInt64(out var l) ? l : 0;
                result.Usage = new ModelUsage(Get("prompt_tokens"), Get("completion_tokens"));
            }
            if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0) continue;
            var choice = choices[0];
            if (choice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String) result.FinishReason = fr.GetString()!;
            if (!choice.TryGetProperty("delta", out var delta)) continue;
            if (delta.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String && c.GetString() is { Length: > 0 } piece)
            {
                text.Append(piece);
                onDelta(piece);
            }
            if (delta.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array)
            {
                foreach (var tc in tcs.EnumerateArray())
                {
                    var index = tc.TryGetProperty("index", out var ix) ? ix.GetInt32() : 0;
                    if (!calls.TryGetValue(index, out var acc)) calls[index] = acc = (new(), new(), new());
                    if (tc.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String) acc.Id.Append(id.GetString());
                    if (tc.TryGetProperty("function", out var fn))
                    {
                        if (fn.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String) acc.Name.Append(n.GetString());
                        if (fn.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.String) acc.Args.Append(a.GetString());
                    }
                }
            }
        }
        result.Content = text.Length == 0 ? null : text.ToString();
        foreach (var (_, acc) in calls)
            result.ToolCalls.Add(new ToolCall { Id = acc.Id.Length > 0 ? acc.Id.ToString() : Ids.New("call"), Name = acc.Name.ToString(), Arguments = acc.Args.Length > 0 ? acc.Args.ToString() : "{}" });
        return result;
    }

    internal static ModelResponse Parse(JsonElement root)
    {
        var result = new ModelResponse();
        if (root.TryGetProperty("model", out var model)) result.Model = model.GetString() ?? "";
        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            long Get(string n) => usage.TryGetProperty(n, out var v) && v.TryGetInt64(out var l) ? l : 0;
            result.Usage = new ModelUsage(Get("prompt_tokens"), Get("completion_tokens"));
        }
        if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0) return result;
        var choice = choices[0];
        if (choice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String) result.FinishReason = fr.GetString()!;
        if (!choice.TryGetProperty("message", out var message)) return result;
        if (message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String) result.Content = content.GetString();
        if (message.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in calls.EnumerateArray())
            {
                var fn = c.GetProperty("function");
                result.ToolCalls.Add(new ToolCall
                {
                    Id = c.TryGetProperty("id", out var id) ? id.GetString() ?? Ids.New("call") : Ids.New("call"),
                    Name = fn.GetProperty("name").GetString() ?? "",
                    Arguments = fn.TryGetProperty("arguments", out var a) ? a.GetString() ?? "{}" : "{}",
                });
            }
        }
        return result;
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}

public sealed class ModelProviderException(string message, bool transient) : Exception(message)
{
    public bool Transient { get; } = transient;
}
