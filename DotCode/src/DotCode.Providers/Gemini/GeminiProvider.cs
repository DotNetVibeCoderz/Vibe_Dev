using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using DotCode.Abstractions;
using DotCode.Providers.Auth;
using DotCode.Providers.Http;

namespace DotCode.Providers.Gemini;

/// <summary>Google Gemini (generateContent) adapter for the Gemini API (API key) and Vertex AI
/// (<c>platform: vertex</c> or <c>type: vertex-gemini</c>, OAuth from Application Default Credentials): schema
/// sanitization to the OpenAPI subset, thought-signature round-trip, role conversion (assistant→model), thinking budgets.</summary>
public sealed class GeminiProvider : IModelProvider
{
    private readonly string id;
    private readonly ProviderConfig config;
    private readonly string _baseUrl;
    private readonly GoogleAuth? _google;

    public bool Vertex { get; }

    public GeminiProvider(string id, ProviderConfig config)
    {
        this.id = id;
        this.config = config;
        Vertex = string.Equals(config.Platform, "vertex", StringComparison.OrdinalIgnoreCase) || string.Equals(config.Type, "vertex-gemini", StringComparison.OrdinalIgnoreCase);
        if (Vertex)
        {
            _google = new GoogleAuth(config.CredentialsFile);
            var location = ConfigValue.Expand(config.Region) ?? Env("GOOGLE_CLOUD_LOCATION") ?? Env("CLOUD_ML_REGION") ?? "global";
            var project = ConfigValue.Expand(config.Project) ?? Env("GOOGLE_CLOUD_PROJECT") ?? Env("GCLOUD_PROJECT") ?? _google.ProjectFromCredentials() ?? "PROJECT";
            var host = location == "global" ? "https://aiplatform.googleapis.com" : $"https://{location}-aiplatform.googleapis.com";
            _baseUrl = ProviderHttp.TrimSlash(ConfigValue.Expand(config.BaseUrl) ?? $"{host}/v1/projects/{project}/locations/{location}/publishers/google");
        }
        else _baseUrl = ProviderHttp.TrimSlash(ConfigValue.Expand(config.BaseUrl) ?? "https://generativelanguage.googleapis.com/v1beta");
    }

    private static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;

    public string Id => id;

