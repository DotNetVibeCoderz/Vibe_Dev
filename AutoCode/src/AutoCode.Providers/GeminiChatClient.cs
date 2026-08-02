// Auto Code — Gravicode Studios (Kang Fadhil)

using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoCode.Core.Configuration;
using AutoCode.Providers.Internal;
using Microsoft.Extensions.AI;

namespace AutoCode.Providers;

/// <summary>
/// <see cref="IChatClient"/> over Google Gemini <c>streamGenerateContent</c>.
///
/// EN: Gemini names function calls but does not give them ids, so ids are synthesised here and the
/// mapping is kept for the round trip — otherwise tool results cannot be paired back up.
/// ID: Gemini tidak memberi id pada function call, sehingga id dibuat di sini dan pemetaannya disimpan
/// agar hasil tool bisa dipasangkan kembali.
/// </summary>
public sealed class GeminiChatClient : IChatClient
{
    private readonly HttpClient _http;
    private readonly ProviderProfile _profile;
    private readonly bool _ownsHttp;
    private readonly ChatClientMetadata _metadata;

    public GeminiChatClient(ProviderProfile profile, HttpClient? http = null)
    {
        _profile = profile;
        _ownsHttp = http is null;
        _http = http ?? new HttpClient();
        _http.Timeout = TimeSpan.FromSeconds(profile.TimeoutSeconds);

        var endpoint = profile.ResolveEndpoint() ?? "https://generativelanguage.googleapis.com/v1beta";
        _metadata = new ChatClientMetadata("gemini", new Uri(endpoint), profile.Model);
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        if (serviceKey is not null)
            return null;

        return serviceType == typeof(ChatClientMetadata) ? _metadata
             : serviceType.IsInstanceOfType(this) ? this
             : null;
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
            updates.Add(update);

        return updates.ToChatResponse();
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var request = BuildRequest(messages, options);
        using var response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException($"Gemini request failed ({(int)response.StatusCode}): {Truncate(body)}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        var responseId = Guid.NewGuid().ToString("n");

        await foreach (var frame in ServerSentEvents.ReadAsync(stream, cancellationToken).ConfigureAwait(false))
        {
            if (frame.Data is "[DONE]" or "")
                continue;

            JsonNode? node;
            try
            {
                node = JsonNode.Parse(frame.Data);
            }
            catch (JsonException)
            {
                continue;
            }

            if (node is not JsonObject obj)
                continue;

            if (obj["error"] is JsonObject error)
                throw new InvalidOperationException($"Gemini stream error: {error["message"]?.GetValue<string>() ?? "unknown"}");

            var candidate = obj["candidates"]?.AsArray().FirstOrDefault();
            var parts = candidate?["content"]?["parts"]?.AsArray();

            if (parts is not null)
            {
                foreach (var part in parts)
                {
                    if (part is not JsonObject p)
                        continue;

                    if (p["functionCall"] is JsonObject call)
                    {
                        var name = call["name"]?.GetValue<string>() ?? "";
                        var args = call["args"] is JsonObject argsObj
                            ? JsonSerializer.Deserialize<Dictionary<string, object?>>(argsObj.ToJsonString()) ?? []
                            : [];

                        yield return Update(responseId, new FunctionCallContent(
                            $"gemini-{Guid.NewGuid():n}", name, args));
                        continue;
                    }

                    var text = p["text"]?.GetValue<string>();
                    if (string.IsNullOrEmpty(text))
                        continue;

                    var isThought = p["thought"]?.GetValue<bool>() ?? false;
                    yield return Update(responseId, isThought
                        ? new TextReasoningContent(text)
                        : new TextContent(text));
                }
            }

            if (obj["usageMetadata"] is JsonObject usageNode)
            {
                var input = usageNode["promptTokenCount"]?.GetValue<long>() ?? 0;
                var output = usageNode["candidatesTokenCount"]?.GetValue<long>() ?? 0;

                yield return new ChatResponseUpdate(ChatRole.Assistant, [
                    new UsageContent(new UsageDetails
                    {
                        InputTokenCount = input,
                        OutputTokenCount = output,
                        TotalTokenCount = usageNode["totalTokenCount"]?.GetValue<long>() ?? input + output,
                    })])
                {
                    ResponseId = responseId,
                    FinishReason = MapFinishReason(candidate?["finishReason"]?.GetValue<string>()),
                };
            }
        }
    }

    private HttpRequestMessage BuildRequest(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        var endpoint = (_profile.ResolveEndpoint() ?? "https://generativelanguage.googleapis.com/v1beta").TrimEnd('/');
        var model = options?.ModelId ?? _profile.Model;

        var apiKey = _profile.ResolveApiKey()
            ?? throw new InvalidOperationException(
                "Gemini API key is not configured. Set GEMINI_API_KEY or providers.gemini.apiKey.");

        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{endpoint}/models/{model}:streamGenerateContent?alt=sse");

        request.Headers.TryAddWithoutValidation("x-goog-api-key", apiKey);
        foreach (var (name, value) in _profile.Headers)
            request.Headers.TryAddWithoutValidation(name, value);

        var payload = new JsonObject();
        var (system, contents) = ConvertMessages(messages);

        if (system.Length > 0)
        {
            payload["systemInstruction"] = new JsonObject
            {
                ["parts"] = new JsonArray(new JsonObject { ["text"] = system.ToString() }),
            };
        }

        payload["contents"] = contents;

        var generation = new JsonObject();
        if ((options?.Temperature ?? _profile.Temperature) is { } temperature)
            generation["temperature"] = temperature;
        if ((options?.MaxOutputTokens ?? _profile.MaxOutputTokens) is { } maxTokens)
            generation["maxOutputTokens"] = maxTokens;
        if (_profile.EnableExtendedThinking)
        {
            generation["thinkingConfig"] = new JsonObject
            {
                ["thinkingBudget"] = _profile.ThinkingBudgetTokens,
                ["includeThoughts"] = true,
            };
        }

        if (generation.Count > 0)
            payload["generationConfig"] = generation;

        if (options?.Tools is { Count: > 0 } tools)
        {
            var declarations = new JsonArray();
            foreach (var tool in tools.OfType<AIFunction>())
            {
                declarations.Add(new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = JsonSchemaSanitizer.ForGemini(tool.JsonSchema),
                });
            }

            if (declarations.Count > 0)
            {
                payload["tools"] = new JsonArray(new JsonObject { ["functionDeclarations"] = declarations });
                payload["toolConfig"] = new JsonObject
                {
                    ["functionCallingConfig"] = new JsonObject
                    {
                        ["mode"] = options.ToolMode is RequiredChatToolMode ? "ANY" : "AUTO",
                    },
                };
            }
        }

        request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return request;
    }

