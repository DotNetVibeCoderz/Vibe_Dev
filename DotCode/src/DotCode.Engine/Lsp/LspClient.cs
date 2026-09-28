using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DotCode.Abstractions;
using DotCode.Engine.Util;

namespace DotCode.Engine.Lsp;

public sealed record LspDiagnostic(int Line, int Character, int Severity, string Message, string? Source, string? Code);

/// <summary>Language Server Protocol client: JSON-RPC 2.0 with Content-Length framing over a server's stdio.
/// Keeps open documents in sync (full-text didOpen/didChange) and collects published diagnostics.
/// Written directly on System.Text.Json so it stays NativeAOT-compatible.</summary>
public sealed class LspClient : IAsyncDisposable
{
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly Process? _process;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly ConcurrentDictionary<string, (int Version, string Text)> _open = new(PathComparer);
    private readonly ConcurrentDictionary<string, (long Seq, List<LspDiagnostic> Items)> _diagnostics = new(PathComparer);
    private readonly StringBuilder _stderr = new();
    private readonly CancellationTokenSource _cts = new();
    private long _nextId;
    private long _diagnosticSeq;
    private Task? _reader;

    public string Name { get; }
    public string Root { get; }
    public JsonElement Capabilities { get; private set; }
    public bool IsAlive => !_cts.IsCancellationRequested && (_process is null || !_process.HasExited);
    public string Stderr { get { lock (_stderr) return _stderr.ToString(); } }

    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>Raised when diagnostics for a file are published (path).</summary>
    public event Action<string>? DiagnosticsPublished;

    public LspClient(string name, string root, Stream fromServer, Stream toServer, Process? process = null)
    {
        Name = name;
        Root = root;
        _input = fromServer;
        _output = toServer;
        _process = process;
    }

