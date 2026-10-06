using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Marbots.Abstractions;
using Marbots.Kernel;
using Microsoft.Extensions.Logging;

namespace Marbots.Runtime;

public sealed record McpToolInfo(string Name, string Description, string InputSchema);

/// <summary>Minimal Model Context Protocol client (JSON-RPC 2.0) over stdio or streamable HTTP.</summary>
public interface IMcpClient : IAsyncDisposable
{
    Task InitializeAsync(CancellationToken ct);
    Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken ct);
    Task<FunctionResult> CallToolAsync(string name, JsonElement arguments, CancellationToken ct);
}

internal static class JsonRpc
{
    public static byte[] Request(long id, string method, Action<Utf8JsonWriter>? writeParams)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("jsonrpc", "2.0");
            if (id >= 0) w.WriteNumber("id", id);
            w.WriteString("method", method);
            if (writeParams is not null)
            {
                w.WritePropertyName("params");
                writeParams(w);
            }
            w.WriteEndObject();
        }
        return ms.ToArray();
    }

    public static void InitializeParams(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WriteString("protocolVersion", "2025-06-18");
        w.WriteStartObject("capabilities");
        w.WriteEndObject();
        w.WriteStartObject("clientInfo");
        w.WriteString("name", "marbots");
        w.WriteString("version", "0.1.0");
        w.WriteEndObject();
        w.WriteEndObject();
    }

    public static Action<Utf8JsonWriter> CallParams(string name, JsonElement args) => w =>
    {
        w.WriteStartObject();
        w.WriteString("name", name);
        w.WritePropertyName("arguments");
        if (args.ValueKind == JsonValueKind.Object) args.WriteTo(w);
        else { w.WriteStartObject(); w.WriteEndObject(); }
        w.WriteEndObject();
    };

    public static IReadOnlyList<McpToolInfo> ParseTools(JsonElement result)
    {
        var list = new List<McpToolInfo>();
        if (!result.TryGetProperty("tools", out var tools)) return list;
        foreach (var t in tools.EnumerateArray())
        {
            list.Add(new McpToolInfo(
                t.GetProperty("name").GetString() ?? "",
                t.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "",
                t.TryGetProperty("inputSchema", out var s) ? s.GetRawText() : """{"type":"object","properties":{}}"""));
        }
        return list;
    }

    public static FunctionResult ParseCallResult(JsonElement result)
    {
        var isError = result.TryGetProperty("isError", out var e) && e.ValueKind == JsonValueKind.True;
        var sb = new StringBuilder();
        if (result.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in content.EnumerateArray())
            {
                var type = c.TryGetProperty("type", out var t) ? t.GetString() : "text";
                if (type == "text" && c.TryGetProperty("text", out var txt)) sb.AppendLine(txt.GetString());
                else if (type == "resource" && c.TryGetProperty("resource", out var r) && r.TryGetProperty("text", out var rt)) sb.AppendLine(rt.GetString());
                else sb.Append('[').Append(type).AppendLine(" content omitted]");
            }
        }
        if (result.TryGetProperty("structuredContent", out var sc) && sb.Length == 0) sb.Append(sc.GetRawText());
        var text = sb.ToString().TrimEnd();
        if (text.Length > 60_000) text = text[..60_000] + "…[truncated]";
        return isError ? FunctionResult.Fail(text) : FunctionResult.Ok(text.Length == 0 ? "(empty result)" : text);
    }
}

public sealed class StdioMcpClient : IMcpClient
{
    private readonly Process _process;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly StringBuilder _stderr = new();
    private readonly Task _readerLoop;
    private long _nextId;

