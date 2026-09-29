using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.Json;
using DotCode.Abstractions;
using DotCode.Engine.Permissions;
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
    public IReadOnlyList<string> CliArgs { get; init; } = [];
    /// <summary>ws://host:port for Connect mode.</summary>
    public Uri? ServerUrl { get; init; }
    public string? Token { get; init; }
    /// <summary>Default working directory for sessions (and of the server process).</summary>
    public string? Cwd { get; init; }
    public IReadOnlyDictionary<string, string>? Environment { get; init; }
}

/// <summary>A model offered by a configured provider.</summary>
public sealed record ModelInfo(string Provider, string Id, string QualifiedId);

/// <summary>A saved session.</summary>
public sealed record SessionMetadata(string Id, string? Title, string FirstPrompt, DateTimeOffset Modified, int MessageCount);

/// <summary>Entry point of the DotCode SDK: <c>client → session → SendAndWaitAsync / On</c>, with typed custom tools,
/// permission handlers and any LLM provider (BYOK). The same API works across Spawn, Connect and InProcess modes.</summary>
/// <example>
/// <code>
/// await using var client = new DotCodeClient();
/// await client.StartAsync();
/// await using var session = await client.CreateSessionAsync(new SessionConfig
/// {
///     Model = "anthropic:claude-sonnet-4-5",
///     OnPermissionRequest = PermissionHandler.ApproveAll,
/// });
/// session.On&lt;AssistantTextDeltaEvent&gt;(e =&gt; Console.Write(e.Text));
/// var result = await session.SendAndWaitAsync("Summarize README.md");
/// </code>
/// </example>
public sealed class DotCodeClient : IAsyncDisposable
{
    private readonly DotCodeClientOptions _options;
    private Process? _process;
    private JsonRpcConnection? _rpc;
    private IMessageTransport? _transport;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, RemoteSession> _sessions = new();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<AgentEvent>> _early = new();
    private int _opening;
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private Task? _loop;

    public DotCodeClient(DotCodeClientOptions? options = null) => _options = options ?? new DotCodeClientOptions();

    public bool IsStarted => _rpc is not null || _options.Mode == ClientMode.InProcess;
    public string? ServerVersion { get; private set; }