    /// <summary>Starts the server process and performs the initialize handshake.</summary>
    public static async Task<LspClient> StartAsync(string name, string command, IEnumerable<string> args, string root,
        JsonElement? initializationOptions, IReadOnlyDictionary<string, string>? env, CancellationToken ct)
    {
        var psi = ProcessRunner.CreateStartInfo(command, args, root);
        psi.RedirectStandardInput = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        if (env is not null) foreach (var (k, v) in env) psi.Environment[k] = Providers.ConfigValue.Expand(v);
        Process process;
        try { process = Process.Start(psi) ?? throw new InvalidOperationException($"could not start {command}"); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new InvalidOperationException($"Could not start language server '{command}': {ex.Message}");
        }
        var client = new LspClient(name, root, process.StandardOutput.BaseStream, process.StandardInput.BaseStream, process);
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (client._stderr) if (client._stderr.Length < 20_000) client._stderr.AppendLine(e.Data);
        };
        process.BeginErrorReadLine();
        try
        {
            await client.InitializeAsync(initializationOptions, ct).ConfigureAwait(false);
            return client;
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task InitializeAsync(JsonElement? initializationOptions, CancellationToken ct)
    {
        _reader ??= Task.Run(ReadLoopAsync, CancellationToken.None);
        var rootUri = PathToUri(Root);
        var result = await RequestAsync("initialize", w =>
        {
            w.WriteNumber("processId", Environment.ProcessId);
            w.WriteStartObject("clientInfo"); w.WriteString("name", "dotcode"); w.WriteString("version", AppInfo.Version); w.WriteEndObject();
            w.WriteString("rootUri", rootUri);
            w.WriteString("rootPath", Root);
            w.WriteStartArray("workspaceFolders");
            w.WriteStartObject(); w.WriteString("uri", rootUri); w.WriteString("name", Path.GetFileName(Root.TrimEnd('/', '\\'))); w.WriteEndObject();
            w.WriteEndArray();
            if (initializationOptions is { } io) { w.WritePropertyName("initializationOptions"); io.WriteTo(w); }
            w.WriteStartObject("capabilities");
            w.WriteStartObject("workspace");
            w.WriteBoolean("configuration", true);
            w.WriteBoolean("workspaceFolders", true);
            w.WriteStartObject("symbol"); w.WriteBoolean("dynamicRegistration", false); w.WriteEndObject();
            w.WriteEndObject();
            w.WriteStartObject("textDocument");
            w.WriteStartObject("synchronization"); w.WriteBoolean("didSave", true); w.WriteEndObject();
            w.WriteStartObject("definition"); w.WriteBoolean("linkSupport", true); w.WriteEndObject();
            w.WriteStartObject("implementation"); w.WriteBoolean("linkSupport", true); w.WriteEndObject();
            w.WriteStartObject("references"); w.WriteEndObject();
            w.WriteStartObject("hover");
            w.WriteStartArray("contentFormat"); w.WriteStringValue("markdown"); w.WriteStringValue("plaintext"); w.WriteEndArray();
            w.WriteEndObject();
            w.WriteStartObject("documentSymbol"); w.WriteBoolean("hierarchicalDocumentSymbolSupport", true); w.WriteEndObject();
            w.WriteStartObject("publishDiagnostics"); w.WriteBoolean("versionSupport", true); w.WriteEndObject();
            w.WriteStartObject("diagnostic"); w.WriteBoolean("dynamicRegistration", false); w.WriteEndObject();
            w.WriteEndObject();
            w.WriteStartObject("window"); w.WriteBoolean("workDoneProgress", true); w.WriteEndObject();
            w.WriteEndObject();
        }, ct).ConfigureAwait(false);
        Capabilities = result.GetProp("capabilities")?.Clone() ?? DotCodeJson.EmptyObject;
        await NotifyAsync("initialized", w => { }, ct).ConfigureAwait(false);
    }

    public bool Supports(string capability) =>
        Capabilities.GetProp(capability) is { } c && c.ValueKind is not (JsonValueKind.False or JsonValueKind.Null or JsonValueKind.Undefined);

    // ---------------- documents

    /// <summary>Opens the file on the server, or sends its new content when it changed on disk since.</summary>
    public async Task SyncDocumentAsync(string path, string languageId, CancellationToken ct)
    {
        path = Path.GetFullPath(path);
        var text = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        var uri = PathToUri(path);
        if (_open.TryGetValue(path, out var open))
        {
            if (open.Text == text) return;
            var version = open.Version + 1;
            _open[path] = (version, text);
            await NotifyAsync("textDocument/didChange", w =>
            {
                w.WriteStartObject("textDocument"); w.WriteString("uri", uri); w.WriteNumber("version", version); w.WriteEndObject();
                w.WriteStartArray("contentChanges"); w.WriteStartObject(); w.WriteString("text", text); w.WriteEndObject(); w.WriteEndArray();
            }, ct).ConfigureAwait(false);
            await NotifyAsync("textDocument/didSave", w => { w.WriteStartObject("textDocument"); w.WriteString("uri", uri); w.WriteEndObject(); }, ct).ConfigureAwait(false);
            return;
        }
        _open[path] = (1, text);
        await NotifyAsync("textDocument/didOpen", w =>
        {
            w.WriteStartObject("textDocument");
            w.WriteString("uri", uri);
            w.WriteString("languageId", languageId);
            w.WriteNumber("version", 1);
            w.WriteString("text", text);
            w.WriteEndObject();
        }, ct).ConfigureAwait(false);
    }

    public bool IsOpen(string path) => _open.ContainsKey(Path.GetFullPath(path));

    /// <summary>Sequence number of the latest diagnostics published for a file (0 = none yet).</summary>
    public long DiagnosticsSeq(string path) => _diagnostics.TryGetValue(Path.GetFullPath(path), out var d) ? d.Seq : 0;

    /// <summary>Diagnostics for a file: pulled (textDocument/diagnostic) when supported, otherwise the next pushed
    /// set after <paramref name="afterSeq"/> (waiting up to <paramref name="wait"/>), else the latest known.</summary>
    public async Task<List<LspDiagnostic>> GetDiagnosticsAsync(string path, long afterSeq, TimeSpan wait, CancellationToken ct)
    {
        path = Path.GetFullPath(path);
        if (Supports("diagnosticProvider"))
        {
            try
            {
                var report = await RequestAsync("textDocument/diagnostic", w =>
                {
                    w.WriteStartObject("textDocument"); w.WriteString("uri", PathToUri(path)); w.WriteEndObject();
                }, ct, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                if (report.GetProp("items") is { ValueKind: JsonValueKind.Array } items) return [.. items.EnumerateArray().Select(ParseDiagnostic)];
            }
            catch (LspException) { }
        }
        var deadline = DateTime.UtcNow + wait;
        while (DiagnosticsSeq(path) <= afterSeq && DateTime.UtcNow < deadline)
            await Task.Delay(50, ct).ConfigureAwait(false);
        return _diagnostics.TryGetValue(path, out var d) ? d.Items : [];
    }

    private static LspDiagnostic ParseDiagnostic(JsonElement d)
    {
        var start = d.GetProp("range")?.GetProp("start");
        var code = d.GetProp("code") is { } c ? (c.ValueKind == JsonValueKind.String ? c.GetString() : c.GetRawText()) : null;
        return new LspDiagnostic(start?.GetInt("line") ?? 0, start?.GetInt("character") ?? 0, d.GetInt("severity") ?? 1,
            d.GetString("message") ?? "", d.GetString("source"), code);
    }

    // ---------------- JSON-RPC

    public async Task<JsonElement> RequestAsync(string method, Action<Utf8JsonWriter> writeParams, CancellationToken ct, TimeSpan? timeout = null)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            await WriteAsync(w =>
            {
                w.WriteString("jsonrpc", "2.0");
                w.WriteNumber("id", id);
                w.WriteString("method", method);
                w.WriteStartObject("params");
                writeParams(w);
                w.WriteEndObject();
            }, ct).ConfigureAwait(false);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(timeout ?? TimeSpan.FromSeconds(60));
            using var reg = linked.Token.Register(() => tcs.TrySetException(ct.IsCancellationRequested
                ? new OperationCanceledException(ct)
                : new LspException($"{Name}: {method} timed out")));
            return await tcs.Task.ConfigureAwait(false);
        }
        finally { _pending.TryRemove(id, out _); }
    }

