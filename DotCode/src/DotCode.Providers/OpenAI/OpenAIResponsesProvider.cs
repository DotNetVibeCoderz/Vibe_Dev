using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using DotCode.Abstractions;
using DotCode.Providers.Auth;
using DotCode.Providers.Http;

namespace DotCode.Providers.OpenAI;

/// <summary>OpenAI Responses API adapter (primary path for OpenAI and Azure OpenAI v1). Runs stateless
/// (<c>store:false</c>) so the transcript stays owned by DotCode; encrypted reasoning items are round-tripped
/// through <see cref="ProviderOpaque"/>.</summary>
public sealed class OpenAIResponsesProvider : IModelProvider
{
    private readonly string _id;
    private readonly ProviderConfig _config;
    private readonly string _baseUrl;
    private readonly bool _azureAuth;

    public OpenAIResponsesProvider(string id, ProviderConfig config)
    {
        _id = id;
        _config = config;
        _baseUrl = ProviderHttp.TrimSlash(ConfigValue.Expand(config.BaseUrl) ?? "https://api.openai.com/v1");
        _azureAuth = config.Type == "azure" || (config.Quirks?.AuthHeader == "api-key");
        if (string.Equals(config.Auth, "entra", StringComparison.OrdinalIgnoreCase)) _entra = new EntraAuth(config);
    }

    private readonly EntraAuth? _entra;

