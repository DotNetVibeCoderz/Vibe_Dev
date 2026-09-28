using System.Diagnostics;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using DotCode.Abstractions;
using DotCode.Engine.Agent;
using DotCode.Protocol;

namespace DotCode.Sdk;

/// <summary>How the client reaches the agent engine.</summary>
public enum ClientMode
{
    /// <summary>Start a <c>dotcode serve</c> child process and talk JSON-RPC over its stdio (default).</summary>
    Spawn,
    /// <summary>Connect to an already running server over WebSocket.</summary>
    Connect,
    /// <summary>Run the engine inside this process (no child process, lowest latency; .NET only).</summary>
    InProcess,
}

public sealed class DotCodeClientOptions
{
    public ClientMode Mode { get; init; } = ClientMode.Spawn;
    /// <summary>Path to the dotcode executable (Spawn). Defaults to DOTCODE_CLI_PATH or "dotcode" on PATH.</summary>
    public string? CliPath { get; init; }
    /// <summary>Extra arguments for <c>dotcode serve</c>.</summary>
    public IReadOnlyList<string> ServerArgs { get; init; } = [];
    /// <summary>ws://host:port for Connect mode.</summary>
    public Uri? ServerUrl { get; init; }
    public string? Token { get; init; }
    /// <summary>Default working directory for sessions.</summary>
    public string? Cwd { get; init; }
    public IReadOnlyDictionary<string, string>? Environment { get; init; }
}

/// <summary>Entry point of the DotCode SDK: <c>Client → Session → SendAsync / StreamAsync</c>, with custom tools,
/// permission handlers and any LLM provider (BYOK). The same API works across Spawn, Connect and InProcess modes.</summary>
/// <example>
/// <code>
/// await using var client = new DotCodeClient();
/// await using var session = await client.CreateSessionAsync(new SessionOptions { Model = "anthropic:claude-sonnet-4-5" });
/// var result = await session.SendAsync("Summarize README.md");
/// Console.WriteLine(result.Result);
/// </code>
/// </example>
public sealed class DotCodeClient : IAsyncDisposable
{
    private readonly DotCodeClientOptions _options;
    private Process? _process;
    private JsonRpcConnection? _rpc;
    private IMessageTransport? _transport;
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<string, RemoteSession> _sessions = [];
    private readonly Lock _gate = new();
    private Task? _loop;

    public DotCodeClient(DotCodeClientOptions? options = null) => _options = options ?? new DotCodeClientOptions();

    public bool IsStarted => _rpc is not null || _options.Mode == ClientMode.InProcess;
    public string? ServerVersion { get; private set; }

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (IsStarted) return;
        switch (_options.Mode)
        {
            case ClientMode.InProcess:
                return;
            case ClientMode.Connect:
            {
                var ws = new ClientWebSocket();
                if (_options.Token is { } token) ws.Options.SetRequestHeader("Authorization", "Bearer " + token);
                await ws.ConnectAsync(_options.ServerUrl ?? throw new InvalidOperationException("ServerUrl is required in Connect mode"), ct).ConfigureAwait(false);
                _transport = new WebSocketTransport(ws);
                break;
            }
            default:
            {
                var cli = _options.CliPath ?? System.Environment.GetEnvironmentVariable("DOTCODE_CLI_PATH") ?? "dotcode";
                var psi = cli.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? new ProcessStartInfo("dotnet") { ArgumentList = { cli } } : new ProcessStartInfo(cli);
                psi.ArgumentList.Add("serve");
                foreach (var a in _options.ServerArgs) psi.ArgumentList.Add(a);
                psi.RedirectStandardInput = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.WorkingDirectory = _options.Cwd ?? System.Environment.CurrentDirectory;
                if (_options.Environment is not null) foreach (var (k, v) in _options.Environment) psi.Environment[k] = v;
                try { _process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start dotcode"); }
                catch (System.ComponentModel.Win32Exception ex)
                {
                    throw new InvalidOperationException($"Could not start '{cli}'. Install the DotCode CLI (dotnet tool install -g DotCode.Cli) or set DOTCODE_CLI_PATH. ({ex.Message})", ex);
                }
                _process.ErrorDataReceived += (_, _) => { };
                _process.BeginErrorReadLine();
                _transport = new StreamTransport(_process.StandardOutput.BaseStream, _process.StandardInput.BaseStream, contentLength: false);
                break;
            }
        }

        _rpc = new JsonRpcConnection(_transport!) { OnRequest = OnServerRequestAsync, OnNotification = OnNotification };
        _loop = _rpc.RunAsync(_cts.Token);
        var init = await _rpc.RequestAsync("initialize", w =>
        {
            w.WriteStartObject();
            w.WriteString("protocolVersion", AgentServer.ProtocolVersion);
            w.WriteStartObject("clientInfo"); w.WriteString("name", "dotcode-sdk-dotnet"); w.WriteString("version", Engine.AppInfo.Version); w.WriteEndObject();
            w.WriteStartObject("capabilities"); w.WriteBoolean("permissions", true); w.WriteBoolean("questions", true); w.WriteEndObject();
            w.WriteEndObject();
        }, ct).ConfigureAwait(false);
        ServerVersion = init.GetProp("serverInfo")?.GetString("version");
    }

