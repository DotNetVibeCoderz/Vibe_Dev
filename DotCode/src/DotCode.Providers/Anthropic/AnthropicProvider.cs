using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using DotCode.Abstractions;
using DotCode.Providers.Auth;
using DotCode.Providers.Http;

namespace DotCode.Providers.Anthropic;

/// <summary>Where the Anthropic Messages API is served from.</summary>
public enum AnthropicPlatform { Direct, Bedrock, Vertex }

/// <summary>Anthropic Messages API adapter for the Anthropic API (and compatible gateways such as DeepSeek's
/// <c>/anthropic</c> endpoint), Amazon Bedrock (<c>type: bedrock</c>: SigV4 or Bedrock API key, AWS event-stream
/// responses) and Google Vertex AI (<c>type: vertex</c>: OAuth from Application Default Credentials). Supports
/// streaming, extended thinking with signature round-trip, explicit prompt-cache breakpoints, vision, PDFs and token
/// counting (Anthropic API).</summary>
public sealed class AnthropicProvider : IModelProvider
{
    private const string ApiVersion = "2023-06-01";
    private readonly string id;
    private readonly ProviderConfig config;
    private readonly string _baseUrl;
    private readonly GoogleAuth? _google;

    public AnthropicPlatform Platform { get; }

    public AnthropicProvider(string id, ProviderConfig config)
    {
        this.id = id;
        this.config = config;
        Platform = (config.Type ?? "").ToLowerInvariant() switch
        {
            "bedrock" or "anthropic-bedrock" => AnthropicPlatform.Bedrock,
            "vertex" or "anthropic-vertex" => AnthropicPlatform.Vertex,
            _ => AnthropicPlatform.Direct,
        };
        if (Platform == AnthropicPlatform.Vertex) _google = new GoogleAuth(config.CredentialsFile);
        _baseUrl = ProviderHttp.TrimSlash(ConfigValue.Expand(config.BaseUrl) ?? Platform switch
        {
            AnthropicPlatform.Bedrock => $"https://bedrock-runtime.{BedrockRegion}.amazonaws.com",
            AnthropicPlatform.Vertex => VertexLocation == "global" ? "https://aiplatform.googleapis.com" : $"https://{VertexLocation}-aiplatform.googleapis.com",
            _ => "https://api.anthropic.com",
        });
    }

    public string Id => id;

    private string BedrockRegion => AwsAuth.Region(config) ?? "us-east-1";

    private string VertexLocation => ConfigValue.Expand(config.Region) ?? Env("CLOUD_ML_REGION") ?? Env("GOOGLE_CLOUD_LOCATION") ?? "us-east5";

    private string VertexProject => ConfigValue.Expand(config.Project) ?? Env("ANTHROPIC_VERTEX_PROJECT_ID") ?? Env("GOOGLE_CLOUD_PROJECT") ?? Env("GCLOUD_PROJECT")
        ?? _google?.ProjectFromCredentials()
        ?? throw new ModelProviderException(id, "config", $"{id}: set \"project\" (or ANTHROPIC_VERTEX_PROJECT_ID / GOOGLE_CLOUD_PROJECT) for Vertex AI.", false);

    private static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;