    /// <summary>Sends with the API key already set, or with a Microsoft Entra bearer token (<c>auth: entra</c>).</summary>
    private async Task<HttpResponseMessage> SendAuthorizedAsync(HttpRequestMessage req, CancellationToken ct)
    {
        if (_entra is null) return await ProviderHttp.SendAsync(_id, req, ct).ConfigureAwait(false);
        req.Headers.Remove("Authorization");
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + (await _entra.GetTokenAsync(ct).ConfigureAwait(false)).Token);
        try { return await ProviderHttp.SendAsync(_id, req, ct).ConfigureAwait(false); }
        catch (ModelProviderException ex) when (ex.Code == "auth")
        {
            _entra.Invalidate();
            throw;
        }
    }

    public string Id => _id;

    public ModelCapabilities GetCapabilities(string modelId) => ModelCatalog.Lookup(modelId, _config);

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var req = new HttpRequestMessage(method, _baseUrl + path);
        var key = ConfigValue.Expand(_config.ApiKey) ?? Environment.GetEnvironmentVariable(_azureAuth ? "AZURE_OPENAI_API_KEY" : "OPENAI_API_KEY");
        if (_entra is null && !string.IsNullOrEmpty(key))
        {
            if (_azureAuth) req.Headers.TryAddWithoutValidation("api-key", key);
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
        using var resp = await SendAuthorizedAsync(req, ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
        var list = new List<ModelInfo>();
        if (doc.RootElement.TryGetProperty("data", out var data))
            foreach (var m in data.EnumerateArray())
                if (m.GetString("id") is { } mid) list.Add(new ModelInfo(_id, mid));
        return list;
    }

    public async IAsyncEnumerable<ModelEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        using var req = CreateRequest(HttpMethod.Post, "/responses");
        req.Content = ProviderHttp.JsonContent(w => WriteBody(w, request));
        using var resp = await SendAuthorizedAsync(req, ct).ConfigureAwait(false);
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

        var usage = new Usage();
        var hasToolCalls = false;
        var stop = StopReason.EndTurn;
        var completed = false;
        var argBuffers = new Dictionary<string, (string CallId, StringBuilder Args)>();

        await foreach (var sse in ProviderHttp.ReadSseAsync(stream, ct).ConfigureAwait(false))
        {
            if (sse.Data.Length == 0 || sse.Data == "[DONE]") continue;
            using var doc = JsonDocument.Parse(sse.Data);
            var root = doc.RootElement;
            var type = root.GetString("type") ?? sse.Event;
            switch (type)
            {
                case "response.created":
                    var r0 = root.GetProp("response");
                    yield return new MessageStarted(r0?.GetString("id"), r0?.GetString("model"));
                    break;
                case "response.output_text.delta":
                    if (root.GetString("delta") is { Length: > 0 } td) yield return new TextDelta(td);
                    break;
                case "response.reasoning_summary_text.delta" or "response.reasoning_text.delta":
                    if (root.GetString("delta") is { Length: > 0 } rd) yield return new ThinkingDelta(rd);
                    break;
                case "response.reasoning_summary_part.done":
                    yield return new ThinkingDelta("\n\n");
                    break;
                case "response.output_item.added":
                {
                    var item = root.GetProperty("item");
                    if (item.GetString("type") == "function_call")
                    {
                        var callId = item.GetString("call_id") ?? item.GetString("id") ?? "";
                        argBuffers[item.GetString("id") ?? callId] = (callId, new StringBuilder());
                        yield return new ToolUseStarted(callId, item.GetString("name") ?? "");
                    }
                    break;
                }
                case "response.function_call_arguments.delta":
                {
                    var itemId = root.GetString("item_id") ?? "";
                    var delta = root.GetString("delta") ?? "";
                    if (argBuffers.TryGetValue(itemId, out var buf))
                    {
                        buf.Args.Append(delta);
                        yield return new ToolInputDelta(buf.CallId, delta);
                    }
                    break;
                }
                case "response.output_item.done":
                {
                    var item = root.GetProperty("item");
                    switch (item.GetString("type"))
                    {
                        case "message":
                            var sb = new StringBuilder();
                            if (item.GetProp("content") is { ValueKind: JsonValueKind.Array } content)
                                foreach (var c in content.EnumerateArray())
                                {
                                    if (c.GetString("type") == "output_text") sb.Append(c.GetString("text"));
                                    else if (c.GetString("type") == "refusal") { sb.Append(c.GetString("refusal")); stop = StopReason.Refusal; }
                                }
                            if (sb.Length > 0) yield return new ContentBlockCompleted(new TextPart(sb.ToString()));
                            break;
                        case "function_call":
                            hasToolCalls = true;
                            var args = item.GetString("arguments") ?? "{}";
                            yield return new ContentBlockCompleted(new ToolUsePart(
                                item.GetString("call_id") ?? item.GetString("id") ?? "",
                                item.GetString("name") ?? "",
                                Anthropic.AnthropicProvider.ParseInput(args)));
                            break;
                        case "reasoning":
                            var summary = new StringBuilder();
                            if (item.GetProp("summary") is { ValueKind: JsonValueKind.Array } parts)
                                foreach (var p in parts.EnumerateArray())
                                {
                                    if (summary.Length > 0) summary.Append("\n\n");
                                    summary.Append(p.GetString("text"));
                                }
                            yield return new ContentBlockCompleted(new ThinkingPart(summary.ToString())
                            {
                                Opaque = item.GetProp("encrypted_content") is not null ? new ProviderOpaque(_id, item.Clone()) : null,
                            });
                            break;
                    }
                    break;
                }
                case "response.completed" or "response.incomplete":
                {
                    var r = root.GetProperty("response");
                    if (r.GetProp("usage") is { } u)
                    {
                        long input = u.GetInt("input_tokens") ?? 0;
                        long cached = u.GetProp("input_tokens_details")?.GetInt("cached_tokens") ?? 0;
                        usage = new Usage
                        {
                            InputTokens = Math.Max(0, input - cached),
                            CacheReadTokens = cached,
                            OutputTokens = u.GetInt("output_tokens") ?? 0,
                            ReasoningTokens = u.GetProp("output_tokens_details")?.GetInt("reasoning_tokens") ?? 0,
                        };
                        yield return new UsageUpdated(usage);
                    }
                    if (type == "response.incomplete" && r.GetProp("incomplete_details")?.GetString("reason") is "max_output_tokens")
                        stop = StopReason.MaxTokens;
                    completed = true;
                    break;
                }
                case "response.failed" or "error":
                {
                    var err = root.GetProp("response")?.GetProp("error") ?? root.GetProp("error") ?? root;
                    var code = err.GetString("code") ?? "server_error";
                    var retryable = code is "rate_limit_exceeded" or "server_error" or "overloaded";
                    throw new ModelProviderException(_id, code == "rate_limit_exceeded" ? "rate_limit" : code, $"{_id}: {err.GetString("message") ?? sse.Data}", retryable);
                }
            }
        }
        if (!completed && ct.IsCancellationRequested) yield break;
        yield return new MessageStopped(hasToolCalls ? StopReason.ToolUse : stop, usage);
    }

    private void WriteBody(Utf8JsonWriter w, ModelRequest r)
    {
        var caps = GetCapabilities(r.Model);
        w.WriteStartObject();
        w.WriteString("model", r.Model);
        w.WriteBoolean("stream", true);
        w.WriteBoolean("store", false);
        w.WriteNumber("max_output_tokens", Math.Min(r.MaxOutputTokens, caps.MaxOutputTokens));
        var instructions = string.Join("\n\n", r.System.Select(s => s.Text));
        if (instructions.Length > 0) w.WriteString("instructions", instructions);

        if (caps.Reasoning == ReasoningSupport.Effort)
        {
            w.WriteStartObject("reasoning");
            var effort = r.Reasoning?.Effort ?? ReasoningEffort.Medium;
            w.WriteString("effort", effort switch
            {
                ReasoningEffort.Off or ReasoningEffort.Low => "low",
                ReasoningEffort.Medium => "medium",
                _ => "high",
            });
            w.WriteString("summary", "auto");
            w.WriteEndObject();
            w.WriteStartArray("include");
            w.WriteStringValue("reasoning.encrypted_content");
            w.WriteEndArray();
        }
        else if (r.Temperature is { } t) w.WriteNumber("temperature", t);

        w.WriteStartArray("input");
        foreach (var m in r.Messages) WriteMessage(w, m, caps);
        w.WriteEndArray();

        if (r.Tools.Count > 0)
        {
            w.WriteStartArray("tools");
            foreach (var tool in r.Tools)
            {
                w.WriteStartObject();
                w.WriteString("type", "function");
                w.WriteString("name", tool.Name);
                w.WriteString("description", tool.Description);
                w.WritePropertyName("parameters");
                SchemaSanitizer.Sanitize(tool.InputSchema, caps.SchemaProfile).WriteTo(w);
                w.WriteBoolean("strict", false);
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
                    w.WriteString("name", r.ToolChoice.ToolName);
                    w.WriteEndObject();
                    break;
            }
            w.WriteBoolean("parallel_tool_calls", caps.ParallelToolCalls);
        }

        if (r.ProviderOptions is not null)
            foreach (var (k, v) in r.ProviderOptions) { w.WritePropertyName(k); v.WriteTo(w); }
        w.WriteEndObject();
    }

    private void WriteMessage(Utf8JsonWriter w, Message m, ModelCapabilities caps)
    {
        if (m.Role == Role.Assistant)
        {
            var textBuffer = new StringBuilder();
            void FlushText()
            {
                if (textBuffer.Length == 0) return;
                w.WriteStartObject();
                w.WriteString("type", "message");
                w.WriteString("role", "assistant");
                w.WriteStartArray("content");
                w.WriteStartObject(); w.WriteString("type", "output_text"); w.WriteString("text", textBuffer.ToString()); w.WriteEndObject();
                w.WriteEndArray();
                w.WriteEndObject();
                textBuffer.Clear();
            }
            foreach (var part in m.Content)
            {
                switch (part)
                {
                    case ThinkingPart { Opaque: { } op } when op.ProviderId == _id:
                        FlushText();
                        op.Payload.WriteTo(w);
                        break;
                    case TextPart tp:
                        textBuffer.Append(tp.Text);
                        break;
                    case ToolUsePart tu:
                        FlushText();
                        w.WriteStartObject();
                        w.WriteString("type", "function_call");
                        w.WriteString("call_id", tu.Id);
                        w.WriteString("name", tu.Name);
                        w.WriteString("arguments", tu.Input.ValueKind == JsonValueKind.Object ? tu.Input.GetRawText() : "{}");
                        w.WriteEndObject();
                        break;
                }
            }
            FlushText();
            return;
        }

        var images = new List<ImagePart>();
        foreach (var tr in m.ToolResults)
        {
            w.WriteStartObject();
            w.WriteString("type", "function_call_output");
            w.WriteString("call_id", tr.ToolUseId);
            var content = tr.TextContent;
            if (tr.IsError && !content.StartsWith("Error", StringComparison.OrdinalIgnoreCase)) content = "Error: " + content;
            w.WriteString("output", content.Length == 0 ? "(no output)" : content);
            w.WriteEndObject();
            images.AddRange(tr.Content.OfType<ImagePart>());
        }
        var rest = m.Content.Where(c => c is TextPart or ImagePart or DocumentPart).ToList();
        if (caps.Vision) rest.AddRange(images);
        if (rest.Count == 0) return;
        w.WriteStartObject();
        w.WriteString("role", "user");
        w.WriteStartArray("content");
        foreach (var p in rest)
        {
            switch (p)
            {
                case TextPart tp:
                    w.WriteStartObject(); w.WriteString("type", "input_text"); w.WriteString("text", tp.Text); w.WriteEndObject();
                    break;
                case ImagePart ip when caps.Vision:
                    w.WriteStartObject(); w.WriteString("type", "input_image"); w.WriteString("image_url", $"data:{ip.MediaType};base64,{ip.Base64Data}"); w.WriteEndObject();
                    break;
                case ImagePart:
                    w.WriteStartObject(); w.WriteString("type", "input_text"); w.WriteString("text", "[image omitted: model has no vision support]"); w.WriteEndObject();
                    break;
                case DocumentPart dp:
                    w.WriteStartObject(); w.WriteString("type", "input_file"); w.WriteString("filename", dp.Name ?? "document.pdf"); w.WriteString("file_data", $"data:{dp.MediaType};base64,{dp.Base64Data}"); w.WriteEndObject();
                    break;
            }
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }
}