    public ModelCapabilities GetCapabilities(string modelId) => ModelCatalog.Lookup(modelId, config) with { TokenCountingEndpoint = !Vertex };

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var req = new HttpRequestMessage(method, _baseUrl + path);
        if (!Vertex)
        {
            var key = ConfigValue.Expand(config.ApiKey) ?? Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? Environment.GetEnvironmentVariable("GOOGLE_API_KEY");
            if (!string.IsNullOrEmpty(key)) req.Headers.TryAddWithoutValidation("x-goog-api-key", key);
        }
        if (config.Headers is not null)
            foreach (var (k, v) in config.Headers) req.Headers.TryAddWithoutValidation(k, ConfigValue.Expand(v));
        return req;
    }

    /// <summary>Vertex AI: OAuth bearer token (cached, refreshed before expiry).</summary>
    private async Task AuthorizeAsync(HttpRequestMessage req, CancellationToken ct)
    {
        if (_google is null) return;
        if (_baseUrl.Contains("/projects/PROJECT/", StringComparison.Ordinal))
            throw new ModelProviderException(id, "config", $"{id}: set \"project\" (or GOOGLE_CLOUD_PROJECT) for Gemini on Vertex AI.", false);
        var token = await _google.GetTokenAsync(ct).ConfigureAwait(false);
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token.Token);
    }

    public async ValueTask<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken ct)
    {
        if (config.Models is { Count: > 0 } declared) return [.. declared.Select(m => new ModelInfo(id, m))];
        if (Vertex) return [.. new[] { "gemini-2.5-pro", "gemini-2.5-flash", "gemini-2.5-flash-lite" }.Select(m => new ModelInfo(id, m))];
        using var req = CreateRequest(HttpMethod.Get, "/models?pageSize=200");
        using var resp = await ProviderHttp.SendAsync(id, req, ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
        var list = new List<ModelInfo>();
        if (doc.RootElement.TryGetProperty("models", out var models))
            foreach (var m in models.EnumerateArray())
            {
                var name = m.GetString("name") ?? "";
                if (name.StartsWith("models/", StringComparison.Ordinal)) name = name[7..];
                list.Add(new ModelInfo(id, name, m.GetString("displayName")));
            }
        return list;
    }

    public async ValueTask<int?> CountTokensAsync(ModelRequest request, CancellationToken ct)
    {
        if (Vertex) return null;
        using var req = CreateRequest(HttpMethod.Post, $"/models/{request.Model}:countTokens");
        req.Content = ProviderHttp.JsonContent(w =>
        {
            w.WriteStartObject();
            w.WriteStartObject("generateContentRequest");
            w.WriteString("model", "models/" + request.Model);
            WriteContents(w, request);
            w.WriteEndObject();
            w.WriteEndObject();
        });
        try
        {
            using var resp = await ProviderHttp.SendAsync(id, req, ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
            return doc.RootElement.GetInt("totalTokens");
        }
        catch (ModelProviderException) { return null; }
    }

    public async IAsyncEnumerable<ModelEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        using var req = CreateRequest(HttpMethod.Post, $"/models/{request.Model}:streamGenerateContent?alt=sse");
        req.Content = ProviderHttp.JsonContent(w => WriteBody(w, request));
        await AuthorizeAsync(req, ct).ConfigureAwait(false);
        HttpResponseMessage resp;
        try { resp = await ProviderHttp.SendAsync(id, req, ct).ConfigureAwait(false); }
        catch (ModelProviderException ex) when (ex.Code == "auth")
        {
            _google?.Invalidate();
            throw;
        }
        using var _ = resp;
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

        var text = new StringBuilder();
        var thought = new StringBuilder();
        string? thoughtSignature = null;
        var calls = new List<ToolUsePart>();
        var usage = new Usage();
        var stop = StopReason.EndTurn;
        var started = false;

        await foreach (var sse in ProviderHttp.ReadSseAsync(stream, ct).ConfigureAwait(false))
        {
            if (sse.Data.Length == 0) continue;
            using var doc = JsonDocument.Parse(sse.Data);
            var root = doc.RootElement;
            if (root.GetProp("error") is { } err)
                throw new ModelProviderException(id, "server_error", $"{id}: {err.GetString("message")}", true);
            if (!started)
            {
                started = true;
                yield return new MessageStarted(root.GetString("responseId"), root.GetString("modelVersion"));
            }
            if (root.GetProp("usageMetadata") is { } um)
            {
                long prompt = um.GetInt("promptTokenCount") ?? 0;
                long cached = um.GetInt("cachedContentTokenCount") ?? 0;
                long thoughts = um.GetInt("thoughtsTokenCount") ?? 0;
                usage = new Usage
                {
                    InputTokens = Math.Max(0, prompt - cached),
                    CacheReadTokens = cached,
                    OutputTokens = (um.GetInt("candidatesTokenCount") ?? 0) + thoughts,
                    ReasoningTokens = thoughts,
                };
                yield return new UsageUpdated(usage);
            }
            if (root.GetProp("candidates") is not { ValueKind: JsonValueKind.Array } cands || cands.GetArrayLength() == 0) continue;
            var cand = cands[0];
            if (cand.GetProp("content")?.GetProp("parts") is { ValueKind: JsonValueKind.Array } parts)
            {
                foreach (var p in parts.EnumerateArray())
                {
                    var sig = p.GetString("thoughtSignature");
                    if (p.GetProp("functionCall") is { } fc)
                    {
                        var callId = fc.GetString("id") is { Length: > 0 } fid ? fid : "call_" + Guid.NewGuid().ToString("n")[..24];
                        var name = fc.GetString("name") ?? "";
                        yield return new ToolUseStarted(callId, name);
                        calls.Add(new ToolUsePart(callId, name, fc.GetProp("args") is { } a ? a.Clone() : DotCodeJson.EmptyObject)
                        {
                            Opaque = sig is not null ? SignatureOpaque(sig) : null,
                        });
                        continue;
                    }
                    if (p.GetString("text") is { } t)
                    {
                        if (p.GetBool("thought") == true)
                        {
                            thought.Append(t);
                            yield return new ThinkingDelta(t);
                        }
                        else if (t.Length > 0)
                        {
                            text.Append(t);
                            yield return new TextDelta(t);
                        }
                    }
                    if (sig is not null) thoughtSignature = sig;
                }
            }
            if (cand.GetString("finishReason") is { } fr)
                stop = fr switch { "MAX_TOKENS" => StopReason.MaxTokens, "SAFETY" or "RECITATION" or "PROHIBITED_CONTENT" => StopReason.Refusal, _ => StopReason.EndTurn };
        }

        if (thought.Length > 0 || thoughtSignature is not null)
            yield return new ContentBlockCompleted(new ThinkingPart(thought.ToString()) { Opaque = thoughtSignature is not null ? SignatureOpaque(thoughtSignature) : null });
        if (text.Length > 0) yield return new ContentBlockCompleted(new TextPart(text.ToString()));
        foreach (var c in calls) yield return new ContentBlockCompleted(c);
        if (ct.IsCancellationRequested) yield break;
        yield return new MessageStopped(calls.Count > 0 ? StopReason.ToolUse : stop, usage);
    }

    private ProviderOpaque SignatureOpaque(string sig) =>
        new(id, DotCodeJson.Build(w => { w.WriteStartObject(); w.WriteString("thoughtSignature", sig); w.WriteEndObject(); }));

    private void WriteBody(Utf8JsonWriter w, ModelRequest r)
    {
        var caps = GetCapabilities(r.Model);
        w.WriteStartObject();
        var system = string.Join("\n\n", r.System.Select(s => s.Text));
        if (system.Length > 0)
        {
            w.WriteStartObject("systemInstruction");
            w.WriteStartArray("parts");
            w.WriteStartObject(); w.WriteString("text", system); w.WriteEndObject();
            w.WriteEndArray();
            w.WriteEndObject();
        }
        WriteContents(w, r);

        if (r.Tools.Count > 0)
        {
            w.WriteStartArray("tools");
            w.WriteStartObject();
            w.WriteStartArray("functionDeclarations");
            foreach (var t in r.Tools)
            {
                w.WriteStartObject();
                w.WriteString("name", t.Name);
                w.WriteString("description", t.Description);
                var schema = SchemaSanitizer.Sanitize(t.InputSchema, JsonSchemaProfile.OpenApiSubset);
                if (schema.TryGetProperty("properties", out _))
                {
                    w.WritePropertyName("parameters");
                    schema.WriteTo(w);
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteStartObject("toolConfig");
            w.WriteStartObject("functionCallingConfig");
            w.WriteString("mode", r.ToolChoice.Kind switch { ToolChoiceKind.Any or ToolChoiceKind.Tool => "ANY", ToolChoiceKind.None => "NONE", _ => "AUTO" });
            if (r.ToolChoice.ToolName is { } tn)
            {
                w.WriteStartArray("allowedFunctionNames"); w.WriteStringValue(tn); w.WriteEndArray();
            }
            w.WriteEndObject();
            w.WriteEndObject();
        }

        w.WriteStartObject("generationConfig");
        w.WriteNumber("maxOutputTokens", Math.Min(r.MaxOutputTokens, caps.MaxOutputTokens));
        if (r.Temperature is { } temp) w.WriteNumber("temperature", temp);
        if (caps.Reasoning != ReasoningSupport.None)
        {
            w.WriteStartObject("thinkingConfig");
            w.WriteBoolean("includeThoughts", true);
            if (r.Reasoning is { } ro) w.WriteNumber("thinkingBudget", ro.Effort == ReasoningEffort.Off ? 0 : ro.EffectiveBudget);
            w.WriteEndObject();
        }
        w.WriteEndObject();

        if (r.ProviderOptions is not null)
            foreach (var (k, v) in r.ProviderOptions) { w.WritePropertyName(k); v.WriteTo(w); }
        w.WriteEndObject();
    }

    private void WriteContents(Utf8JsonWriter w, ModelRequest r)
    {
        // functionResponse needs the function name; recover it from the originating call.
        var names = new Dictionary<string, string>();
        foreach (var m in r.Messages)
            foreach (var tu in m.ToolUses) names[tu.Id] = tu.Name;

        w.WriteStartArray("contents");
        foreach (var m in r.Messages)
        {
            if (m.Content.Count == 0) continue;
            var sameProvider = m.ProviderId == id;
            w.WriteStartObject();
            w.WriteString("role", m.Role == Role.User ? "user" : "model");
            w.WriteStartArray("parts");
            var wrote = 0;
            string? pendingSignature = null;
            foreach (var part in m.Content)
            {
                switch (part)
                {
                    case ThinkingPart th when sameProvider && th.Opaque?.Payload.GetString("thoughtSignature") is { } sig:
                        pendingSignature = sig;
                        break;
                    case TextPart { Text.Length: > 0 } tp:
                        w.WriteStartObject();
                        w.WriteString("text", tp.Text);
                        if (pendingSignature is not null) { w.WriteString("thoughtSignature", pendingSignature); pendingSignature = null; }
                        w.WriteEndObject();
                        wrote++;
                        break;
                    case ImagePart ip:
                        WriteInline(w, ip.MediaType, ip.Base64Data);
                        wrote++;
                        break;
                    case DocumentPart dp:
                        WriteInline(w, dp.MediaType, dp.Base64Data);
                        wrote++;
                        break;
                    case ToolUsePart tu:
                        w.WriteStartObject();
                        w.WriteStartObject("functionCall");
                        w.WriteString("name", tu.Name);
                        w.WritePropertyName("args");
                        (tu.Input.ValueKind == JsonValueKind.Object ? tu.Input : DotCodeJson.EmptyObject).WriteTo(w);
                        w.WriteEndObject();
                        var s = sameProvider ? tu.Opaque?.Payload.GetString("thoughtSignature") ?? pendingSignature : null;
                        if (s is not null) { w.WriteString("thoughtSignature", s); pendingSignature = null; }
                        w.WriteEndObject();
                        wrote++;
                        break;
                    case ToolResultPart tr:
                        w.WriteStartObject();
                        w.WriteStartObject("functionResponse");
                        w.WriteString("name", names.GetValueOrDefault(tr.ToolUseId, "tool"));
                        w.WriteStartObject("response");
                        w.WriteString(tr.IsError ? "error" : "result", tr.TextContent.Length == 0 ? "(no output)" : tr.TextContent);
                        w.WriteEndObject();
                        w.WriteEndObject();
                        w.WriteEndObject();
                        foreach (var img in tr.Content.OfType<ImagePart>()) WriteInline(w, img.MediaType, img.Base64Data);
                        wrote++;
                        break;
                }
            }
            if (wrote == 0) { w.WriteStartObject(); w.WriteString("text", "(empty)"); w.WriteEndObject(); }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    private static void WriteInline(Utf8JsonWriter w, string mime, string data)
    {
        w.WriteStartObject();
        w.WriteStartObject("inlineData");
        w.WriteString("mimeType", mime);
        w.WriteString("data", data);
        w.WriteEndObject();
        w.WriteEndObject();
    }
}