    public async Task<IDotCodeSession> CreateSessionAsync(SessionOptions? options = null, CancellationToken ct = default)
    {
        options ??= new SessionOptions();
        if (_options.Mode == ClientMode.InProcess) return InProcessSession.Create(options, _options.Cwd);
        await StartAsync(ct).ConfigureAwait(false);
        var info = await _rpc!.RequestAsync("session.create", w => options.Write(w, _options.Cwd), ct).ConfigureAwait(false);
        var session = new RemoteSession(_rpc, info, options);
        lock (_gate) _sessions[session.Id] = session;
        return session;
    }

    public async Task<IDotCodeSession> ResumeSessionAsync(string sessionId, SessionOptions? options = null, bool fork = false, CancellationToken ct = default)
    {
        options ??= new SessionOptions();
        await StartAsync(ct).ConfigureAwait(false);
        var info = await _rpc!.RequestAsync("session.resume", w => options.Write(w, _options.Cwd, sessionId, fork), ct).ConfigureAwait(false);
        var session = new RemoteSession(_rpc, info, options);
        lock (_gate) _sessions[session.Id] = session;
        return session;
    }

    public async Task<JsonElement> ListModelsAsync(CancellationToken ct = default)
    {
        await StartAsync(ct).ConfigureAwait(false);
        return await _rpc!.RequestAsync("models.list", w => { w.WriteStartObject(); if (_options.Cwd is { } c) w.WriteString("cwd", c); w.WriteEndObject(); }, ct).ConfigureAwait(false);
    }

    public async Task<JsonElement> ListSessionsAsync(CancellationToken ct = default)
    {
        await StartAsync(ct).ConfigureAwait(false);
        return await _rpc!.RequestAsync("session.list", w => { w.WriteStartObject(); if (_options.Cwd is { } c) w.WriteString("cwd", c); w.WriteEndObject(); }, ct).ConfigureAwait(false);
    }

    private void OnNotification(string method, JsonElement p)
    {
        if (method != "session.event" || p.GetString("sessionId") is not { } id) return;
        RemoteSession? s;
        lock (_gate) _sessions.TryGetValue(id, out s);
        if (s is null || p.GetProp("event") is not { } ev) return;
        var e = ev.Deserialize(AbstractionsJsonContext.Default.AgentEvent);
        if (e is not null) s.Dispatch(e);
    }

    private async Task<JsonElement?> OnServerRequestAsync(string method, JsonElement p, CancellationToken ct)
    {
        RemoteSession? s = null;
        if (p.GetString("sessionId") is { } id) lock (_gate) _sessions.TryGetValue(id, out s);
        if (s is null) throw new JsonRpcException(JsonRpcException.SessionNotFound, "Unknown session");
        return await s.HandleServerRequestAsync(method, p, ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_rpc is not null)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _rpc.RequestAsync("shutdown", w => { w.WriteStartObject(); w.WriteEndObject(); }, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception) { }
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_transport is not null) await _transport.DisposeAsync().ConfigureAwait(false);
        if (_process is { HasExited: false })
        {
            try { _process.StandardInput.Close(); } catch { }
            if (!_process.WaitForExit(2000)) _process.Kill(entireProcessTree: true);
        }
        _process?.Dispose();
    }
}