    /// <summary>
    /// Maps the neutral message list onto Gemini's <c>contents</c>: system hoisted out, assistant
    /// mapped to <c>model</c>, and tool results expressed as <c>functionResponse</c> parts on a user turn.
    /// </summary>
    private static (StringBuilder System, JsonArray Contents) ConvertMessages(IEnumerable<ChatMessage> messages)
    {
        var system = new StringBuilder();
        var contents = new JsonArray();

        // Gemini pairs a functionResponse to its call by name, so remember what each id was called.
        var callNames = new Dictionary<string, string>(StringComparer.Ordinal);

        string? lastRole = null;
        JsonArray? lastParts = null;

        void Append(string role, JsonNode part)
        {
            if (lastRole == role && lastParts is not null)
            {
                lastParts.Add(part);
                return;
            }

            lastParts = [part];
            lastRole = role;
            contents.Add(new JsonObject { ["role"] = role, ["parts"] = lastParts });
        }

        foreach (var message in messages)
        {
            if (message.Role == ChatRole.System)
            {
                if (system.Length > 0) system.Append("\n\n");
                system.Append(message.Text);
                continue;
            }

            var role = message.Role == ChatRole.Assistant ? "model" : "user";

            foreach (var content in message.Contents)
            {
                switch (content)
                {
                    case TextContent { Text.Length: > 0 } text:
                        Append(role, new JsonObject { ["text"] = text.Text });
                        break;

                    case FunctionCallContent call:
                        callNames[call.CallId] = call.Name;
                        Append("model", new JsonObject
                        {
                            ["functionCall"] = new JsonObject
                            {
                                ["name"] = call.Name,
                                ["args"] = call.Arguments is { Count: > 0 }
                                    ? JsonNode.Parse(JsonSerializer.Serialize(call.Arguments))
                                    : new JsonObject(),
                            },
                        });
                        break;

                    case FunctionResultContent result:
                        Append("user", new JsonObject
                        {
                            ["functionResponse"] = new JsonObject
                            {
                                ["name"] = callNames.GetValueOrDefault(result.CallId, "tool"),
                                ["response"] = new JsonObject
                                {
                                    ["result"] = result.Result?.ToString() ?? "",
                                },
                            },
                        });
                        break;
                }
            }
        }

        return (system, contents);
    }

    private static ChatResponseUpdate Update(string responseId, AIContent content) =>
        new(ChatRole.Assistant, [content]) { ResponseId = responseId };

    private static ChatFinishReason? MapFinishReason(string? reason) => reason switch
    {
        "STOP" => ChatFinishReason.Stop,
        "MAX_TOKENS" => ChatFinishReason.Length,
        "SAFETY" or "PROHIBITED_CONTENT" or "BLOCKLIST" => ChatFinishReason.ContentFilter,
        _ => null,
    };

    private static string Truncate(string value) =>
        value.Length <= 800 ? value : value[..800] + "…";

    public void Dispose()
    {
        if (_ownsHttp)
            _http.Dispose();
    }
}