    /// <summary>Starts the server (Spawn) or connects (Connect) and performs the handshake. Other methods call it lazily.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (IsStarted) return;
        await _startGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsStarted) return;
            if (_options.Mode == ClientMode.Connect)
            {
                var ws = new ClientWebSocket();
                if (_options.Token is { } token) ws.Options.SetRequestHeader("Authorization", "Bearer " + token);
                await ws.ConnectAsync(_options.ServerUrl ?? throw new InvalidOperationException("ServerUrl is required in Connect mode"), ct).ConfigureAwait(false);
                _transport = new WebSocketTransport(ws);
            }
            else
            {
                var cli = _options.CliPath ?? System.Environment.GetEnvironmentVariable("DOTCODE_CLI_PATH") ?? "dotcode";
                var psi = cli.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? new ProcessStartInfo("dotnet") { ArgumentList = { cli } } : new ProcessStartInfo(cli);
                psi.ArgumentList.Add("serve");
                foreach (var a in _options.CliArgs) psi.ArgumentList.Add(a);
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
            }

            var rpc = new JsonRpcConnection(_transport) { OnRequest = OnServerRequestAsync, OnNotification = OnNotification };
            _loop = rpc.RunAsync(_cts.Token);
            var init = await rpc.RequestAsync("initialize", w =>
            {
                w.WriteStartObject();
                w.WriteString("protocolVersion", AgentServer.ProtocolVersion);
                w.WriteStartObject("clientInfo"); w.WriteString("name", "dotcode-sdk-dotnet"); w.WriteString("version", Engine.AppInfo.Version); w.WriteEndObject();
                w.WriteStartObject("capabilities"); w.WriteBoolean("permissions", true); w.WriteBoolean("questions", true); w.WriteEndObject();
                w.WriteEndObject();
            }, ct).ConfigureAwait(false);
            ServerVersion = init.GetProp("serverInfo")?.GetString("version");
            _rpc = rpc;
        }
        finally
        {
            _startGate.Release();
        }
    }

    /// <summary>Creates a session. Without <see cref="SessionConfig.OnPermissionRequest"/> it is deny-by-default.</summary>
    public async Task<DotCodeSession> CreateSessionAsync(SessionConfig? config = null, CancellationToken ct = default)
    {
        config ??= new SessionConfig();
        if (_options.Mode == ClientMode.InProcess) return InProcessSession.Create(config, _options.Cwd);
        return await OpenAsync("session.create", config, w => config.Write(w, _options.Cwd), ct).ConfigureAwait(false);
    }

    /// <summary>Resumes a saved session (<see cref="ResumeSessionConfig.Fork"/> continues it under a new id).</summary>
    public async Task<DotCodeSession> ResumeSessionAsync(string sessionId, ResumeSessionConfig? config = null, CancellationToken ct = default)
    {
        config ??= new ResumeSessionConfig();
        if (_options.Mode == ClientMode.InProcess) throw new NotSupportedException("Resuming is available in Spawn and Connect modes.");
        return await OpenAsync("session.resume", config, w => config.Write(w, _options.Cwd, sessionId, config.Fork), ct).ConfigureAwait(false);
    }

    private async Task<DotCodeSession> OpenAsync(string method, SessionConfig config, Action<Utf8JsonWriter> write, CancellationToken ct)
    {
        await StartAsync(ct).ConfigureAwait(false);
        Interlocked.Increment(ref _opening);
        try
        {
            var info = await _rpc!.RequestAsync(method, write, ct).ConfigureAwait(false);
            var session = new RemoteSession(_rpc, info, config, id => _sessions.TryRemove(id, out _));
            _sessions[session.SessionId] = session;
            if (_early.TryRemove(session.SessionId, out var early))
                while (early.TryDequeue(out var e)) session.Receive(e);
            return session;
        }
        finally
        {
            if (Interlocked.Decrement(ref _opening) == 0) _early.Clear();
        }
    }

    public async Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken ct = default)
    {
        await StartAsync(ct).ConfigureAwait(false);
        var r = await _rpc!.RequestAsync("models.list", WriteCwd, ct).ConfigureAwait(false);
        return r.GetProp("models") is { ValueKind: JsonValueKind.Array } models
            ? [.. models.EnumerateArray().Select(m => new ModelInfo(m.GetString("provider") ?? "", m.GetString("id") ?? "", m.GetString("qualifiedId") ?? ""))]
            : [];
    }

    public async Task<IReadOnlyList<SessionMetadata>> ListSessionsAsync(CancellationToken ct = default)
    {
        await StartAsync(ct).ConfigureAwait(false);
        var r = await _rpc!.RequestAsync("session.list", WriteCwd, ct).ConfigureAwait(false);
        return r.GetProp("sessions") is { ValueKind: JsonValueKind.Array } sessions
            ? [.. sessions.EnumerateArray().Select(s => new SessionMetadata(s.GetString("id") ?? "", s.GetString("title"), s.GetString("firstPrompt") ?? "",
                DateTimeOffset.TryParse(s.GetString("modified"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d) ? d : default,
                s.GetInt("messageCount") ?? 0))]
            : [];
    }

    public async Task PingAsync(CancellationToken ct = default)
    {
        await StartAsync(ct).ConfigureAwait(false);
        await _rpc!.RequestAsync("ping", w => { w.WriteStartObject(); w.WriteEndObject(); }, ct).ConfigureAwait(false);
    }

    private void WriteCwd(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        if (_options.Cwd is { } c) w.WriteString("cwd", c);
        w.WriteEndObject();
    }

    private void OnNotification(string method, JsonElement p)
    {
        if (method != "session.event" || p.GetString("sessionId") is not { } id || p.GetProp("event") is not { } ev) return;
        if (ev.Deserialize(AbstractionsJsonContext.Default.AgentEvent) is not { } e) return;
        if (_sessions.TryGetValue(id, out var s)) s.Receive(e);
        else if (Volatile.Read(ref _opening) > 0) _early.GetOrAdd(id, _ => new()).Enqueue(e);
    }

    private async Task<JsonElement?> OnServerRequestAsync(string method, JsonElement p, CancellationToken ct)
    {
        if (p.GetString("sessionId") is not { } id || !_sessions.TryGetValue(id, out var s))
            throw new JsonRpcException(JsonRpcException.SessionNotFound, "Unknown session");
        return await s.HandleServerRequestAsync(method, p, ct).ConfigureAwait(false);
    }

    /// <summary>Stops the server gracefully (same as disposing the client).</summary>
    public Task StopAsync() => DisposeAsync().AsTask();

    /// <summary>Kills the server process without a graceful shutdown.</summary>
    public void ForceStop()
    {
        _cts.Cancel();
        if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true);
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
        _rpc = null;
    }
}

/// <summary>A session hosted by a <c>dotcode serve</c> process (Spawn and Connect modes).</summary>
internal sealed class RemoteSession : DotCodeSession
{
    private readonly JsonRpcConnection _rpc;
    private readonly Action<string> _onClose;
    private string _model;

    public RemoteSession(JsonRpcConnection rpc, JsonElement info, SessionConfig config, Action<string> onClose) : base(config)
    {
        _rpc = rpc;
        _onClose = onClose;
        SessionId = info.GetString("sessionId")!;
        _model = info.GetString("model") ?? "";
    }

    public override string SessionId { get; }
    public override string Model => _model;

    internal void Receive(AgentEvent e)
    {
        if (e is ModelChangedEvent mc) _model = mc.Model;
        Dispatch(e);
    }