/// <summary>Common session surface for remote (JSON-RPC) and in-process sessions.</summary>
public interface IDotCodeSession : IAsyncDisposable
{
    string Id { get; }
    string Model { get; }
    /// <summary>Raised for every engine event (text deltas, tool calls, usage…).</summary>
    event Action<AgentEvent>? EventReceived;
    /// <summary>Runs a prompt to completion and returns the final result.</summary>
    Task<SessionResult> SendAsync(string prompt, CancellationToken ct = default);
    /// <summary>Runs a prompt, yielding events as they happen; the last event is <see cref="TurnCompletedEvent"/>.</summary>
    IAsyncEnumerable<AgentEvent> StreamAsync(string prompt, CancellationToken ct = default);
    Task AbortAsync();
    Task SetModelAsync(string model, CancellationToken ct = default);
    Task SetPermissionModeAsync(string mode, CancellationToken ct = default);
    Task CompactAsync(string? instructions = null, CancellationToken ct = default);
    Task<IReadOnlyList<Message>> GetMessagesAsync(CancellationToken ct = default);
}

public sealed record SessionResult(string Result, string StopReason, bool IsError, string? Error, Usage Usage, decimal CostUsd, decimal TotalCostUsd, long DurationMs, int ModelCalls);

internal sealed class RemoteSession : IDotCodeSession
{
    private readonly JsonRpcConnection _rpc;
    private readonly SessionOptions _options;
    private Channel<AgentEvent>? _stream;

    public string Id { get; }
    public string Model { get; private set; }
    public event Action<AgentEvent>? EventReceived;

    public RemoteSession(JsonRpcConnection rpc, JsonElement info, SessionOptions options)
    {
        _rpc = rpc;
        _options = options;
        Id = info.GetString("sessionId")!;
        Model = info.GetString("model") ?? "";
    }

    internal void Dispatch(AgentEvent e)
    {
        if (e is ModelChangedEvent mc) Model = mc.Model;
        EventReceived?.Invoke(e);
        _options.OnEvent?.Invoke(e);
        _stream?.Writer.TryWrite(e);
    }

    internal async Task<JsonElement?> HandleServerRequestAsync(string method, JsonElement p, CancellationToken ct)
    {
        switch (method)
        {
            case "permission.request":
            {
                var request = p.GetProp("request")!.Value.Deserialize(AbstractionsJsonContext.Default.PermissionRequest)!;
                var decision = _options.OnPermissionRequest is { } handler
                    ? await handler(request, ct).ConfigureAwait(false)
                    : PermissionDecision.Deny("No permission handler registered in the SDK host (deny by default).");
                return DotCodeJson.Build(w =>
                {
                    w.WriteStartObject();
                    w.WriteString("decision", decision.Kind switch
                    {
                        PermissionDecisionKind.AllowOnce => "allow",
                        PermissionDecisionKind.AllowAlways => "allow_always",
                        PermissionDecisionKind.AllowSession => "allow_session",
                        _ => "deny",
                    });
                    if (decision.Feedback is { } f) w.WriteString("feedback", f);
                    if (decision.Rule is { } r) w.WriteString("rule", r);
                    if (decision.UpdatedInput is { } ui) { w.WritePropertyName("updatedInput"); ui.WriteTo(w); }
                    w.WriteEndObject();
                });
            }
            case "user.question":
            {
                var questions = p.GetProp("questions")?.Deserialize(AbstractionsJsonContext.Default.ListUserQuestion) ?? [];
                var answers = _options.OnQuestion is { } q ? await q(questions, ct).ConfigureAwait(false) : [];
                return DotCodeJson.Build(w =>
                {
                    w.WriteStartObject();
                    w.WritePropertyName("answers");
                    JsonSerializer.Serialize(w, answers.ToList(), AbstractionsJsonContext.Default.ListUserQuestionAnswer);
                    w.WriteEndObject();
                });
            }
            case "plan.review":
            {
                var approve = _options.OnPlanReview is null || await _options.OnPlanReview(p.GetString("plan") ?? "", ct).ConfigureAwait(false);
                return DotCodeJson.Build(w => { w.WriteStartObject(); w.WriteString("approval", approve ? "approve" : "reject"); w.WriteEndObject(); });
            }
            case "tool.call":
            {
                var name = p.GetString("name");
                var tool = _options.Tools.FirstOrDefault(t => t.Name == name) ?? throw new JsonRpcException(JsonRpcException.MethodNotFound, $"Unknown host tool {name}");
                try
                {
                    var output = await tool.Handler(p.GetProp("input") ?? DotCodeJson.EmptyObject, ct).ConfigureAwait(false);
                    return DotCodeJson.Build(w => { w.WriteStartObject(); w.WriteString("content", output); w.WriteEndObject(); });
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    return DotCodeJson.Build(w => { w.WriteStartObject(); w.WriteString("content", $"Error: {ex.Message}"); w.WriteBoolean("isError", true); w.WriteEndObject(); });
                }
            }
        }
        throw new JsonRpcException(JsonRpcException.MethodNotFound, method);
    }

