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
/// <see cref="IChatClient"/> over the Anthropic Messages API, written directly against the wire
/// format so extended thinking, tool use and streaming usage accounting all work.
///
/// EN: implemented natively rather than through a shim because Auto Code needs thinking blocks and
/// their signatures preserved across tool-use round trips — a detail generic adapters tend to drop.
/// ID: diimplementasikan langsung ke format wire agar blok thinking beserta signature-nya tetap utuh
/// pada setiap putaran pemanggilan tool.
/// </summary>
public sealed class AnthropicChatClient : IChatClient
{
    private const string AnthropicVersion = "2023-06-01";

    private readonly HttpClient _http;
    private readonly ProviderProfile _profile;
    private readonly bool _ownsHttp;
    private readonly ChatClientMetadata _metadata;

    public AnthropicChatClient(ProviderProfile profile, HttpClient? http = null)
    {
        _profile = profile;
        _ownsHttp = http is null;
        _http = http ?? new HttpClient();
        _http.Timeout = TimeSpan.FromSeconds(profile.TimeoutSeconds);

        var endpoint = profile.ResolveEndpoint() ?? "https://api.anthropic.com/v1";
        _metadata = new ChatClientMetadata("anthropic", new Uri(endpoint), profile.Model);
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
            throw new InvalidOperationException($"Anthropic request failed ({(int)response.StatusCode}): {Truncate(body)}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        var responseId = Guid.NewGuid().ToString("n");
        var blocks = new Dictionary<int, PendingBlock>();
        long inputTokens = 0;
        long cacheReadTokens = 0;

        await foreach (var frame in ServerSentEvents.ReadAsync(stream, cancellationToken).ConfigureAwait(false))
        {
            if (frame.Data is "[DONE]")
                break;

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

            var type = obj["type"]?.GetValue<string>();

            switch (type)
            {
                case "message_start":
                    {
                        responseId = obj["message"]?["id"]?.GetValue<string>() ?? responseId;
                        var usage = obj["message"]?["usage"];
                        inputTokens = usage?["input_tokens"]?.GetValue<long>() ?? 0;
                        cacheReadTokens = usage?["cache_read_input_tokens"]?.GetValue<long>() ?? 0;
                        break;
                    }

                case "content_block_start":
                    {
                        var index = obj["index"]?.GetValue<int>() ?? 0;
                        var block = obj["content_block"];
                        var blockType = block?["type"]?.GetValue<string>();

                        blocks[index] = new PendingBlock
                        {
                            Kind = blockType ?? "text",
                            ToolCallId = block?["id"]?.GetValue<string>(),
                            ToolName = block?["name"]?.GetValue<string>(),
                        };
                        break;
                    }

                case "content_block_delta":
                    {
                        var index = obj["index"]?.GetValue<int>() ?? 0;
                        var delta = obj["delta"];
                        var deltaType = delta?["type"]?.GetValue<string>();

                        if (!blocks.TryGetValue(index, out var pending))
                            blocks[index] = pending = new PendingBlock { Kind = "text" };

                        switch (deltaType)
                        {
                            case "text_delta":
                                {
                                    var text = delta?["text"]?.GetValue<string>() ?? "";
                                    if (text.Length > 0)
                                        yield return Update(responseId, new TextContent(text));
                                    break;
                                }

                            case "thinking_delta":
                                {
                                    var text = delta?["thinking"]?.GetValue<string>() ?? "";
                                    pending.Text.Append(text);
                                    if (text.Length > 0)
                                        yield return Update(responseId, new TextReasoningContent(text));
                                    break;
                                }

                            case "signature_delta":
                                pending.Signature.Append(delta?["signature"]?.GetValue<string>() ?? "");
                                break;

                            case "input_json_delta":
                                pending.Json.Append(delta?["partial_json"]?.GetValue<string>() ?? "");
                                break;
                        }

                        break;
                    }

                case "content_block_stop":
                    {
                        var index = obj["index"]?.GetValue<int>() ?? 0;
                        if (!blocks.Remove(index, out var pending))
                            break;

                        if (pending.Kind == "tool_use" && pending.ToolName is not null)
                        {
                            var arguments = ParseArguments(pending.Json.ToString());
                            yield return Update(responseId, new FunctionCallContent(
                                pending.ToolCallId ?? Guid.NewGuid().ToString("n"),
                                pending.ToolName,
                                arguments));
                        }
                        else if (pending.Kind == "thinking" && pending.Signature.Length > 0)
                        {
                            // Replayed verbatim on the next request; Anthropic rejects unsigned thinking.
                            var reasoning = new TextReasoningContent("")
                            {
                                AdditionalProperties = new AdditionalPropertiesDictionary
                                {
                                    ["anthropic.signature"] = pending.Signature.ToString(),
                                    ["anthropic.thinking"] = pending.Text.ToString(),
                                },
                            };
                            yield return Update(responseId, reasoning);
                        }

                        break;
                    }

                case "message_delta":
                    {
                        var outputTokens = obj["usage"]?["output_tokens"]?.GetValue<long>() ?? 0;
                        var usage = new UsageDetails
                        {
                            InputTokenCount = inputTokens,
                            OutputTokenCount = outputTokens,
                            TotalTokenCount = inputTokens + outputTokens,
                        };

                        if (cacheReadTokens > 0)
                            usage.AdditionalCounts = new AdditionalPropertiesDictionary<long> { ["cache_read"] = cacheReadTokens };

                        var stop = obj["delta"]?["stop_reason"]?.GetValue<string>();
                        yield return new ChatResponseUpdate(ChatRole.Assistant, [new UsageContent(usage)])
                        {
                            ResponseId = responseId,
                            FinishReason = MapFinishReason(stop),
                        };
                        break;
                    }

                case "error":
                    {
                        var message = obj["error"]?["message"]?.GetValue<string>() ?? "unknown error";
                        throw new InvalidOperationException($"Anthropic stream error: {message}");
                    }
            }
        }
    }

    private HttpRequestMessage BuildRequest(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        var endpoint = (_profile.ResolveEndpoint() ?? "https://api.anthropic.com/v1").TrimEnd('/');
        var request = new HttpRequestMessage(HttpMethod.Post, $"{endpoint}/messages");

        var apiKey = _profile.ResolveApiKey()
            ?? throw new InvalidOperationException(
                "Anthropic API key is not configured. Set ANTHROPIC_API_KEY or providers.anthropic.apiKey.");

        request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
        request.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);
        foreach (var (name, value) in _profile.Headers)
            request.Headers.TryAddWithoutValidation(name, value);

        var payload = new JsonObject
        {
            ["model"] = options?.ModelId ?? _profile.Model,
            ["max_tokens"] = options?.MaxOutputTokens ?? _profile.MaxOutputTokens ?? 8192,
            ["stream"] = true,
        };

        var (system, converted) = ConvertMessages(messages);
        if (system.Length > 0)
            payload["system"] = system.ToString();

        payload["messages"] = converted;

        if (_profile.EnableExtendedThinking)
        {
            payload["thinking"] = new JsonObject
            {
                ["type"] = "enabled",
                ["budget_tokens"] = _profile.ThinkingBudgetTokens,
            };
            // Anthropic requires the default temperature while thinking is enabled.
        }
        else if ((options?.Temperature ?? _profile.Temperature) is { } temperature)
        {
            payload["temperature"] = temperature;
        }

        if (options?.Tools is { Count: > 0 } tools)
        {
            var declarations = new JsonArray();
            foreach (var tool in tools.OfType<AIFunction>())
            {
                declarations.Add(new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["input_schema"] = JsonNode.Parse(tool.JsonSchema.GetRawText()),
                });
            }

            if (declarations.Count > 0)
            {
                payload["tools"] = declarations;
                payload["tool_choice"] = new JsonObject
                {
                    ["type"] = options.ToolMode is RequiredChatToolMode ? "any" : "auto",
                    ["disable_parallel_tool_use"] = !_profile.SupportsParallelToolCalls,
                };
            }
        }

