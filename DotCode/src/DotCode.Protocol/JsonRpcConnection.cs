using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using DotCode.Abstractions;

namespace DotCode.Protocol;

public sealed class JsonRpcException(int code, string message, JsonElement? data = null) : Exception(message)
{
    public int Code { get; } = code;
    public JsonElement? ErrorData { get; } = data;

    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;
    public const int SessionNotFound = -32001;
    public const int Busy = -32002;
}

/// <summary>Message framing: newline-delimited JSON (default) or LSP-style Content-Length headers; WebSocket text frames.</summary>
public interface IMessageTransport : IAsyncDisposable
{
    Task<string?> ReadAsync(CancellationToken ct);
    Task WriteAsync(string message, CancellationToken ct);
}

public sealed class StreamTransport(Stream input, Stream output, bool contentLength) : IMessageTransport
{
    private readonly StreamReader _reader = new(input, new UTF8Encoding(false), false, 65536);
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public async Task<string?> ReadAsync(CancellationToken ct)
    {
        if (!contentLength)
        {
            while (true)
            {
                var line = await _reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null) return null;
                if (line.Trim().Length > 0) return line;
            }
        }
        var length = -1;
        while (true)
        {
            var header = await _reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (header is null) return null;
            if (header.Length == 0) { if (length >= 0) break; continue; }
            if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(header[15..].Trim());
        }
        var buffer = new char[length];
        var read = 0;
        while (read < length)
        {
            var n = await _reader.ReadAsync(buffer.AsMemory(read, length - read), ct).ConfigureAwait(false);
            if (n == 0) return null;
            read += n;
        }
        return new string(buffer);
    }

    public async Task WriteAsync(string message, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(message);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (contentLength)
            {
                var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
                await output.WriteAsync(header, ct).ConfigureAwait(false);
                await output.WriteAsync(body, ct).ConfigureAwait(false);
            }
            else
            {
                await output.WriteAsync(body, ct).ConfigureAwait(false);
                await output.WriteAsync("\n"u8.ToArray(), ct).ConfigureAwait(false);
            }
            await output.FlushAsync(ct).ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }
    }

    public ValueTask DisposeAsync()
    {
        _reader.Dispose();
        return ValueTask.CompletedTask;
    }
}

public sealed class WebSocketTransport(WebSocket socket) : IMessageTransport
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public async Task<string?> ReadAsync(CancellationToken ct)
    {
        var buffer = new ArrayBufferWriter<byte>(8192);
        while (true)
        {
            var mem = buffer.GetMemory(8192);
            var result = await socket.ReceiveAsync(mem, ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            buffer.Advance(result.Count);
            if (result.EndOfMessage) return Encoding.UTF8.GetString(buffer.WrittenSpan);
        }
    }

    public async Task WriteAsync(string message, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try { await socket.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, true, ct).ConfigureAwait(false); }
        finally { _writeLock.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).ConfigureAwait(false); } catch { }
        socket.Dispose();
    }
}

/// <summary>Bidirectional JSON-RPC 2.0 peer: handles incoming requests/notifications and lets the server issue its
/// own requests to the client (permission prompts, host tool calls) and await the responses.</summary>
public sealed class JsonRpcConnection(IMessageTransport transport)
{
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private long _nextId;

    public delegate Task<JsonElement?> RequestHandler(string method, JsonElement @params, CancellationToken ct);
    public RequestHandler? OnRequest { get; set; }
    public Action<string, JsonElement>? OnNotification { get; set; }

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            string? raw;
            try { raw = await transport.ReadAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or WebSocketException or OperationCanceledException) { break; }
            if (raw is null) break;

