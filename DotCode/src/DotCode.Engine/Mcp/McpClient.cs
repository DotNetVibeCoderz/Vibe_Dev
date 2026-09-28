using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DotCode.Abstractions;
using DotCode.Engine.Configuration;
using DotCode.Engine.Util;
using DotCode.Providers;
using DotCode.Providers.Http;

namespace DotCode.Engine.Mcp;

public sealed record McpToolInfo(string Name, string Description, JsonElement InputSchema, bool ReadOnlyHint);
public sealed record McpPromptInfo(string Name, string Description, IReadOnlyList<string> Arguments);
public sealed record McpResourceInfo(string Uri, string Name, string? Description, string? MimeType);

/// <summary>Model Context Protocol client (JSON-RPC 2.0) over stdio or Streamable HTTP (with SSE responses).
/// Implemented directly on System.Text.Json so it stays NativeAOT-compatible.</summary>
public sealed class McpClient : IAsyncDisposable
{
    public const string ProtocolVersion = "2025-06-18";

    private readonly IMcpTransport _transport;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private long _nextId;

    public string Name { get; }
    public McpServerConfig Config { get; }
    public string? ServerName { get; private set; }
    public string? ServerVersion { get; private set; }
    public string? Instructions { get; private set; }
    public List<McpToolInfo> Tools { get; private set; } = [];
    public List<McpPromptInfo> Prompts { get; private set; } = [];
    public List<McpResourceInfo> Resources { get; private set; } = [];
    public bool SupportsPrompts { get; private set; }
    public bool SupportsResources { get; private set; }

    private McpClient(string name, McpServerConfig config, IMcpTransport transport)
    {
        Name = name;
        Config = config;
        _transport = transport;
        _transport.MessageReceived += OnMessage;
        _transport.Closed += OnClosed;
    }

    /// <summary>The connection is gone (process exited, SSE stream ended): fail every request still waiting.</summary>
    private void OnClosed(string reason)
    {
        foreach (var id in _pending.Keys)
            if (_pending.TryRemove(id, out var tcs)) tcs.TrySetException(new McpException($"MCP server '{Name}' disconnected: {reason}"));
    }

    /// <summary>Transport in use: stdio, http (Streamable HTTP) or sse (legacy HTTP+SSE, protocol 2024-11-05).</summary>
    public string Transport { get; private set; } = "stdio";

    public static async Task<McpClient> ConnectAsync(string name, McpServerConfig config, string cwd, CancellationToken ct)
    {
        switch (config.EffectiveType)
        {
            case "sse":
                return await ConnectWithAsync(name, config, new SseTransport(config), "sse", ct).ConfigureAwait(false);
            case "http" or "streamable-http":
                try
                {
                    return await ConnectWithAsync(name, config, new HttpTransport(config), "http", ct).ConfigureAwait(false);
                }
                catch (McpException ex) when (ex.Code is 400 or 404 or 405)
                {
                    // Backwards compatibility (per the MCP spec): a server that rejects the Streamable HTTP POST may
                    // only speak the older HTTP+SSE transport, so open the SSE stream on the same URL instead.
                    return await ConnectWithAsync(name, config, new SseTransport(config), "sse", ct).ConfigureAwait(false);
                }
            default:
                return await ConnectWithAsync(name, config, new StdioTransport(config, cwd), "stdio", ct).ConfigureAwait(false);
        }
    }

