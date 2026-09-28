using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using DotCode.Abstractions;
using DotCode.Providers.Http;

namespace DotCode.Providers.Ollama;

/// <summary>Native Ollama <c>/api/chat</c> adapter. Raises <c>num_ctx</c> automatically (Ollama's small default
/// silently truncates long agent prompts) and probes model capabilities via <c>/api/show</c>.</summary>
public sealed class OllamaProvider(string id, ProviderConfig config) : IModelProvider
{
    private readonly string _baseUrl = ProviderHttp.TrimSlash(ConfigValue.Expand(config.BaseUrl) ?? Environment.GetEnvironmentVariable("OLLAMA_HOST") ?? "http://localhost:11434");
    private readonly ConcurrentDictionary<string, ModelCapabilities> _probed = new();

    public string Id => id;

    public ModelCapabilities GetCapabilities(string modelId) =>
        _probed.TryGetValue(modelId, out var c) ? c : ModelCatalog.Lookup(modelId, config);

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var req = new HttpRequestMessage(method, _baseUrl + path);
        if (ConfigValue.Expand(config.ApiKey) is { Length: > 0 } key) req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
        if (config.Headers is not null)
            foreach (var (k, v) in config.Headers) req.Headers.TryAddWithoutValidation(k, ConfigValue.Expand(v));
        return req;
    }

    public async ValueTask<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken ct)
    {
        using var req = CreateRequest(HttpMethod.Get, "/api/tags");
        using var resp = await ProviderHttp.SendAsync(id, req, ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
        var list = new List<ModelInfo>();
        if (doc.RootElement.TryGetProperty("models", out var models))
            foreach (var m in models.EnumerateArray())
                if (m.GetString("name") is { } n) list.Add(new ModelInfo(id, n));
        return list;
    }

    /// <summary>Queries <c>/api/show</c> for tools/vision/thinking support and native context length.</summary>
    public async ValueTask<ModelCapabilities> ProbeAsync(string model, CancellationToken ct)
    {
        if (_probed.TryGetValue(model, out var cached)) return cached;
        var caps = ModelCatalog.Lookup(model, config);
        try
        {
            using var req = CreateRequest(HttpMethod.Post, "/api/show");
            req.Content = ProviderHttp.JsonContent(w => { w.WriteStartObject(); w.WriteString("model", model); w.WriteEndObject(); });
            using var resp = await ProviderHttp.SendAsync(id, req, ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
            var root = doc.RootElement;
            if (root.GetProp("capabilities") is { ValueKind: JsonValueKind.Array } capsArr)
            {
                var set = capsArr.EnumerateArray().Select(x => x.GetString()).ToHashSet();
                caps = caps with
                {
                    Tools = set.Contains("tools"),
                    Vision = set.Contains("vision"),
                    Reasoning = set.Contains("thinking") ? ReasoningSupport.Budget : ReasoningSupport.None,
                };
            }
            if (root.GetProp("model_info") is { ValueKind: JsonValueKind.Object } info)
                foreach (var p in info.EnumerateObject())
                    if (p.Name.EndsWith(".context_length", StringComparison.Ordinal) && p.Value.TryGetInt32(out var ctx))
                        caps = caps with { ContextWindow = config.NumCtx ?? Math.Min(ctx, 131_072) };
        }
        catch (ModelProviderException) { }
        _probed[model] = caps;
        return caps;
    }

    public async IAsyncEnumerable<ModelEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var caps = await ProbeAsync(request.Model, ct).ConfigureAwait(false);
        using var req = CreateRequest(HttpMethod.Post, "/api/chat");
        req.Content = ProviderHttp.JsonContent(w => WriteBody(w, request, caps));
        using var resp = await ProviderHttp.SendAsync(id, req, ct).ConfigureAwait(false);
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

        var text = new StringBuilder();
        var thinking = new StringBuilder();
        var calls = new List<ToolUsePart>();
        var usage = new Usage();
        var stop = StopReason.EndTurn;
        yield return new MessageStarted(null, request.Model);

        await foreach (var line in ProviderHttp.ReadLinesAsync(stream, ct).ConfigureAwait(false))
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.GetString("error") is { } error) throw new ModelProviderException(id, "server_error", $"{id}: {error}", false);
            if (root.GetProp("message") is { } msg)
            {
                if (msg.GetString("thinking") is { Length: > 0 } th) { thinking.Append(th); yield return new ThinkingDelta(th); }
                if (msg.GetString("content") is { Length: > 0 } c) { text.Append(c); yield return new TextDelta(c); }
                if (msg.GetProp("tool_calls") is { ValueKind: JsonValueKind.Array } tcs)
                    foreach (var tc in tcs.EnumerateArray())
                    {
                        var fn = tc.GetProperty("function");
                        var callId = tc.GetString("id") ?? "call_" + Guid.NewGuid().ToString("n")[..24];
                        var name = fn.GetString("name") ?? "";
                        yield return new ToolUseStarted(callId, name);
                        var args = fn.GetProp("arguments") is { } a
                            ? a.ValueKind == JsonValueKind.String ? Anthropic.AnthropicProvider.ParseInput(a.GetString()!) : a.Clone()
                            : DotCodeJson.EmptyObject;
                        calls.Add(new ToolUsePart(callId, name, args));
                    }
            }
            if (root.GetBool("done") == true)
            {
                usage = new Usage { InputTokens = root.GetInt("prompt_eval_count") ?? 0, OutputTokens = root.GetInt("eval_count") ?? 0 };
                if (root.GetString("done_reason") == "length") stop = StopReason.MaxTokens;
                yield return new UsageUpdated(usage);
            }
        }

        if (thinking.Length > 0) yield return new ContentBlockCompleted(new ThinkingPart(thinking.ToString()));
        if (text.Length > 0) yield return new ContentBlockCompleted(new TextPart(text.ToString()));
        foreach (var c in calls) yield return new ContentBlockCompleted(c);
        if (ct.IsCancellationRequested) yield break;
        yield return new MessageStopped(calls.Count > 0 ? StopReason.ToolUse : stop, usage);
    }

    private void WriteBody(Utf8JsonWriter w, ModelRequest r, ModelCapabilities caps)
    {
        w.WriteStartObject();
        w.WriteString("model", r.Model);
        w.WriteBoolean("stream", true);
        if (config.KeepAlive is { } ka) w.WriteString("keep_alive", ka);
        if (caps.Reasoning != ReasoningSupport.None)
            w.WriteBoolean("think", r.Reasoning is { Effort: not ReasoningEffort.Off });
        w.WriteStartObject("options");
        w.WriteNumber("num_ctx", config.NumCtx ?? caps.ContextWindow);
        w.WriteNumber("num_predict", Math.Min(r.MaxOutputTokens, caps.MaxOutputTokens));
        if (r.Temperature is { } t) w.WriteNumber("temperature", t);
        w.WriteEndObject();

        var names = new Dictionary<string, string>();
        w.WriteStartArray("messages");
        var system = string.Join("\n\n", r.System.Select(s => s.Text));
        if (system.Length > 0)
        {
            w.WriteStartObject(); w.WriteString("role", "system"); w.WriteString("content", system); w.WriteEndObject();
        }
        foreach (var m in r.Messages)
        {
            if (m.Role == Role.Assistant)
            {
                w.WriteStartObject();
                w.WriteString("role", "assistant");
                w.WriteString("content", m.Text);
                var calls = m.ToolUses.ToList();
                if (calls.Count > 0)
                {
                    w.WriteStartArray("tool_calls");
                    foreach (var c in calls)
                    {
                        names[c.Id] = c.Name;
                        w.WriteStartObject();
                        w.WriteStartObject("function");
                        w.WriteString("name", c.Name);
                        w.WritePropertyName("arguments");
                        (c.Input.ValueKind == JsonValueKind.Object ? c.Input : DotCodeJson.EmptyObject).WriteTo(w);
                        w.WriteEndObject();
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                }
                w.WriteEndObject();
                continue;
            }
            foreach (var tr in m.ToolResults)
            {
                w.WriteStartObject();
                w.WriteString("role", "tool");
                w.WriteString("tool_name", names.GetValueOrDefault(tr.ToolUseId, "tool"));
                w.WriteString("content", tr.TextContent.Length == 0 ? "(no output)" : tr.TextContent);
                w.WriteEndObject();
            }
            var texts = string.Concat(m.Content.OfType<TextPart>().Select(t => t.Text));
            var images = m.Content.OfType<ImagePart>().ToList();
            if (texts.Length == 0 && images.Count == 0) continue;
            w.WriteStartObject();
            w.WriteString("role", "user");
            w.WriteString("content", texts);
            if (images.Count > 0 && caps.Vision)
            {
                w.WriteStartArray("images");
                foreach (var img in images) w.WriteStringValue(img.Base64Data);
                w.WriteEndArray();
            }
            w.WriteEndObject();
        }
        w.WriteEndArray();

        if (r.Tools.Count > 0 && caps.Tools && r.ToolChoice.Kind != ToolChoiceKind.None)
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
                SchemaSanitizer.Sanitize(tool.InputSchema, caps.SchemaProfile).WriteTo(w);
                w.WriteEndObject();
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        w.WriteEndObject();
    }
}