    public StdioMcpClient(McpServerConfig config, IReadOnlyDictionary<string, string> env, string? workingDirectory)
    {
        var (file, args) = ResolveCommand(config.Command ?? throw new InvalidOperationException("MCP stdio server needs a command."), config.Args);
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        foreach (var (k, v) in env) psi.Environment[k] = v;
        _process = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start MCP server '{config.Name}'.");
        _process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (_stderr) { if (_stderr.Length < 8000) _stderr.AppendLine(e.Data); } };
        _process.BeginErrorReadLine();
        _readerLoop = Task.Run(ReadLoopAsync);
    }

    /// <summary>On Windows, npm/npx/uvx shims are .cmd files which must be launched through cmd.exe.</summary>
    private static (string File, IReadOnlyList<string> Args) ResolveCommand(string command, IReadOnlyList<string> args)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || Path.HasExtension(command)) return (command, args);
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (File.Exists(Path.Combine(dir, command + ".exe"))) return (command + ".exe", args);
            if (File.Exists(Path.Combine(dir, command + ".cmd")) || File.Exists(Path.Combine(dir, command + ".bat")))
                return ("cmd.exe", ["/d", "/s", "/c", command, .. args]);
        }
        return (command, args);
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (await _process.StandardOutput.ReadLineAsync() is { } line)
            {
                if (line.Length == 0 || line[0] != '{') continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("id", out var idEl) || !idEl.TryGetInt64(out var id)) continue; // notification
                    if (!_pending.TryRemove(id, out var tcs)) continue;
                    if (root.TryGetProperty("error", out var err))
                        tcs.TrySetException(new InvalidOperationException("MCP error: " + err.GetRawText()));
                    else
                        tcs.TrySetResult(root.TryGetProperty("result", out var r) ? r.Clone() : default);
                }
                catch (JsonException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
        foreach (var p in _pending.Values) p.TrySetException(new IOException("MCP server exited. " + StdErr));
    }

    public string StdErr { get { lock (_stderr) return _stderr.ToString(); } }

    private async Task<JsonElement> SendAsync(string method, Action<Utf8JsonWriter>? p, CancellationToken ct, TimeSpan? timeout = null)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        await NotifyRawAsync(JsonRpc.Request(id, method, p), ct);
        try
        {
            return await tcs.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(120), ct);
        }
        finally { _pending.TryRemove(id, out _); }
    }

    private async Task NotifyRawAsync(byte[] payload, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            var stream = _process.StandardInput.BaseStream;
            await stream.WriteAsync(payload, ct);
            stream.WriteByte((byte)'\n');
            await stream.FlushAsync(ct);
        }
        finally { _writeLock.Release(); }
    }

    public async Task InitializeAsync(CancellationToken ct)
    {
        // First start via npx/uvx may download packages; allow a generous timeout.
        await SendAsync("initialize", JsonRpc.InitializeParams, ct, TimeSpan.FromMinutes(3));
        await NotifyRawAsync(JsonRpc.Request(-1, "notifications/initialized", null), ct);
    }

    public async Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken ct) =>
        JsonRpc.ParseTools(await SendAsync("tools/list", null, ct));

    public async Task<FunctionResult> CallToolAsync(string name, JsonElement arguments, CancellationToken ct) =>
        JsonRpc.ParseCallResult(await SendAsync("tools/call", JsonRpc.CallParams(name, arguments), ct, TimeSpan.FromMinutes(5)));

    public async ValueTask DisposeAsync()
    {
        try
        {
            _process.StandardInput.Close();
            if (!_process.WaitForExit(1500)) _process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception) { }
        await Task.WhenAny(_readerLoop, Task.Delay(1000));
        _process.Dispose();
    }
}

/// <summary>Streamable-HTTP MCP transport: POST JSON-RPC, response as JSON or a single SSE message.</summary>
public sealed class HttpMcpClient(HttpClient http, Uri url, IReadOnlyDictionary<string, string> headers) : IMcpClient
{
    private long _nextId;
    private string? _session;