    /// <summary>Well-known Claude model ids per platform (Bedrock cross-region inference profiles; Vertex publisher
    /// model ids), used for aliases (sonnet/opus/haiku) and model lists. Any other id can be used directly.</summary>
    private static readonly Dictionary<string, (string Bedrock, string Vertex)> FamilyIds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sonnet"] = ("us.anthropic.claude-sonnet-4-5-20250929-v1:0", "claude-sonnet-4-5@20250929"),
        ["opus"] = ("us.anthropic.claude-opus-4-5-20251101-v1:0", "claude-opus-4-5@20251101"),
        ["haiku"] = ("us.anthropic.claude-haiku-4-5-20251001-v1:0", "claude-haiku-4-5@20251001"),
    };

    /// <summary>The platform model id for a model family alias (sonnet, opus, haiku).</summary>
    public static string? PlatformModelId(string providerType, string family) =>
        FamilyIds.TryGetValue(family, out var ids)
            ? providerType.Equals("bedrock", StringComparison.OrdinalIgnoreCase) ? ids.Bedrock : providerType.Equals("vertex", StringComparison.OrdinalIgnoreCase) ? ids.Vertex : null
            : null;

    private static string[] BedrockModels => [.. FamilyIds.Values.Select(v => v.Bedrock), "global.anthropic.claude-sonnet-4-5-20250929-v1:0"];
    private static string[] VertexModels => [.. FamilyIds.Values.Select(v => v.Vertex)];

    public ModelCapabilities GetCapabilities(string modelId)
    {
        var caps = ModelCatalog.Lookup(modelId, config);
        return Platform == AnthropicPlatform.Direct ? caps : caps with { TokenCountingEndpoint = false };
    }

    public async ValueTask<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken ct)
    {
        if (config.Models is { Count: > 0 } declared) return [.. declared.Select(m => new ModelInfo(id, m))];
        if (Platform == AnthropicPlatform.Bedrock) return [.. BedrockModels.Select(m => new ModelInfo(id, m))];
        if (Platform == AnthropicPlatform.Vertex) return [.. VertexModels.Select(m => new ModelInfo(id, m))];
        using var req = CreateRequest(HttpMethod.Get, "/v1/models?limit=100");
        using var resp = await ProviderHttp.SendAsync(id, req, ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
        var list = new List<ModelInfo>();
        if (doc.RootElement.TryGetProperty("data", out var data))
            foreach (var m in data.EnumerateArray())
                list.Add(new ModelInfo(id, m.GetString("id") ?? "", m.GetString("display_name")));
        return list;
    }

    public async ValueTask<int?> CountTokensAsync(ModelRequest request, CancellationToken ct)
    {
        if (Platform != AnthropicPlatform.Direct) return null;
        using var req = CreateRequest(HttpMethod.Post, "/v1/messages/count_tokens");
        req.Content = ProviderHttp.JsonContent(w => WriteBody(w, request, stream: false, countOnly: true));
        try
        {
            using var resp = await ProviderHttp.SendAsync(id, req, ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
            return doc.RootElement.GetInt("input_tokens");
        }
        catch (ModelProviderException) { return null; }
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var req = new HttpRequestMessage(method, _baseUrl + path);
        var key = ConfigValue.Expand(config.ApiKey) ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        if (!string.IsNullOrEmpty(key))
        {
            if (config.UseBearerAuth == true) req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
            else req.Headers.TryAddWithoutValidation("x-api-key", key);
        }
        else if (Environment.GetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN") is { Length: > 0 } token)
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        req.Headers.TryAddWithoutValidation("anthropic-version", ApiVersion);
        if (config.Betas is { Count: > 0 } betas) req.Headers.TryAddWithoutValidation("anthropic-beta", string.Join(',', betas));
        if (config.Headers is not null)
            foreach (var (k, v) in config.Headers) req.Headers.TryAddWithoutValidation(k, ConfigValue.Expand(v));
        return req;
    }

    /// <summary>The streaming request for the platform: URL, body variant and authentication (SigV4, OAuth or API key).</summary>
    private async Task<HttpRequestMessage> CreateStreamRequestAsync(ModelRequest request, CancellationToken ct)
    {
        var body = ProviderHttp.JsonContent(w => WriteBody(w, request, stream: Platform != AnthropicPlatform.Bedrock, countOnly: false));
        switch (Platform)
        {
            case AnthropicPlatform.Bedrock:
            {
                var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/model/{Uri.EscapeDataString(request.Model)}/invoke-with-response-stream") { Content = body };
                req.Headers.TryAddWithoutValidation("Accept", "application/vnd.amazon.eventstream");
                AddCustomHeaders(req);
                // Bedrock API keys (bearer) take precedence; otherwise sign with AWS credentials.
                if ((ConfigValue.Expand(config.ApiKey) ?? Env("AWS_BEARER_TOKEN_BEDROCK")) is { } bearer)
                    req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + bearer);
                else
                {
                    var creds = await AwsAuth.ResolveAsync(config, ct).ConfigureAwait(false)
                                ?? throw new ModelProviderException(id, "auth", $"{id}: no AWS credentials found. Set AWS_ACCESS_KEY_ID/AWS_SECRET_ACCESS_KEY, configure a profile in ~/.aws (awsProfile / AWS_PROFILE), run `aws sso login`, or use a Bedrock API key (AWS_BEARER_TOKEN_BEDROCK).", false);
                    AwsAuth.Sign(req, await body.ReadAsByteArrayAsync(ct).ConfigureAwait(false), creds, BedrockRegion, "bedrock");
                }
                return req;
            }
            case AnthropicPlatform.Vertex:
            {
                var req = new HttpRequestMessage(HttpMethod.Post,
                    $"{_baseUrl}/v1/projects/{VertexProject}/locations/{VertexLocation}/publishers/anthropic/models/{request.Model}:streamRawPredict") { Content = body };
                var token = await _google!.GetTokenAsync(ct).ConfigureAwait(false);
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token.Token);
                if (config.Betas is { Count: > 0 } betas) req.Headers.TryAddWithoutValidation("anthropic-beta", string.Join(',', betas));
                AddCustomHeaders(req);
                return req;
            }
            default:
            {
                var req = CreateRequest(HttpMethod.Post, "/v1/messages");
                req.Content = body;
                return req;
            }
        }
    }

    private void AddCustomHeaders(HttpRequestMessage req)
    {
        if (config.Headers is not null)
            foreach (var (k, v) in config.Headers) req.Headers.TryAddWithoutValidation(k, ConfigValue.Expand(v));
    }

    /// <summary>Anthropic stream events as (event name, JSON): SSE for the API and Vertex, AWS event-stream frames
    /// wrapping base64 event JSON for Bedrock.</summary>
    private async IAsyncEnumerable<(string? Event, string Data)> RawEventsAsync(Stream stream, [EnumeratorCancellation] CancellationToken ct)
    {
        if (Platform != AnthropicPlatform.Bedrock)
        {
            await foreach (var sse in ProviderHttp.ReadSseAsync(stream, ct).ConfigureAwait(false)) yield return (sse.Event, sse.Data);
            yield break;
        }
        await foreach (var frame in AwsEventStream.ReadAsync(stream, ct).ConfigureAwait(false))
        {
            var payload = Encoding.UTF8.GetString(frame.Payload);
            if (frame.MessageType is "exception" or "error")
            {
                var kind = frame.ExceptionType ?? frame.Headers.GetValueOrDefault(":error-code") ?? "error";
                var message = DotCodeJsonSafe(payload)?.GetString("message") ?? payload;
                var (code, retryable) = kind switch
                {
                    "throttlingException" => ("rate_limit", true),
                    "serviceUnavailableException" or "internalServerException" => ("server_error", true),
                    "modelStreamErrorException" or "modelTimeoutException" => ("server_error", true),
                    "validationException" when message.Contains("too long", StringComparison.OrdinalIgnoreCase) => ("context_length", false),
                    "accessDeniedException" => ("auth", false),
                    _ => ("invalid_request", false),
                };
                throw new ModelProviderException(id, code, $"{id}: Bedrock {kind}: {message}", retryable);
            }
            if (frame.EventType != "chunk") continue;
            // {"bytes":"<base64 of an Anthropic stream event>"}
            if (DotCodeJsonSafe(payload)?.GetString("bytes") is { } b64) yield return (null, Encoding.UTF8.GetString(Convert.FromBase64String(b64)));
        }
    }

    private static JsonElement? DotCodeJsonSafe(string json)
    {
        try { return DotCodeJson.Parse(json); }
        catch (JsonException) { return null; }
    }

    public async IAsyncEnumerable<ModelEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        using var req = await CreateStreamRequestAsync(request, ct).ConfigureAwait(false);
        HttpResponseMessage resp;
        try { resp = await ProviderHttp.SendAsync(id, req, ct).ConfigureAwait(false); }
        catch (ModelProviderException ex) when (ex.Code == "auth")
        {
            _google?.Invalidate();   // an expired or revoked token is fetched again on the next call
            throw;
        }
        using var _ = resp;
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

        var blocks = new Dictionary<int, BlockState>();
        var usage = new Usage();
        var stop = StopReason.EndTurn;
        var stopped = false;

        await foreach (var sse in RawEventsAsync(stream, ct).ConfigureAwait(false))
        {
            if (sse.Data.Length == 0 || sse.Data == "[DONE]") continue;
            using var doc = JsonDocument.Parse(sse.Data);
            var root = doc.RootElement;
            var type = root.GetString("type") ?? sse.Event;
            switch (type)
            {
                case "message_start":
                {
                    var msg = root.GetProperty("message");
                    if (msg.GetProp("usage") is { } u) usage = ReadUsage(u, usage);
                    yield return new MessageStarted(msg.GetString("id"), msg.GetString("model"));
                    break;
                }
                case "content_block_start":
                {
                    var index = root.GetProperty("index").GetInt32();
                    var cb = root.GetProperty("content_block");
                    var state = new BlockState(cb.GetString("type") ?? "text");
                    switch (state.Type)
                    {
                        case "tool_use" or "server_tool_use":
                            state.ToolId = cb.GetString("id");
                            state.ToolName = cb.GetString("name");
                            yield return new ToolUseStarted(state.ToolId!, state.ToolName!);
                            break;
                        case "redacted_thinking":
                            state.Signature = cb.GetString("data");
                            break;
                        case "text" when cb.GetString("text") is { Length: > 0 } initial:
                            state.Text.Append(initial);
                            yield return new TextDelta(initial);
                            break;
                    }
                    blocks[index] = state;
                    break;
                }
                case "content_block_delta":
                {
                    var index = root.GetProperty("index").GetInt32();
                    if (!blocks.TryGetValue(index, out var state)) break;
                    var delta = root.GetProperty("delta");
                    switch (delta.GetString("type"))
                    {
                        case "text_delta":
                            var t = delta.GetString("text") ?? "";
                            state.Text.Append(t);
                            yield return new TextDelta(t);
                            break;
                        case "thinking_delta":
                            var th = delta.GetString("thinking") ?? "";
                            state.Text.Append(th);
                            yield return new ThinkingDelta(th);
                            break;
                        case "signature_delta":
                            state.Signature = (state.Signature ?? "") + delta.GetString("signature");
                            break;
                        case "input_json_delta":
                            var pj = delta.GetString("partial_json") ?? "";
                            state.Text.Append(pj);
                            yield return new ToolInputDelta(state.ToolId ?? "", pj);
                            break;
                    }
                    break;
                }
                case "content_block_stop":
                {
                    var index = root.GetProperty("index").GetInt32();
                    if (!blocks.Remove(index, out var state)) break;
                    if (Complete(state) is { } part) yield return new ContentBlockCompleted(part);
                    break;
                }
                case "message_delta":
                {
                    if (root.GetProp("delta") is { } d && d.GetString("stop_reason") is { } sr) stop = MapStop(sr);
                    if (root.GetProp("usage") is { } u) usage = ReadUsage(u, usage);
                    yield return new UsageUpdated(usage);
                    break;
                }
                case "message_stop":
                    stopped = true;
                    yield return new MessageStopped(stop, usage);
                    break;
                case "error":
                {
                    var err = root.GetProp("error");
                    var etype = err?.GetString("type") ?? "error";
                    var emsg = err?.GetString("message") ?? sse.Data;
                    var retryable = etype is "overloaded_error" or "rate_limit_error" or "api_error";
                    throw new ModelProviderException(id, etype == "overloaded_error" ? "overloaded" : etype, $"{id}: {emsg}", retryable);
                }
            }
        }
        if (!stopped)
        {
            foreach (var state in blocks.Values)
                if (Complete(state) is { } part) yield return new ContentBlockCompleted(part);
            if (ct.IsCancellationRequested) yield break;
            yield return new MessageStopped(stop, usage);
        }
    }

    private ContentPart? Complete(BlockState state) => state.Type switch
    {
        "text" => state.Text.Length > 0 ? new TextPart(state.Text.ToString()) : null,
        "thinking" => new ThinkingPart(state.Text.ToString())
        {
            Opaque = state.Signature is { } sig ? new ProviderOpaque(id, DotCodeJson.Build(w => { w.WriteStartObject(); w.WriteString("signature", sig); w.WriteEndObject(); })) : null,
        },
        "redacted_thinking" => new ThinkingPart("")
        {
            Redacted = true,
            Opaque = new ProviderOpaque(id, DotCodeJson.Build(w => { w.WriteStartObject(); w.WriteString("data", state.Signature); w.WriteEndObject(); })),
        },
        "tool_use" => new ToolUsePart(state.ToolId!, state.ToolName!, ParseInput(state.Text.ToString())),
        _ => null,
    };

    internal static JsonElement ParseInput(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return DotCodeJson.EmptyObject;
        try { return DotCodeJson.Parse(json); }
        catch (JsonException) { return JsonRepair.TryRepair(json); }
    }

    private static Usage ReadUsage(JsonElement u, Usage prev) => new()
    {
        InputTokens = u.GetInt("input_tokens") ?? (int)prev.InputTokens,
        OutputTokens = u.GetInt("output_tokens") ?? (int)prev.OutputTokens,
        CacheReadTokens = u.GetInt("cache_read_input_tokens") ?? (int)prev.CacheReadTokens,
        CacheWriteTokens = u.GetInt("cache_creation_input_tokens") ?? (int)prev.CacheWriteTokens,
    };

    private static StopReason MapStop(string s) => s switch
    {
        "tool_use" => StopReason.ToolUse,
        "max_tokens" => StopReason.MaxTokens,
        "stop_sequence" => StopReason.StopSequence,
        "refusal" => StopReason.Refusal,
        _ => StopReason.EndTurn,
    };

    private sealed class BlockState(string type)
    {
        public string Type { get; } = type;
        public StringBuilder Text { get; } = new();
        public string? ToolId { get; set; }
        public string? ToolName { get; set; }
        public string? Signature { get; set; }
    }

    // ---------------- request body ----------------

    private void WriteBody(Utf8JsonWriter w, ModelRequest r, bool stream, bool countOnly)
    {
        var caps = GetCapabilities(r.Model);
        var thinking = r.Reasoning is { Effort: not ReasoningEffort.Off } && caps.Reasoning != ReasoningSupport.None;
        var budget = thinking ? Math.Max(1024, r.Reasoning!.EffectiveBudget) : 0;
        var maxTokens = Math.Min(r.MaxOutputTokens, caps.MaxOutputTokens);
        if (thinking && maxTokens <= budget) maxTokens = Math.Min(caps.MaxOutputTokens, budget + 4096);
        var cache = r.PromptCaching && caps.Caching == CachingSupport.ExplicitBreakpoints;

        w.WriteStartObject();
        // Bedrock and Vertex take the model from the URL and a platform-specific anthropic_version in the body.
        switch (Platform)
        {
            case AnthropicPlatform.Bedrock:
                w.WriteString("anthropic_version", "bedrock-2023-05-31");
                if (config.Betas is { Count: > 0 } betas)
                {
                    w.WriteStartArray("anthropic_beta");
                    foreach (var b in betas) w.WriteStringValue(b);
                    w.WriteEndArray();
                }
                break;
            case AnthropicPlatform.Vertex:
                w.WriteString("anthropic_version", "vertex-2023-10-16");
                break;
            default:
                w.WriteString("model", r.Model);
                break;
        }
        if (!countOnly)
        {
            w.WriteNumber("max_tokens", maxTokens);
            if (stream) w.WriteBoolean("stream", true);
            if (r.Temperature is { } temp && !thinking) w.WriteNumber("temperature", temp);
        }

        if (r.System.Count > 0)
        {
            w.WriteStartArray("system");
            for (var i = 0; i < r.System.Count; i++)
            {
                w.WriteStartObject();
                w.WriteString("type", "text");
                w.WriteString("text", r.System[i].Text);
                if (cache && i == r.System.Count - 1) WriteCacheControl(w);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }

        if (r.Tools.Count > 0)
        {
            w.WriteStartArray("tools");
            for (var i = 0; i < r.Tools.Count; i++)
            {
                var t = r.Tools[i];
                w.WriteStartObject();
                w.WriteString("name", t.Name);
                w.WriteString("description", t.Description);
                w.WritePropertyName("input_schema");
                t.InputSchema.WriteTo(w);
                if (cache && i == r.Tools.Count - 1) WriteCacheControl(w);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            if (!countOnly && r.ToolChoice.Kind != ToolChoiceKind.Auto)
            {
                w.WriteStartObject("tool_choice");
                w.WriteString("type", r.ToolChoice.Kind switch { ToolChoiceKind.Any => "any", ToolChoiceKind.Tool => "tool", ToolChoiceKind.None => "none", _ => "auto" });
                if (r.ToolChoice.ToolName is { } tn) w.WriteString("name", tn);
                w.WriteEndObject();
            }
        }

        if (thinking)
        {
            w.WriteStartObject("thinking");
            w.WriteString("type", "enabled");
            w.WriteNumber("budget_tokens", budget);
            w.WriteEndObject();
        }

        var messages = NormalizeAlternation(r.Messages);
        w.WriteStartArray("messages");
        for (var mi = 0; mi < messages.Count; mi++)
        {
            var (role, parts, providerId) = messages[mi];
            // Cache breakpoint on the last block of the final message (and the last user message before it) so the
            // stable conversation prefix is reused on the next call.
            var cacheLast = cache && (mi == messages.Count - 1 || (mi == messages.Count - 3 && role == Role.User));
            w.WriteStartObject();
            w.WriteString("role", role == Role.User ? "user" : "assistant");
            w.WriteStartArray("content");
            var written = 0;
            var lastIndex = LastWritableIndex(parts, providerId, thinking);
            for (var pi = 0; pi < parts.Count; pi++)
            {
                if (WritePart(w, parts[pi], providerId, thinking, cacheLast && pi == lastIndex)) written++;
            }
            if (written == 0)
            {
                w.WriteStartObject(); w.WriteString("type", "text"); w.WriteString("text", "(empty)"); w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndArray();

        if (!countOnly && r.ProviderOptions is not null)
            foreach (var (k, v) in r.ProviderOptions) { w.WritePropertyName(k); v.WriteTo(w); }
        w.WriteEndObject();
    }

    private int LastWritableIndex(List<ContentPart> parts, string? providerId, bool thinking)
    {
        for (var i = parts.Count - 1; i >= 0; i--)
            if (parts[i] is not ThinkingPart && !(parts[i] is TextPart { Text.Length: 0 })) return i;
        return -1;
    }

    private static void WriteCacheControl(Utf8JsonWriter w)
    {
        w.WriteStartObject("cache_control");
        w.WriteString("type", "ephemeral");
        w.WriteEndObject();
    }

    private bool WritePart(Utf8JsonWriter w, ContentPart part, string? messageProvider, bool thinkingEnabled, bool cacheControl)
    {
        switch (part)
        {
            case TextPart { Text.Length: 0 }:
                return false;
            case TextPart t:
                w.WriteStartObject();
                w.WriteString("type", "text");
                w.WriteString("text", t.Text);
                if (cacheControl) WriteCacheControl(w);
                w.WriteEndObject();
                return true;
            case ImagePart img:
                w.WriteStartObject();
                w.WriteString("type", "image");
                w.WriteStartObject("source");
                w.WriteString("type", "base64");
                w.WriteString("media_type", img.MediaType);
                w.WriteString("data", img.Base64Data);
                w.WriteEndObject();
                if (cacheControl) WriteCacheControl(w);
                w.WriteEndObject();
                return true;
            case DocumentPart doc:
                w.WriteStartObject();
                w.WriteString("type", "document");
                w.WriteStartObject("source");
                w.WriteString("type", "base64");
                w.WriteString("media_type", doc.MediaType);
                w.WriteString("data", doc.Base64Data);
                w.WriteEndObject();
                if (cacheControl) WriteCacheControl(w);
                w.WriteEndObject();
                return true;
            case ToolUsePart tu:
                w.WriteStartObject();
                w.WriteString("type", "tool_use");
                w.WriteString("id", tu.Id);
                w.WriteString("name", tu.Name);
                w.WritePropertyName("input");
                (tu.Input.ValueKind == JsonValueKind.Object ? tu.Input : DotCodeJson.EmptyObject).WriteTo(w);
                if (cacheControl) WriteCacheControl(w);
                w.WriteEndObject();
                return true;
            case ToolResultPart tr:
                w.WriteStartObject();
                w.WriteString("type", "tool_result");
                w.WriteString("tool_use_id", tr.ToolUseId);
                if (tr.IsError) w.WriteBoolean("is_error", true);
                w.WriteStartArray("content");
                var any = false;
                foreach (var c in tr.Content)
                {
                    if (c is TextPart { Text.Length: > 0 } or ImagePart) { WritePart(w, c, messageProvider, thinkingEnabled, false); any = true; }
                }
                if (!any) { w.WriteStartObject(); w.WriteString("type", "text"); w.WriteString("text", "(no output)"); w.WriteEndObject(); }
                w.WriteEndArray();
                if (cacheControl) WriteCacheControl(w);
                w.WriteEndObject();
                return true;
            case ThinkingPart th:
                // Thinking blocks can only be replayed to the provider that signed them.
                if (!thinkingEnabled || th.Opaque is null || th.Opaque.ProviderId != id) return false;
                w.WriteStartObject();
                if (th.Redacted)
                {
                    w.WriteString("type", "redacted_thinking");
                    w.WriteString("data", th.Opaque.Payload.GetString("data"));
                }
                else
                {
                    w.WriteString("type", "thinking");
                    w.WriteString("thinking", th.Text);
                    w.WriteString("signature", th.Opaque.Payload.GetString("signature"));
                }
                w.WriteEndObject();
                return true;
        }
        return false;
    }

    /// <summary>Merges consecutive same-role messages (the Messages API requires alternation).</summary>
    private static List<(Role Role, List<ContentPart> Parts, string? Provider)> NormalizeAlternation(IReadOnlyList<Message> messages)
    {
        var result = new List<(Role, List<ContentPart>, string?)>(messages.Count);
        foreach (var m in messages)
        {
            if (m.Content.Count == 0) continue;
            if (result.Count > 0 && result[^1].Item1 == m.Role)
            {
                var prev = result[^1];
                // Tool results must come first in a user message.
                if (m.Role == Role.User && m.Content.Any(c => c is ToolResultPart))
                    prev.Item2.InsertRange(prev.Item2.TakeWhile(c => c is ToolResultPart).Count(), m.Content.Where(c => c is ToolResultPart));
                prev.Item2.AddRange(m.Role == Role.User ? m.Content.Where(c => c is not ToolResultPart) : m.Content);
            }
            else result.Add((m.Role, [.. m.Content], m.ProviderId));
        }
        if (result.Count > 0 && result[0].Item1 == Role.Assistant) result.Insert(0, (Role.User, [new TextPart("(continue)")], null));
        return result;
    }
}