        request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return request;
    }

    /// <summary>
    /// Maps the neutral message list onto Anthropic's shape: a hoisted system prompt, tool results
    /// folded into user turns, and consecutive same-role messages merged (the API rejects runs).
    /// </summary>
    private static (StringBuilder System, JsonArray Messages) ConvertMessages(IEnumerable<ChatMessage> messages)
    {
        var system = new StringBuilder();
        var result = new JsonArray();
        string? lastRole = null;
        JsonArray? lastContent = null;

        void Append(string role, JsonNode block)
        {
            if (lastRole == role && lastContent is not null)
            {
                lastContent.Add(block);
                return;
            }

            lastContent = [block];
            lastRole = role;
            result.Add(new JsonObject { ["role"] = role, ["content"] = lastContent });
        }

        foreach (var message in messages)
        {
            if (message.Role == ChatRole.System)
            {
                if (system.Length > 0) system.Append("\n\n");
                system.Append(message.Text);
                continue;
            }

            var role = message.Role == ChatRole.Assistant ? "assistant" : "user";

            foreach (var content in message.Contents)
            {
                switch (content)
                {
                    case TextContent { Text.Length: > 0 } text:
                        Append(role, new JsonObject { ["type"] = "text", ["text"] = text.Text });
                        break;

                    case TextReasoningContent reasoning
                        when reasoning.AdditionalProperties?.TryGetValue("anthropic.signature", out var signature) == true:
                        Append("assistant", new JsonObject
                        {
                            ["type"] = "thinking",
                            ["thinking"] = reasoning.AdditionalProperties.TryGetValue("anthropic.thinking", out var raw)
                                ? raw?.ToString() ?? reasoning.Text
                                : reasoning.Text,
                            ["signature"] = signature?.ToString(),
                        });
                        break;

                    case FunctionCallContent call:
                        Append("assistant", new JsonObject
                        {
                            ["type"] = "tool_use",
                            ["id"] = call.CallId,
                            ["name"] = call.Name,
                            ["input"] = call.Arguments is { Count: > 0 }
                                ? JsonNode.Parse(JsonSerializer.Serialize(call.Arguments))
                                : new JsonObject(),
                        });
                        break;

                    case FunctionResultContent result_:
                        Append("user", new JsonObject
                        {
                            ["type"] = "tool_result",
                            ["tool_use_id"] = result_.CallId,
                            ["content"] = result_.Result?.ToString() ?? "",
                        });
                        break;
                }
            }
        }

        return (system, result);
    }

    private static ChatResponseUpdate Update(string responseId, AIContent content) =>
        new(ChatRole.Assistant, [content]) { ResponseId = responseId };

    private static Dictionary<string, object?> ParseArguments(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(json) ?? [];
        }
        catch (JsonException)
        {
            return new Dictionary<string, object?> { ["__raw"] = json };
        }
    }

    private static ChatFinishReason? MapFinishReason(string? stopReason) => stopReason switch
    {
        "end_turn" or "stop_sequence" => ChatFinishReason.Stop,
        "max_tokens" => ChatFinishReason.Length,
        "tool_use" => ChatFinishReason.ToolCalls,
        "refusal" => ChatFinishReason.ContentFilter,
        _ => null,
    };

    private static string Truncate(string value) =>
        value.Length <= 800 ? value : value[..800] + "…";

    public void Dispose()
    {
        if (_ownsHttp)
            _http.Dispose();
    }

    private sealed class PendingBlock
    {
        public required string Kind { get; init; }
        public string? ToolCallId { get; init; }
        public string? ToolName { get; init; }
        public StringBuilder Json { get; } = new();
        public StringBuilder Text { get; } = new();
        public StringBuilder Signature { get; } = new();
    }
}