    private static async Task<McpClient> ConnectWithAsync(string name, McpServerConfig config, IMcpTransport transport, string kind, CancellationToken ct)
    {
        var client = new McpClient(name, config, transport) { Transport = kind };
        try
        {
            await transport.StartAsync(ct).ConfigureAwait(false);
            await client.InitializeAsync(ct).ConfigureAwait(false);
            return client;
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task InitializeAsync(CancellationToken ct)
    {
        var result = await RequestAsync("initialize", w =>
        {
            w.WriteString("protocolVersion", ProtocolVersion);
            w.WriteStartObject("capabilities");
            w.WriteStartObject("roots"); w.WriteBoolean("listChanged", false); w.WriteEndObject();
            w.WriteEndObject();
            w.WriteStartObject("clientInfo");
            w.WriteString("name", "dotcode");
            w.WriteString("version", "0.1.0");
            w.WriteEndObject();
        }, ct).ConfigureAwait(false);
        ServerName = result.GetProp("serverInfo")?.GetString("name");
        ServerVersion = result.GetProp("serverInfo")?.GetString("version");
        Instructions = result.GetString("instructions");
        var caps = result.GetProp("capabilities");
        SupportsPrompts = caps?.GetProp("prompts") is not null;
        SupportsResources = caps?.GetProp("resources") is not null;
        await NotifyAsync("notifications/initialized", ct).ConfigureAwait(false);

        if (caps?.GetProp("tools") is not null || caps is null) await RefreshToolsAsync(ct).ConfigureAwait(false);
        if (SupportsPrompts) await RefreshPromptsAsync(ct).ConfigureAwait(false);
        if (SupportsResources) await RefreshResourcesAsync(ct).ConfigureAwait(false);
    }

    public async Task RefreshToolsAsync(CancellationToken ct)
    {
        var tools = new List<McpToolInfo>();
        string? cursor = null;
        do
        {
            var c = cursor;
            var result = await RequestAsync("tools/list", c is null ? null : w => w.WriteString("cursor", c), ct).ConfigureAwait(false);
            if (result.GetProp("tools") is { ValueKind: JsonValueKind.Array } arr)
                foreach (var t in arr.EnumerateArray())
                    tools.Add(new McpToolInfo(
                        t.GetString("name") ?? "",
                        t.GetString("description") ?? "",
                        t.GetProp("inputSchema")?.Clone() ?? DotCodeJson.Parse("{\"type\":\"object\"}"),
                        t.GetProp("annotations")?.GetBool("readOnlyHint") == true));
            cursor = result.GetString("nextCursor");
        } while (cursor is not null);
        Tools = tools;
    }

    public async Task RefreshPromptsAsync(CancellationToken ct)
    {
        try
        {
            var result = await RequestAsync("prompts/list", null, ct).ConfigureAwait(false);
            var list = new List<McpPromptInfo>();
            if (result.GetProp("prompts") is { ValueKind: JsonValueKind.Array } arr)
                foreach (var p in arr.EnumerateArray())
                    list.Add(new McpPromptInfo(p.GetString("name") ?? "", p.GetString("description") ?? "",
                        p.GetProp("arguments") is { ValueKind: JsonValueKind.Array } args ? [.. args.EnumerateArray().Select(a => a.GetString("name") ?? "")] : []));
            Prompts = list;
        }
        catch (McpException) { }
    }

    public async Task RefreshResourcesAsync(CancellationToken ct)
    {
        try
        {
            var result = await RequestAsync("resources/list", null, ct).ConfigureAwait(false);
            var list = new List<McpResourceInfo>();
            if (result.GetProp("resources") is { ValueKind: JsonValueKind.Array } arr)
                foreach (var r in arr.EnumerateArray())
                    list.Add(new McpResourceInfo(r.GetString("uri") ?? "", r.GetString("name") ?? "", r.GetString("description"), r.GetString("mimeType")));
            Resources = list;
        }
        catch (McpException) { }
    }

    public async Task<(List<ContentPart> Content, bool IsError)> CallToolAsync(string tool, JsonElement arguments, CancellationToken ct)
    {
        var timeout = TimeSpan.FromSeconds(Config.Timeout ?? 600);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var result = await RequestAsync("tools/call", w =>
        {
            w.WriteString("name", tool);
            w.WritePropertyName("arguments");
            (arguments.ValueKind == JsonValueKind.Object ? arguments : DotCodeJson.EmptyObject).WriteTo(w);
        }, cts.Token).ConfigureAwait(false);
        return (ParseContent(result), result.GetBool("isError") == true);
    }

    public async Task<string> GetPromptAsync(string prompt, IReadOnlyDictionary<string, string> args, CancellationToken ct)
    {
        var result = await RequestAsync("prompts/get", w =>
        {
            w.WriteString("name", prompt);
            w.WriteStartObject("arguments");
            foreach (var (k, v) in args) w.WriteString(k, v);
            w.WriteEndObject();
        }, ct).ConfigureAwait(false);
        var sb = new StringBuilder();
        if (result.GetProp("messages") is { ValueKind: JsonValueKind.Array } msgs)
            foreach (var m in msgs.EnumerateArray())
                if (m.GetProp("content") is { } c && c.GetString("text") is { } t) sb.AppendLine(t);
        return sb.ToString();
    }

    public async Task<string> ReadResourceAsync(string uri, CancellationToken ct)
    {
        var result = await RequestAsync("resources/read", w => w.WriteString("uri", uri), ct).ConfigureAwait(false);
        var sb = new StringBuilder();
        if (result.GetProp("contents") is { ValueKind: JsonValueKind.Array } arr)
            foreach (var c in arr.EnumerateArray())
                sb.AppendLine(c.GetString("text") ?? (c.GetString("blob") is { } b ? $"[binary {c.GetString("mimeType")} {b.Length} base64 chars]" : ""));
        return sb.ToString();
    }

    private static List<ContentPart> ParseContent(JsonElement result)
    {
        var parts = new List<ContentPart>();
        if (result.GetProp("content") is { ValueKind: JsonValueKind.Array } arr)
        {
            foreach (var c in arr.EnumerateArray())
            {
                switch (c.GetString("type"))
                {
                    case "text": parts.Add(new TextPart(c.GetString("text") ?? "")); break;
                    case "image": parts.Add(new ImagePart(c.GetString("data") ?? "", c.GetString("mimeType") ?? "image/png")); break;
                    case "resource":
                        var res = c.GetProp("resource");
                        parts.Add(new TextPart(res?.GetString("text") ?? $"[resource {res?.GetString("uri")}]"));
                        break;
                    case "resource_link":
                        parts.Add(new TextPart($"[resource {c.GetString("name")}: {c.GetString("uri")}]"));
                        break;
                    default: parts.Add(new TextPart(c.GetRawText())); break;
                }
            }
        }
        if (parts.Count == 0 && result.GetProp("structuredContent") is { } sc) parts.Add(new TextPart(sc.GetRawText()));
        return parts;
    }

    // ---------- JSON-RPC ----------

    public async Task<JsonElement> RequestAsync(string method, Action<Utf8JsonWriter>? writeParams, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        var message = DotCodeJson.Build(w =>
        {
            w.WriteStartObject();
            w.WriteString("jsonrpc", "2.0");
            w.WriteNumber("id", id);
            w.WriteString("method", method);
            w.WriteStartObject("params");
            writeParams?.Invoke(w);
            w.WriteEndObject();
            w.WriteEndObject();
        });
        try
        {
            await _transport.SendAsync(message, ct).ConfigureAwait(false);
            using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private Task NotifyAsync(string method, CancellationToken ct) =>
        _transport.SendAsync(DotCodeJson.Build(w =>
        {
            w.WriteStartObject();
            w.WriteString("jsonrpc", "2.0");
            w.WriteString("method", method);
            w.WriteEndObject();
        }), ct);

    private void OnMessage(JsonElement msg)
    {
        if (msg.GetProp("id") is { } idEl && (msg.GetProp("result") is not null || msg.GetProp("error") is not null))
        {
            if (idEl.ValueKind == JsonValueKind.Number && _pending.TryRemove(idEl.GetInt64(), out var tcs))
            {
                if (msg.GetProp("error") is { } err) tcs.TrySetException(new McpException(err.GetString("message") ?? err.GetRawText(), err.GetInt("code") ?? 0));
                else tcs.TrySetResult(msg.GetProperty("result").Clone());
            }
            return;
        }
        var method = msg.GetString("method");
        if (method is null) return;
        if (msg.GetProp("id") is { } reqId)
        {
            // Server → client requests: answer roots/list and ping; reject others.
            _ = _transport.SendAsync(DotCodeJson.Build(w =>
            {
                w.WriteStartObject();
                w.WriteString("jsonrpc", "2.0");
                w.WritePropertyName("id");
                reqId.WriteTo(w);
                if (method == "roots/list")
                {
                    w.WriteStartObject("result");
                    w.WriteStartArray("roots");
                    w.WriteEndArray();
                    w.WriteEndObject();
                }
                else if (method == "ping")
                {
                    w.WriteStartObject("result");
                    w.WriteEndObject();
                }
                else
                {
                    w.WriteStartObject("error");
                    w.WriteNumber("code", -32601);
                    w.WriteString("message", "Method not supported by client");
                    w.WriteEndObject();
                }
                w.WriteEndObject();
            }), CancellationToken.None);
            return;
        }
        if (method == "notifications/tools/list_changed")
            _ = Task.Run(async () => { try { await RefreshToolsAsync(CancellationToken.None).ConfigureAwait(false); } catch { } });
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var p in _pending.Values) p.TrySetCanceled();
        await _transport.DisposeAsync().ConfigureAwait(false);
    }
}

public sealed class McpException(string message, int code = 0) : Exception(message)
{
    public int Code { get; } = code;
}

internal interface IMcpTransport : IAsyncDisposable
{
    event Action<JsonElement>? MessageReceived;
    /// <summary>Raised once when the connection ends unexpectedly.</summary>
    event Action<string>? Closed;
    Task StartAsync(CancellationToken ct);
    Task SendAsync(JsonElement message, CancellationToken ct);
}

/// <summary>Newline-delimited JSON-RPC over a child process's stdin/stdout.</summary>
internal sealed class StdioTransport(McpServerConfig config, string cwd) : IMcpTransport
{
    private Process? _process;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly StringBuilder _stderr = new();
    public event Action<JsonElement>? MessageReceived;

    public Task StartAsync(CancellationToken ct)
    {
        var command = ConfigValue.Expand(config.Command) ?? throw new InvalidOperationException("MCP stdio server requires a command");
        var args = (config.Args ?? []).Select(a => ConfigValue.Expand(a) ?? "");
        var psi = ProcessRunner.CreateStartInfo(command, args, config.Cwd is { } d ? DotCodePaths.Resolve(d, cwd) : cwd);
        psi.RedirectStandardInput = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.StandardOutputEncoding = new UTF8Encoding(false);
        psi.StandardInputEncoding = new UTF8Encoding(false);
        if (config.Env is not null)
            foreach (var (k, v) in config.Env) psi.Environment[k] = ConfigValue.Expand(v);
        _process = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start MCP server '{command}'");
        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (_stderr) if (_stderr.Length < 20_000) _stderr.AppendLine(e.Data);
        };
        _process.BeginErrorReadLine();
        _ = Task.Run(ReadLoop, CancellationToken.None);
        return Task.CompletedTask;
    }

    public string Stderr { get { lock (_stderr) return _stderr.ToString(); } }

    public event Action<string>? Closed;
    private bool _disposing;

    private async Task ReadLoop()
    {
        var reader = _process!.StandardOutput;
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (line.Length == 0 || line[0] != '{') continue;
                JsonElement msg;
                try { msg = DotCodeJson.Parse(line); }
                catch (JsonException) { continue; }
                MessageReceived?.Invoke(msg);
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { }
        if (!_disposing) Closed?.Invoke($"process exited{(Stderr.Length > 0 ? ": " + TextUtil.FirstLine(Stderr, 300) : "")}");
    }

    public async Task SendAsync(JsonElement message, CancellationToken ct)
    {
        if (_process is null || _process.HasExited)
            throw new McpException($"MCP server process exited{(Stderr.Length > 0 ? ": " + TextUtil.FirstLine(Stderr, 300) : "")}");
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _process.StandardInput.WriteLineAsync(message.GetRawText().AsMemory(), ct).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(ct).ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }
    }

    public ValueTask DisposeAsync()
    {
        _disposing = true;
        if (_process is not null)
        {
            try { _process.StandardInput.Close(); } catch { }
            if (!_process.WaitForExit(500)) ProcessRunner.Kill(_process);
            _process.Dispose();
        }
        return ValueTask.CompletedTask;
    }
}

/// <summary>Streamable HTTP transport: each message is POSTed; responses arrive as JSON or an SSE stream.</summary>
internal sealed class HttpTransport(McpServerConfig config) : IMcpTransport
{
    private string? _sessionId;
    public event Action<JsonElement>? MessageReceived;
    // Each request is its own HTTP exchange; there is no long-lived connection to lose.
    public event Action<string>? Closed { add { } remove { } }

    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    public async Task SendAsync(JsonElement message, CancellationToken ct)
    {
        var url = ConfigValue.Expand(config.Url) ?? throw new InvalidOperationException("MCP http server requires a url");
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        req.Headers.TryAddWithoutValidation("MCP-Protocol-Version", McpClient.ProtocolVersion);
        if (_sessionId is not null) req.Headers.TryAddWithoutValidation("Mcp-Session-Id", _sessionId);
        if (config.Headers is not null)
            foreach (var (k, v) in config.Headers) req.Headers.TryAddWithoutValidation(k, ConfigValue.Expand(v));
        req.Content = new StringContent(message.GetRawText(), Encoding.UTF8, "application/json");

        using var resp = await ProviderHttp.GetClient().SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (resp.Headers.TryGetValues("Mcp-Session-Id", out var ids)) _sessionId = ids.FirstOrDefault();
        if (resp.StatusCode == System.Net.HttpStatusCode.Accepted) return;
        if (!resp.IsSuccessStatusCode)
            throw new McpException($"MCP HTTP {(int)resp.StatusCode}: {TextUtil.Truncate(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false), 500)}", (int)resp.StatusCode);

        var mediaType = resp.Content.Headers.ContentType?.MediaType;
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        if (mediaType == "text/event-stream")
        {
            await foreach (var ev in ProviderHttp.ReadSseAsync(stream, ct).ConfigureAwait(false))
            {
                if (ev.Data.Length == 0) continue;
                try { MessageReceived?.Invoke(DotCodeJson.Parse(ev.Data)); }
                catch (JsonException) { }
            }
        }
        else
        {
            using var reader = new StreamReader(stream);
            var body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
            if (body.Trim().Length == 0) return;
            var json = DotCodeJson.Parse(body);
            if (json.ValueKind == JsonValueKind.Array) foreach (var m in json.EnumerateArray()) MessageReceived?.Invoke(m);
            else MessageReceived?.Invoke(json);
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Legacy HTTP+SSE transport (MCP protocol 2024-11-05): a long-lived GET opens an SSE stream whose first
/// <c>endpoint</c> event names the URL to POST messages to; every server message (responses, requests,
/// notifications) arrives on that stream as a <c>message</c> event.</summary>
internal sealed class SseTransport(McpServerConfig config) : IMcpTransport
{
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource<Uri> _endpoint = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private HttpClient? _http;
    private Task? _reader;
    private volatile bool _closed;

    public event Action<JsonElement>? MessageReceived;
    public event Action<string>? Closed;

    private void AddHeaders(HttpRequestMessage req)
    {
        if (config.Headers is not null)
            foreach (var (k, v) in config.Headers) req.Headers.TryAddWithoutValidation(k, ConfigValue.Expand(v));
    }

    public async Task StartAsync(CancellationToken ct)
    {
        var url = new Uri(ConfigValue.Expand(config.Url) ?? throw new InvalidOperationException("MCP sse server requires a url"));
        _http = ProviderHttp.GetClient();
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        req.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        AddHeaders(req);
        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        finally { req.Dispose(); }
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            resp.Dispose();
            throw new McpException($"MCP SSE {(int)resp.StatusCode}: {TextUtil.Truncate(body, 500)}", (int)resp.StatusCode);
        }
        _reader = Task.Run(() => ReadLoopAsync(resp, url), CancellationToken.None);

        // The endpoint event must arrive before anything can be sent.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        await _endpoint.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
    }

    private async Task ReadLoopAsync(HttpResponseMessage resp, Uri baseUrl)
    {
        var reason = "SSE stream ended";
        try
        {
            using (resp)
            await using (var stream = await resp.Content.ReadAsStreamAsync(_cts.Token).ConfigureAwait(false))
            {
                await foreach (var ev in ProviderHttp.ReadSseAsync(stream, _cts.Token).ConfigureAwait(false))
                {
                    if (ev.Event == "endpoint")
                    {
                        if (Uri.TryCreate(baseUrl, ev.Data.Trim(), out var endpoint)) _endpoint.TrySetResult(endpoint);
                        continue;
                    }
                    if (ev.Event is not (null or "" or "message") || ev.Data.Length == 0) continue;
                    try { MessageReceived?.Invoke(DotCodeJson.Parse(ev.Data)); }
                    catch (JsonException) { }
                }
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { return; }
        catch (Exception ex) when (ex is HttpRequestException or IOException) { reason = ex.Message; }
        _closed = true;
        _endpoint.TrySetException(new McpException($"MCP SSE stream closed before the endpoint event ({reason})"));
        if (!_cts.IsCancellationRequested) Closed?.Invoke(reason);
    }

    public async Task SendAsync(JsonElement message, CancellationToken ct)
    {
        if (_closed) throw new McpException("MCP SSE connection is closed");
        var endpoint = await _endpoint.Task.WaitAsync(ct).ConfigureAwait(false);
        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(message.GetRawText(), Encoding.UTF8, "application/json"),
        };
        AddHeaders(req);
        using var resp = await _http!.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new McpException($"MCP SSE POST {(int)resp.StatusCode}: {TextUtil.Truncate(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false), 500)}", (int)resp.StatusCode);
        // Responses normally arrive on the stream; tolerate servers that also answer in the POST body.
        if (resp.Content.Headers.ContentType?.MediaType == "application/json")
        {
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (body.Trim().Length > 0)
            {
                try { MessageReceived?.Invoke(DotCodeJson.Parse(body)); }
                catch (JsonException) { }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_reader is not null)
        {
            try { await _reader.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
        }
        _cts.Dispose();
    }
}
