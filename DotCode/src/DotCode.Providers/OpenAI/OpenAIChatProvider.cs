using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using DotCode.Abstractions;
using DotCode.Providers.Http;

namespace DotCode.Providers.OpenAI;

/// <summary>OpenAI Chat Completions adapter. Also serves DeepSeek, Azure OpenAI and any OpenAI-compatible server
/// (LM Studio, vLLM, LiteLLM, OpenRouter, Groq, ...) through declarative <see cref="OpenAIQuirks"/>.</summary>
public sealed class OpenAIChatProvider : IModelProvider
{
    private readonly string _id;
    private readonly ProviderConfig _config;
    private readonly OpenAIQuirks _quirks;
    private readonly string _baseUrl;

    public OpenAIChatProvider(string id, ProviderConfig config)
    {
        _id = id;
        _config = config;
        var profile = config.Profile ?? config.Type switch
        {
            "openai" => "openai",
            "azure" => "azure",
            "deepseek" => "deepseek",
            _ => null,
        };
        _quirks = (config.Quirks ?? new OpenAIQuirks()).MergeOver(OpenAIQuirks.ForProfile(profile));
        _baseUrl = ProviderHttp.TrimSlash(ConfigValue.Expand(config.BaseUrl) ?? config.Type switch
        {
            "deepseek" => "https://api.deepseek.com",
            "openai" => "https://api.openai.com/v1",
            _ => "http://localhost:8000/v1",
        });
    }

    public string Id => _id;

    public ModelCapabilities GetCapabilities(string modelId) => ModelCatalog.Lookup(modelId, _config);