            JsonElement msg;
            try { msg = DotCodeJson.Parse(raw); }
            catch (JsonException)
            {
                await SendErrorAsync(null, JsonRpcException.ParseError, "Parse error", ct).ConfigureAwait(false);
                continue;
            }
            if (msg.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in msg.EnumerateArray()) Dispatch(m, ct);
                continue;
            }
            Dispatch(msg, ct);
        }
        foreach (var p in _pending.Values) p.TrySetCanceled();
    }

    private void Dispatch(JsonElement msg, CancellationToken ct)
    {
        var method = msg.GetString("method");
        var id = msg.GetProp("id");
        if (method is null)
        {
            if (id is { ValueKind: JsonValueKind.Number } nid && _pending.TryRemove(nid.GetInt64(), out var tcs))
            {
                if (msg.GetProp("error") is { } err) tcs.TrySetException(new JsonRpcException(err.GetInt("code") ?? 0, err.GetString("message") ?? "error"));
                else tcs.TrySetResult(msg.GetProp("result")?.Clone() ?? default);
            }
            return;
        }
        var @params = msg.GetProp("params")?.Clone() ?? DotCodeJson.EmptyObject;
        if (id is null)
        {
            OnNotification?.Invoke(method, @params);
            return;
        }
        var idCopy = id.Value.Clone();
        _ = Task.Run(async () =>
        {
            try
            {
                var result = OnRequest is null ? throw new JsonRpcException(JsonRpcException.MethodNotFound, $"Method not found: {method}") : await OnRequest(method, @params, ct).ConfigureAwait(false);
                await SendAsync(w =>
                {
                    w.WriteStartObject();
                    w.WriteString("jsonrpc", "2.0");
                    w.WritePropertyName("id"); idCopy.WriteTo(w);
                    w.WritePropertyName("result");
                    if (result is { } r) r.WriteTo(w); else w.WriteNullValue();
                    w.WriteEndObject();
                }, ct).ConfigureAwait(false);
            }
            catch (JsonRpcException ex) { await SendErrorAsync(idCopy, ex.Code, ex.Message, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { await SendErrorAsync(idCopy, JsonRpcException.InternalError, "Cancelled", CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { await SendErrorAsync(idCopy, JsonRpcException.InternalError, ex.Message, ct).ConfigureAwait(false); }
        }, ct);
    }

    public Task NotifyAsync(string method, Action<Utf8JsonWriter> writeParams, CancellationToken ct = default) => SendAsync(w =>
    {
        w.WriteStartObject();
        w.WriteString("jsonrpc", "2.0");
        w.WriteString("method", method);
        w.WritePropertyName("params");
        writeParams(w);
        w.WriteEndObject();
    }, ct);

    /// <summary>Server → client request (e.g. <c>permission.request</c>); awaits the client's response.</summary>
    public async Task<JsonElement> RequestAsync(string method, Action<Utf8JsonWriter> writeParams, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            await SendAsync(w =>
            {
                w.WriteStartObject();
                w.WriteString("jsonrpc", "2.0");
                w.WriteNumber("id", id);
                w.WriteString("method", method);
                w.WritePropertyName("params");
                writeParams(w);
                w.WriteEndObject();
            }, ct).ConfigureAwait(false);
            using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
            return await tcs.Task.ConfigureAwait(false);
        }
        finally { _pending.TryRemove(id, out _); }
    }

    private Task SendErrorAsync(JsonElement? id, int code, string message, CancellationToken ct) => SendAsync(w =>
    {
        w.WriteStartObject();
        w.WriteString("jsonrpc", "2.0");
        w.WritePropertyName("id");
        if (id is { } i) i.WriteTo(w); else w.WriteNullValue();
        w.WriteStartObject("error");
        w.WriteNumber("code", code);
        w.WriteString("message", message);
        w.WriteEndObject();
        w.WriteEndObject();
    }, ct);

    private async Task SendAsync(Action<Utf8JsonWriter> write, CancellationToken ct)
    {
        var buffer = new ArrayBufferWriter<byte>(512);
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })) write(w);
        try { await transport.WriteAsync(Encoding.UTF8.GetString(buffer.WrittenSpan), ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or WebSocketException or ObjectDisposedException) { }
    }
}