    private async Task<JsonElement> SendAsync(string method, Action<Utf8JsonWriter>? p, bool notification, CancellationToken ct)
    {
        var id = notification ? -1 : Interlocked.Increment(ref _nextId);
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(JsonRpc.Request(id, method, p)) };
        req.Content.Headers.ContentType = new("application/json");
        req.Headers.Accept.ParseAdd("application/json");
        req.Headers.Accept.ParseAdd("text/event-stream");
        if (_session is not null) req.Headers.Add("Mcp-Session-Id", _session);
        foreach (var (k, v) in headers) req.Headers.TryAddWithoutValidation(k, v);
        using var resp = await http.SendAsync(req, ct);
        if (resp.Headers.TryGetValues("Mcp-Session-Id", out var s)) _session = s.FirstOrDefault();
        resp.EnsureSuccessStatusCode();
        if (notification) return default;
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (resp.Content.Headers.ContentType?.MediaType == "text/event-stream")
        {
            body = string.Join('\n', body.Split('\n').Where(l => l.StartsWith("data:", StringComparison.Ordinal)).Select(l => l[5..].Trim()).Where(l => l.StartsWith('{')).LastOrDefault() ?? "{}");
        }
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("error", out var err)) throw new InvalidOperationException("MCP error: " + err.GetRawText());
        return doc.RootElement.TryGetProperty("result", out var r) ? r.Clone() : default;
    }

    public async Task InitializeAsync(CancellationToken ct)
    {
        await SendAsync("initialize", JsonRpc.InitializeParams, false, ct);
        await SendAsync("notifications/initialized", null, true, ct);
    }

    public async Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken ct) => JsonRpc.ParseTools(await SendAsync("tools/list", null, false, ct));

    public async Task<FunctionResult> CallToolAsync(string name, JsonElement arguments, CancellationToken ct) =>
        JsonRpc.ParseCallResult(await SendAsync("tools/call", JsonRpc.CallParams(name, arguments), false, ct));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed record McpServerStatus(string ServerId, string Workspace, bool Connected, int ToolCount, string? Error, DateTimeOffset? ConnectedAt);

/// <summary>
/// Starts MCP servers lazily and caches their tool lists. Servers whose arguments reference
/// <c>{workspace}</c> get one process per project workspace so file access stays scoped.
/// </summary>
public sealed class McpManager(IDocumentStore<McpServerConfig> store, ISecretProvider secrets, IHttpClientFactory httpFactory, ILogger<McpManager> log) : IAsyncDisposable
{
    private sealed class Connection
    {
        public required IMcpClient Client { get; init; }
        public required IReadOnlyList<McpToolInfo> Tools { get; init; }
        public DateTimeOffset ConnectedAt { get; } = DateTimeOffset.UtcNow;
    }

    private readonly ConcurrentDictionary<string, Lazy<Task<Connection>>> _connections = new();
    private readonly ConcurrentDictionary<string, string> _errors = new();

    public Task<IReadOnlyList<McpServerConfig>> ListAsync(CancellationToken ct = default) => store.ListAsync(ct);

    public IReadOnlyList<McpServerStatus> Status()
    {
        var list = new List<McpServerStatus>();
        foreach (var (key, lazy) in _connections)
        {
            var parts = key.Split('|', 2);
            if (lazy.IsValueCreated && lazy.Value.IsCompletedSuccessfully)
                list.Add(new(parts[0], parts[1], true, lazy.Value.Result.Tools.Count, null, lazy.Value.Result.ConnectedAt));
        }
        foreach (var (key, err) in _errors)
        {
            var parts = key.Split('|', 2);
            list.Add(new(parts[0], parts[1], false, 0, err, null));
        }
        return list;
    }

    private static bool IsWorkspaceScoped(McpServerConfig c) => c.Args.Any(a => a.Contains("{workspace}", StringComparison.Ordinal));

    public async Task<IReadOnlyList<McpToolInfo>> GetToolsAsync(McpServerConfig config, string workspace, CancellationToken ct) =>
        (await ConnectAsync(config, workspace, ct)).Tools;

    public async Task<FunctionResult> CallAsync(McpServerConfig config, string workspace, string tool, JsonElement args, CancellationToken ct)
    {
        var conn = await ConnectAsync(config, workspace, ct);
        return await conn.Client.CallToolAsync(tool, args, ct);
    }