    internal string? ApiKey => ConfigValue.Expand(_config.ApiKey) ?? _config.Type switch
    {
        "openai" => Environment.GetEnvironmentVariable("OPENAI_API_KEY"),
        "azure" => Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY"),
        "deepseek" => Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY"),
        _ => null,
    };

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var req = new HttpRequestMessage(method, _baseUrl + path);
        if (ApiKey is { Length: > 0 } key)
        {
            if (_quirks.AuthHeader == "api-key") req.Headers.TryAddWithoutValidation("api-key", key);
            else req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
        }
        if (_config.Headers is not null)
            foreach (var (k, v) in _config.Headers) req.Headers.TryAddWithoutValidation(k, ConfigValue.Expand(v));
        return req;
    }

    public async ValueTask<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken ct)
    {
        if (_config.Models is { Count: > 0 } declared) return [.. declared.Select(m => new ModelInfo(_id, m))];
        using var req = CreateRequest(HttpMethod.Get, "/models");
        using var resp = await ProviderHttp.SendAsync(_id, req, ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
        var list = new List<ModelInfo>();
        if (doc.RootElement.TryGetProperty("data", out var data))
            foreach (var m in data.EnumerateArray())
                if (m.GetString("id") is { } mid) list.Add(new ModelInfo(_id, mid));
        return list;
    }

    public async IAsyncEnumerable<ModelEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        using var req = CreateRequest(HttpMethod.Post, "/chat/completions");
        req.Content = ProviderHttp.JsonContent(w => WriteBody(w, request));
        using var resp = await ProviderHttp.SendAsync(_id, req, ct).ConfigureAwait(false);
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

        var text = new StringBuilder();
        var reasoning = new StringBuilder();
        var tools = new SortedDictionary<int, ToolState>();
        var usage = new Usage();
        StopReason? stop = null;
        var started = false;
        var reasoningField = _quirks.ReasoningField ?? "reasoning_content";

        await foreach (var sse in ProviderHttp.ReadSseAsync(stream, ct).ConfigureAwait(false))
        {
            if (sse.Data.Length == 0) continue;
            if (sse.Data == "[DONE]") break;
            using var doc = JsonDocument.Parse(sse.Data);
            var root = doc.RootElement;
            if (root.GetProp("error") is { } err)
                throw new ModelProviderException(_id, "server_error", $"{_id}: {err.GetString("message") ?? err.GetRawText()}", true);
            if (!started)
            {
                started = true;
                yield return new MessageStarted(root.GetString("id"), root.GetString("model"));
            }
            if (root.GetProp("usage") is { ValueKind: JsonValueKind.Object } u)
            {
                usage = ReadUsage(u);
                yield return new UsageUpdated(usage);
            }
            if (root.GetProp("choices") is not { ValueKind: JsonValueKind.Array } choices || choices.GetArrayLength() == 0) continue;
            var choice = choices[0];
            if (choice.GetProp("delta") is { ValueKind: JsonValueKind.Object } delta)
            {
                if ((delta.GetString(reasoningField) ?? delta.GetString("reasoning")) is { Length: > 0 } r)
                {
                    reasoning.Append(r);
                    yield return new ThinkingDelta(r);
                }
                if (delta.GetString("content") is { Length: > 0 } c)
                {
                    text.Append(c);
                    yield return new TextDelta(c);
                }
                if (delta.GetProp("tool_calls") is { ValueKind: JsonValueKind.Array } calls)
                {
                    foreach (var call in calls.EnumerateArray())
                    {
                        var index = call.GetInt("index") ?? tools.Count;
                        if (!tools.TryGetValue(index, out var st)) tools[index] = st = new ToolState();
                        if (call.GetString("id") is { Length: > 0 } cid) st.Id = cid;
                        if (call.GetProp("function") is { } fn)
                        {
                            if (fn.GetString("name") is { Length: > 0 } name)
                            {
                                st.Name += name;
                                if (!st.Announced && st.Id is not null)
                                {
                                    st.Announced = true;
                                    yield return new ToolUseStarted(st.Id, st.Name);
                                }
                            }
                            if (fn.GetString("arguments") is { Length: > 0 } args)
                            {
                                st.Args.Append(args);
                                yield return new ToolInputDelta(st.Id ?? "", args);
                            }
                        }
                    }
                }
            }
            if (choice.GetString("finish_reason") is { } fr) stop = MapFinish(fr);
        }

        if (reasoning.Length > 0) yield return new ContentBlockCompleted(new ThinkingPart(reasoning.ToString()) { Opaque = new ProviderOpaque(_id, DotCodeJson.EmptyObject) });
        if (text.Length > 0) yield return new ContentBlockCompleted(new TextPart(text.ToString()));
        foreach (var st in tools.Values)
        {
            if (string.IsNullOrEmpty(st.Name)) continue;
            var toolId = string.IsNullOrEmpty(st.Id) ? "call_" + Guid.NewGuid().ToString("n")[..24] : st.Id;
            yield return new ContentBlockCompleted(new ToolUsePart(toolId, st.Name, Anthropic.AnthropicProvider.ParseInput(st.Args.ToString())));
        }
        if (ct.IsCancellationRequested) yield break;
        var reason = tools.Count > 0 ? StopReason.ToolUse : stop ?? StopReason.EndTurn;
        yield return new MessageStopped(reason, usage);
    }

    private sealed class ToolState
    {
        public string? Id;
        public string Name = "";
        public readonly StringBuilder Args = new();
        public bool Announced;
    }

    internal static Usage ReadUsage(JsonElement u)
    {
        long prompt = u.GetInt("prompt_tokens") ?? 0;
        long cached = u.GetProp("prompt_tokens_details")?.GetInt("cached_tokens") ?? u.GetInt("prompt_cache_hit_tokens") ?? 0;
        return new Usage
        {
            InputTokens = Math.Max(0, prompt - cached),
            CacheReadTokens = cached,
            OutputTokens = u.GetInt("completion_tokens") ?? 0,
            ReasoningTokens = u.GetProp("completion_tokens_details")?.GetInt("reasoning_tokens") ?? 0,
        };
    }

    private static StopReason MapFinish(string s) => s switch
    {
        "tool_calls" or "function_call" => StopReason.ToolUse,
        "length" => StopReason.MaxTokens,
        "content_filter" => StopReason.Refusal,
        _ => StopReason.EndTurn,
    };

    private bool IsReasoningModel(string model) =>
        GetCapabilities(model).Reasoning is ReasoningSupport.Effort or ReasoningSupport.Always;

    private void WriteBody(Utf8JsonWriter w, ModelRequest r)
    {
        var caps = GetCapabilities(r.Model);
        w.WriteStartObject();
        w.WriteString("model", r.Model);
        w.WriteBoolean("stream", true);
        if (_quirks.SupportsStreamUsage != false)
        {
            w.WriteStartObject("stream_options");
            w.WriteBoolean("include_usage", true);
            w.WriteEndObject();
        }
        w.WriteNumber(_quirks.MaxTokensParam ?? "max_tokens", Math.Min(r.MaxOutputTokens, caps.MaxOutputTokens));
        var reasoningModel = IsReasoningModel(r.Model);
        if (r.Temperature is { } t && _quirks.SupportsTemperature != false && !(reasoningModel && _config.Type is "openai" or "azure"))
            w.WriteNumber("temperature", t);
        if (r.Reasoning is { Effort: not ReasoningEffort.Off } ro && _quirks.SupportsReasoningEffort == true && caps.Reasoning == ReasoningSupport.Effort)
            w.WriteString("reasoning_effort", ro.Effort switch { ReasoningEffort.Low => "low", ReasoningEffort.Medium => "medium", _ => "high" });

        w.WriteStartArray("messages");
        var systemText = string.Join("\n\n", r.System.Select(s => s.Text));
        if (systemText.Length > 0)
        {
            w.WriteStartObject();
            w.WriteString("role", _quirks.RoleForSystem ?? "system");
            w.WriteString("content", systemText);
            w.WriteEndObject();
        }
        WriteMessages(w, r.Messages, caps);
        w.WriteEndArray();

        if (r.Tools.Count > 0)
        {
            w.WriteStartArray("tools");
            foreach (var tool in r.Tools)
            {
                w.WriteStartObject();
                w.WriteString("type", "function");
                w.WriteStartObject("function");
                w.WriteString("name", tool.Name);
                w.WriteString("description", tool.Description);
                w.WritePropertyName("parameters");
                SchemaSanitizer.Sanitize(tool.InputSchema, caps.SchemaProfile == JsonSchemaProfile.Strict ? JsonSchemaProfile.Strict : caps.SchemaProfile).WriteTo(w);
                w.WriteEndObject();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            switch (r.ToolChoice.Kind)
            {
                case ToolChoiceKind.Any: w.WriteString("tool_choice", "required"); break;
                case ToolChoiceKind.None: w.WriteString("tool_choice", "none"); break;
                case ToolChoiceKind.Tool:
                    w.WriteStartObject("tool_choice");
                    w.WriteString("type", "function");
                    w.WriteStartObject("function"); w.WriteString("name", r.ToolChoice.ToolName); w.WriteEndObject();
                    w.WriteEndObject();
                    break;
            }
            if (_quirks.ParallelTools is { } pt && caps.ParallelToolCalls && r.ToolChoice.Kind != ToolChoiceKind.None) w.WriteBoolean("parallel_tool_calls", pt);
        }

        if (r.ProviderOptions is not null)
            foreach (var (k, v) in r.ProviderOptions) { w.WritePropertyName(k); v.WriteTo(w); }
        w.WriteEndObject();
    }

    private void WriteMessages(Utf8JsonWriter w, IReadOnlyList<Message> messages, ModelCapabilities caps)
    {
        // Reasoning is echoed back only for assistant messages in the current turn (after the last real user prompt).
        var turnStart = 0;
        for (var i = messages.Count - 1; i >= 0; i--)
            if (messages[i].Role == Role.User && !messages[i].HasToolResults) { turnStart = i; break; }

        for (var i = 0; i < messages.Count; i++)
        {
            var m = messages[i];
            if (m.Role == Role.Assistant)
            {
                var text = m.Text;
                var calls = m.ToolUses.ToList();
                if (text.Length == 0 && calls.Count == 0) continue;
                w.WriteStartObject();
                w.WriteString("role", "assistant");
                if (text.Length > 0) w.WriteString("content", text); else w.WriteNull("content");
                if (_quirks.SendReasoningBack == true && i > turnStart && m.ProviderId == _id &&
                    m.Content.OfType<ThinkingPart>().FirstOrDefault() is { Text.Length: > 0 } th)
                    w.WriteString(_quirks.ReasoningField ?? "reasoning_content", th.Text);
                if (calls.Count > 0)
                {
                    w.WriteStartArray("tool_calls");
                    foreach (var c in calls)
                    {
                        w.WriteStartObject();
                        w.WriteString("id", c.Id);
                        w.WriteString("type", "function");
                        w.WriteStartObject("function");
                        w.WriteString("name", c.Name);
                        w.WriteString("arguments", c.Input.ValueKind == JsonValueKind.Object ? c.Input.GetRawText() : "{}");
                        w.WriteEndObject();
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                }
                w.WriteEndObject();
                continue;
            }

            // User message: tool results become role=tool messages; remaining parts form a user message.
            var deferredImages = new List<ImagePart>();
            foreach (var tr in m.ToolResults)
            {
                w.WriteStartObject();
                w.WriteString("role", "tool");
                w.WriteString("tool_call_id", tr.ToolUseId);
                var content = tr.TextContent;
                if (tr.IsError && !content.StartsWith("Error", StringComparison.OrdinalIgnoreCase)) content = "Error: " + content;
                w.WriteString("content", content.Length == 0 ? "(no output)" : content);
                w.WriteEndObject();
                deferredImages.AddRange(tr.Content.OfType<ImagePart>());
            }
            var rest = m.Content.Where(c => c is TextPart or ImagePart or DocumentPart).ToList();
            if (deferredImages.Count > 0 && caps.Vision) rest.AddRange(deferredImages);
            if (rest.Count == 0) continue;
            w.WriteStartObject();
            w.WriteString("role", "user");
            if (rest.All(p => p is TextPart))
            {
                w.WriteString("content", string.Concat(rest.Cast<TextPart>().Select(p => p.Text)));
            }
            else
            {
                w.WriteStartArray("content");
                foreach (var p in rest)
                {
                    switch (p)
                    {
                        case TextPart tp:
                            w.WriteStartObject(); w.WriteString("type", "text"); w.WriteString("text", tp.Text); w.WriteEndObject();
                            break;
                        case ImagePart ip when caps.Vision:
                            w.WriteStartObject();
                            w.WriteString("type", "image_url");
                            w.WriteStartObject("image_url"); w.WriteString("url", $"data:{ip.MediaType};base64,{ip.Base64Data}"); w.WriteEndObject();
                            w.WriteEndObject();
                            break;
                        case ImagePart:
                            w.WriteStartObject(); w.WriteString("type", "text"); w.WriteString("text", "[image omitted: model has no vision support]"); w.WriteEndObject();
                            break;
                        case DocumentPart dp:
                            w.WriteStartObject();
                            w.WriteString("type", "file");
                            w.WriteStartObject("file");
                            w.WriteString("filename", dp.Name ?? "document.pdf");
                            w.WriteString("file_data", $"data:{dp.MediaType};base64,{dp.Base64Data}");
                            w.WriteEndObject();
                            w.WriteEndObject();
                            break;
                    }
                }
                w.WriteEndArray();
            }
            w.WriteEndObject();
        }
    }
}