    public async Task<SessionResult> SendAsync(string prompt, CancellationToken ct = default)
    {
        var r = await _rpc.RequestAsync("session.send", w =>
        {
            w.WriteStartObject();
            w.WriteString("sessionId", Id);
            w.WriteString("prompt", prompt);
            w.WriteEndObject();
        }, ct).ConfigureAwait(false);
        return new SessionResult(r.GetString("result") ?? "", r.GetString("stopReason") ?? "", r.GetBool("isError") == true, r.GetString("error"),
            r.GetProp("usage")?.Deserialize(AbstractionsJsonContext.Default.Usage) ?? Usage.Zero,
            r.GetProp("costUsd")?.GetDecimal() ?? 0, r.GetProp("totalCostUsd")?.GetDecimal() ?? 0,
            r.GetProp("durationMs")?.GetInt64() ?? 0, r.GetInt("numModelCalls") ?? 0);
    }

    public async IAsyncEnumerable<AgentEvent> StreamAsync(string prompt, [EnumeratorCancellation] CancellationToken ct = default)
    {
        _stream = Channel.CreateUnbounded<AgentEvent>();
        var send = SendAsync(prompt, ct);
        _ = send.ContinueWith(_ => _stream.Writer.TryComplete(), TaskScheduler.Default);
        await foreach (var e in _stream.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            yield return e;
            if (e is TurnCompletedEvent { ParentToolUseId: null }) break;
        }
        await send.ConfigureAwait(false);
        _stream = null;
    }

    private Task Call(string method, Action<Utf8JsonWriter>? extra = null, CancellationToken ct = default) =>
        _rpc.RequestAsync(method, w => { w.WriteStartObject(); w.WriteString("sessionId", Id); extra?.Invoke(w); w.WriteEndObject(); }, ct);

    public Task AbortAsync() => Call("session.abort");
    public async Task SetModelAsync(string model, CancellationToken ct = default) { await Call("session.setModel", w => w.WriteString("model", model), ct).ConfigureAwait(false); Model = model; }
    public Task SetPermissionModeAsync(string mode, CancellationToken ct = default) => Call("session.setMode", w => w.WriteString("mode", mode), ct);
    public Task CompactAsync(string? instructions = null, CancellationToken ct = default) => Call("session.compact", w => { if (instructions is not null) w.WriteString("instructions", instructions); }, ct);

    public async Task<IReadOnlyList<Message>> GetMessagesAsync(CancellationToken ct = default)
    {
        var r = await _rpc.RequestAsync("session.messages", w => { w.WriteStartObject(); w.WriteString("sessionId", Id); w.WriteEndObject(); }, ct).ConfigureAwait(false);
        return r.GetProp("messages")?.Deserialize(AbstractionsJsonContext.Default.ListMessage) ?? [];
    }

    public async ValueTask DisposeAsync()
    {
        try { await Call("session.close").ConfigureAwait(false); } catch (Exception) { }
    }
}