    private async Task<Connection> ConnectAsync(McpServerConfig config, string workspace, CancellationToken ct)
    {
        var key = $"{config.Id}|{(IsWorkspaceScoped(config) ? Path.GetFullPath(workspace) : "*")}";
        var lazy = _connections.GetOrAdd(key, _ => new Lazy<Task<Connection>>(() => StartAsync(config, workspace)));
        try
        {
            var conn = await lazy.Value.WaitAsync(ct);
            _errors.TryRemove(key, out _);
            return conn;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _connections.TryRemove(key, out _);
            _errors[key] = ex.Message;
            throw new InvalidOperationException($"MCP server '{config.Name}' is unavailable: {ex.Message}", ex);
        }
    }

    private async Task<Connection> StartAsync(McpServerConfig config, string workspace)
    {
        Directory.CreateDirectory(workspace);
        var env = new Dictionary<string, string>();
        foreach (var (k, v) in config.Env)
            env[k] = v.StartsWith("secret:", StringComparison.Ordinal) ? secrets.Get(v[7..]) ?? "" : v;
        IMcpClient client;
        if (config.Transport == "http")
        {
            client = new HttpMcpClient(httpFactory.CreateClient("marbots-mcp"), new Uri(config.Url!), env);
        }
        else
        {
            var resolved = new McpServerConfig
            {
                Id = config.Id, Name = config.Name, Command = config.Command,
                Args = config.Args.Select(a => a.Replace("{workspace}", Path.GetFullPath(workspace), StringComparison.Ordinal)).ToList(),
            };
            client = new StdioMcpClient(resolved, env, workspace);
        }
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        try
        {
            await client.InitializeAsync(cts.Token);
            var tools = await client.ListToolsAsync(cts.Token);
            log.LogInformation("MCP server {Server} connected with {Count} tools", config.Name, tools.Count);
            return new Connection { Client = client, Tools = tools };
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    public async Task RestartAsync(string serverId)
    {
        foreach (var key in _connections.Keys.Where(k => k.StartsWith(serverId + "|", StringComparison.Ordinal)).ToList())
        {
            if (_connections.TryRemove(key, out var lazy) && lazy.IsValueCreated && lazy.Value.IsCompletedSuccessfully)
                await lazy.Value.Result.Client.DisposeAsync();
        }
        foreach (var key in _errors.Keys.Where(k => k.StartsWith(serverId + "|", StringComparison.Ordinal)).ToList()) _errors.TryRemove(key, out _);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var lazy in _connections.Values)
            if (lazy.IsValueCreated && lazy.Value.IsCompletedSuccessfully) await lazy.Value.Result.Client.DisposeAsync();
        _connections.Clear();
    }

    public static string ToolName(string serverId, string tool)
    {
        var name = $"mcp__{Sanitize(serverId)}__{Sanitize(tool)}";
        return name.Length <= 64 ? name : name[..64];
    }

    private static string Sanitize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s) sb.Append(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_');
        return sb.ToString();
    }

    public static PermissionCategory CategoryFor(McpServerConfig c) => c.PermissionProfile switch
    {
        "readonly" => PermissionCategory.ReadOnly,
        "network" => PermissionCategory.Network,
        "external" => PermissionCategory.ExternalCommunication,
        "process" => PermissionCategory.ProcessExecution,
        _ => PermissionCategory.WorkspaceWrite,
    };
}

/// <summary>Adapts one MCP tool to the kernel-function contract so policy, telemetry and audit apply uniformly.</summary>
public sealed class McpToolFunction(McpManager manager, McpServerConfig server, McpToolInfo tool) : IKernelFunction
{
    public FunctionDescriptor Descriptor { get; } = new(
        McpManager.ToolName(server.Id, tool.Name),
        $"[MCP {server.Name}] {tool.Description}",
        tool.InputSchema, "mcp:" + server.Id, McpManager.CategoryFor(server), RiskLevel.Medium, 300);

    public async ValueTask<FunctionResult> InvokeAsync(FunctionCall call, FunctionExecutionContext context, CancellationToken cancellationToken)
    {
        try
        {
            return await manager.CallAsync(server, context.WorkspacePath, tool.Name, call.Arguments, cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException or HttpRequestException)
        {
            return FunctionResult.Fail(ex.Message);
        }
    }
}