    internal async Task<JsonElement?> HandleServerRequestAsync(string method, JsonElement p, CancellationToken ct)
    {
        switch (method)
        {
            case "permission.request":
            {
                var request = p.GetProp("request")!.Value.Deserialize(AbstractionsJsonContext.Default.PermissionRequest)!;
                var decision = await DecidePermissionAsync(request, ct).ConfigureAwait(false);
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
                var answers = await AnswerQuestionsAsync(questions, ct).ConfigureAwait(false) ?? [];
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
                var review = await ReviewPlanAsync(p.GetString("plan") ?? "", ct).ConfigureAwait(false);
                return DotCodeJson.Build(w =>
                {
                    w.WriteStartObject();
                    w.WriteString("approval", !review.Approved ? "reject" : review.AcceptEdits ? "approve_accept_edits" : "approve");
                    if (review.Feedback is { } f) w.WriteString("feedback", f);
                    w.WriteEndObject();
                });
            }
            case "tool.call":
            {
                var result = await CallToolAsync(p.GetString("toolUseId") ?? "", p.GetString("name") ?? "", p.GetProp("input") ?? DotCodeJson.EmptyObject, ct).ConfigureAwait(false);
                return DotCodeJson.Build(w =>
                {
                    w.WriteStartObject();
                    if (result.Images.Count == 0) w.WriteString("content", result.TextResultForLlm);
                    else
                    {
                        w.WriteStartArray("content");
                        w.WriteStartObject(); w.WriteString("type", "text"); w.WriteString("text", result.TextResultForLlm); w.WriteEndObject();
                        foreach (var img in result.Images)
                        {
                            w.WriteStartObject(); w.WriteString("type", "image"); w.WriteString("data", img.Base64Data); w.WriteString("mediaType", img.MediaType); w.WriteEndObject();
                        }
                        w.WriteEndArray();
                    }
                    if (result.IsError) w.WriteBoolean("isError", true);
                    w.WriteEndObject();
                });
            }
        }
        throw new JsonRpcException(JsonRpcException.MethodNotFound, method);
    }

    private protected override async Task<SessionResult> RunTurnAsync(MessageOptions message, CancellationToken ct)
    {
        var r = await _rpc.RequestAsync("session.send", w =>
        {
            w.WriteStartObject();
            w.WriteString("sessionId", SessionId);
            w.WriteString("prompt", message.Prompt);
            if (message.Attachments.Count > 0)
            {
                w.WriteStartArray("attachments");
                foreach (var a in message.Attachments)
                {
                    w.WriteStartObject();
                    switch (a)
                    {
                        case Attachment.File f: w.WriteString("type", "file"); w.WriteString("path", f.Path); break;
                        case Attachment.Image i: w.WriteString("type", "image"); w.WriteString("data", i.Base64Data); w.WriteString("mediaType", i.MediaType); break;
                    }
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            w.WriteEndObject();
        }, ct).ConfigureAwait(false);
        return new SessionResult(r.GetString("result") ?? "", r.GetString("stopReason") ?? "", r.GetBool("isError") == true, r.GetString("error"),
            r.GetProp("usage")?.Deserialize(AbstractionsJsonContext.Default.Usage) ?? Usage.Zero,
            r.GetProp("costUsd")?.GetDecimal() ?? 0, r.GetProp("totalCostUsd")?.GetDecimal() ?? 0,
            r.GetProp("durationMs")?.GetInt64() ?? 0, r.GetInt("numModelCalls") ?? 0);
    }

    private Task Call(string method, Action<Utf8JsonWriter>? extra = null, CancellationToken ct = default) =>
        _rpc.RequestAsync(method, w => { w.WriteStartObject(); w.WriteString("sessionId", SessionId); extra?.Invoke(w); w.WriteEndObject(); }, ct);

    public override Task AbortAsync() => Call("session.abort");
    public override async Task SetModelAsync(string model, CancellationToken ct = default) { await Call("session.setModel", w => w.WriteString("model", model), ct).ConfigureAwait(false); _model = model; }
    public override Task SetPermissionModeAsync(PermissionMode mode, CancellationToken ct = default) => Call("session.setMode", w => w.WriteString("mode", mode.ToSetting()), ct);
    public override Task SetReasoningEffortAsync(ReasoningEffort effort, CancellationToken ct = default) => Call("session.setEffort", w => w.WriteString("effort", effort.ToString().ToLowerInvariant()), ct);
    public override Task CompactAsync(string? instructions = null, CancellationToken ct = default) => Call("session.compact", w => { if (instructions is not null) w.WriteString("instructions", instructions); }, ct);

    public override async Task<IReadOnlyList<Message>> GetMessagesAsync(CancellationToken ct = default)
    {
        var r = await _rpc.RequestAsync("session.messages", w => { w.WriteStartObject(); w.WriteString("sessionId", SessionId); w.WriteEndObject(); }, ct).ConfigureAwait(false);
        return r.GetProp("messages")?.Deserialize(AbstractionsJsonContext.Default.ListMessage) ?? [];
    }

    public override async ValueTask DisposeAsync()
    {
        try { await Call("session.close").ConfigureAwait(false); } catch (Exception) { }
        _onClose(SessionId);
    }
}