    public Task NotifyAsync(string method, Action<Utf8JsonWriter> writeParams, CancellationToken ct) =>
        WriteAsync(w =>
        {
            w.WriteString("jsonrpc", "2.0");
            w.WriteString("method", method);
            w.WriteStartObject("params");
            writeParams(w);
            w.WriteEndObject();
        }, ct);

    private async Task WriteAsync(Action<Utf8JsonWriter> body, CancellationToken ct)
    {
        if (!IsAlive) throw new LspException($"Language server '{Name}' is not running{(Stderr.Length > 0 ? ": " + TextUtil.FirstLine(Stderr, 300) : "")}");
        var json = DotCodeJson.Build(w => { w.WriteStartObject(); body(w); w.WriteEndObject(); }).GetRawText();
        var payload = Encoding.UTF8.GetBytes(json);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {payload.Length}\r\n\r\n");
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _output.WriteAsync(header, ct).ConfigureAwait(false);
            await _output.WriteAsync(payload, ct).ConfigureAwait(false);
            await _output.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (IOException ex) { throw new LspException($"Language server '{Name}' connection failed: {ex.Message}"); }
        finally { _writeLock.Release(); }
    }

    private async Task ReadLoopAsync()
    {
        var reason = "the server closed its output";
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var length = await ReadHeadersAsync().ConfigureAwait(false);
                if (length < 0) break;
                var buffer = new byte[length];
                var read = 0;
                while (read < length)
                {
                    var n = await _input.ReadAsync(buffer.AsMemory(read, length - read), _cts.Token).ConfigureAwait(false);
                    if (n == 0) break;
                    read += n;
                }
                if (read < length) break;
                JsonElement msg;
                try { msg = DotCodeJson.Parse(Encoding.UTF8.GetString(buffer)); }
                catch (JsonException) { continue; }
                Handle(msg);
            }
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { reason = ex.Message; }
        foreach (var id in _pending.Keys)
            if (_pending.TryRemove(id, out var tcs)) tcs.TrySetException(new LspException($"Language server '{Name}' stopped: {reason}{(Stderr.Length > 0 ? " — " + TextUtil.FirstLine(Stderr, 300) : "")}"));
        _cts.Cancel();
    }

    /// <summary>Reads "Header: value" lines up to the blank line; returns Content-Length (-1 at end of stream).</summary>
    private async Task<int> ReadHeadersAsync()
    {
        var length = -1;
        var line = new StringBuilder();
        var one = new byte[1];
        while (true)
        {
            var n = await _input.ReadAsync(one, _cts.Token).ConfigureAwait(false);
            if (n == 0) return -1;
            if (one[0] == '\n')
            {
                var text = line.ToString().TrimEnd('\r');
                line.Clear();
                if (text.Length == 0)
                {
                    if (length >= 0) return length;
                    continue;
                }
                if (text.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) && int.TryParse(text[15..].Trim(), out var l)) length = l;
            }
            else line.Append((char)one[0]);
        }
    }

    private void Handle(JsonElement msg)
    {
        var method = msg.GetString("method");
        var id = msg.GetProp("id");
        if (method is null && id is { } rid)
        {
            if (rid.ValueKind == JsonValueKind.Number && _pending.TryRemove(rid.GetInt64(), out var tcs))
            {
                if (msg.GetProp("error") is { } err) tcs.TrySetException(new LspException($"{Name}: {err.GetString("message") ?? err.GetRawText()}"));
                else tcs.TrySetResult(msg.GetProp("result")?.Clone() ?? default);
            }
            return;
        }
        if (method is null) return;
        if (id is { } reqId)
        {
            // Server → client requests. Answer the common ones so servers do not stall waiting for us.
            var p = msg.GetProp("params");
            _ = WriteAsync(w =>
            {
                w.WriteString("jsonrpc", "2.0");
                w.WritePropertyName("id");
                reqId.WriteTo(w);
                switch (method)
                {
                    case "workspace/configuration":
                        w.WriteStartArray("result");
                        var count = p?.GetProp("items") is { ValueKind: JsonValueKind.Array } items ? items.GetArrayLength() : 1;
                        for (var i = 0; i < count; i++) w.WriteNullValue();
                        w.WriteEndArray();
                        break;
                    case "workspace/workspaceFolders":
                        w.WriteStartArray("result");
                        w.WriteStartObject(); w.WriteString("uri", PathToUri(Root)); w.WriteString("name", Path.GetFileName(Root.TrimEnd('/', '\\'))); w.WriteEndObject();
                        w.WriteEndArray();
                        break;
                    case "window/workDoneProgress/create" or "client/registerCapability" or "client/unregisterCapability"
                        or "window/showMessageRequest" or "workspace/diagnostic/refresh" or "workspace/semanticTokens/refresh"
                        or "workspace/inlayHint/refresh" or "workspace/codeLens/refresh":
                        w.WriteNull("result");
                        break;
                    default:
                        w.WriteStartObject("error"); w.WriteNumber("code", -32601); w.WriteString("message", "Method not supported by DotCode"); w.WriteEndObject();
                        break;
                }
            }, CancellationToken.None).ContinueWith(_ => { }, TaskScheduler.Default);
            return;
        }
        if (method == "textDocument/publishDiagnostics" && msg.GetProp("params") is { } pd && pd.GetString("uri") is { } uri)
        {
            var path = UriToPath(uri);
            var items = pd.GetProp("diagnostics") is { ValueKind: JsonValueKind.Array } arr ? arr.EnumerateArray().Select(ParseDiagnostic).ToList() : [];
            _diagnostics[path] = (Interlocked.Increment(ref _diagnosticSeq), items);
            DiagnosticsPublished?.Invoke(path);
        }
    }

    // ---------------- URIs

    public static string PathToUri(string path) => new Uri(Path.GetFullPath(path)).AbsoluteUri;

    public static string UriToPath(string uri)
    {
        if (!uri.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) return uri;
        var local = Uri.UnescapeDataString(uri["file:".Length..]).TrimStart('/');
        // file:///c:/x → c:/x (Windows); file:///home/x → /home/x
        if (!(local.Length > 1 && local[1] == ':')) local = "/" + local;
        try { return Path.GetFullPath(local); } catch (Exception) { return local; }
    }

    public async ValueTask DisposeAsync()
    {
        if (IsAlive && _reader is not null)
        {
            try
            {
                await RequestAsync("shutdown", _ => { }, CancellationToken.None, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                await NotifyAsync("exit", _ => { }, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is LspException or IOException or OperationCanceledException) { }
        }
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_process is not null)
        {
            try { if (!_process.WaitForExit(1000)) ProcessRunner.Kill(_process); } catch (InvalidOperationException) { }
            _process.Dispose();
        }
    }
}

public sealed class LspException(string message) : Exception(message);
