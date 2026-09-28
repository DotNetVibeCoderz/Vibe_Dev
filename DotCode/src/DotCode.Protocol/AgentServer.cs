using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DotCode.Abstractions;
using DotCode.Engine.Util;
using DotCode.Engine;
using DotCode.Engine.Agent;
using DotCode.Engine.Permissions;
using DotCode.Engine.Sessions;

namespace DotCode.Protocol;

/// <summary><c>dotcode serve</c>: exposes the engine over JSON-RPC 2.0 so any language can embed the same agent.
/// Transports: stdio (NDJSON or Content-Length framing) and WebSocket (localhost, bearer token).</summary>
public static class AgentServer
{
    public const string ProtocolVersion = "1.0";

    public static async Task<int> RunAsync(RuntimeOptions defaults, List<string> args, CancellationToken ct)
    {
        var port = args.IndexOf("--port") is var pi and >= 0 && pi + 1 < args.Count ? int.Parse(args[pi + 1]) : 0;
        var contentLength = args.Contains("--content-length");
        if (port > 0)
        {
            var token = args.IndexOf("--token") is var ti and >= 0 && ti + 1 < args.Count ? args[ti + 1] : Environment.GetEnvironmentVariable("DOTCODE_SERVER_TOKEN");
            await RunWebSocketAsync(defaults, port, token, ct).ConfigureAwait(false);
            return 0;
        }
        var transport = new StreamTransport(Console.OpenStandardInput(), Console.OpenStandardOutput(), contentLength);
        await using var session = new ServerSession(transport, defaults);
        await session.RunAsync(ct).ConfigureAwait(false);
        return 0;
    }

    private static async Task RunWebSocketAsync(RuntimeOptions defaults, int port, string? token, CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        Console.Error.WriteLine($"DotCode server listening on ws://127.0.0.1:{port}{(token is null ? " (no token!)" : "")}");
        while (!ct.IsCancellationRequested)
        {
            var client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            _ = Task.Run(async () =>
            {
                try
                {
                    var stream = client.GetStream();
                    var socket = await AcceptWebSocketAsync(stream, token, ct).ConfigureAwait(false);
                    if (socket is null) { client.Dispose(); return; }
                    await using var session = new ServerSession(new WebSocketTransport(socket), defaults);
                    await session.RunAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or WebSocketException) { }
                finally { client.Dispose(); }
            }, ct);
        }
    }

    /// <summary>Minimal RFC 6455 server handshake on a raw TCP stream (no HttpListener / admin rights needed).</summary>
    private static async Task<WebSocket?> AcceptWebSocketAsync(NetworkStream stream, string? token, CancellationToken ct)
    {
        var header = new StringBuilder();
        var buf = new byte[1];
        while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            if (await stream.ReadAsync(buf, ct).ConfigureAwait(false) == 0) return null;
            header.Append((char)buf[0]);
            if (header.Length > 16_384) return null;
        }
        var lines = header.ToString().Split("\r\n");
        string? key = null, auth = null;
        var path = lines[0].Split(' ').ElementAtOrDefault(1) ?? "/";
        foreach (var l in lines.Skip(1))
        {
            var colon = l.IndexOf(':');
            if (colon <= 0) continue;
            var name = l[..colon].Trim();
            var value = l[(colon + 1)..].Trim();
            if (name.Equals("Sec-WebSocket-Key", StringComparison.OrdinalIgnoreCase)) key = value;
            if (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)) auth = value;
        }
        var queryToken = path.Contains("token=", StringComparison.Ordinal) ? Uri.UnescapeDataString(path[(path.IndexOf("token=", StringComparison.Ordinal) + 6)..].Split('&')[0]) : null;
        if (token is not null && auth != "Bearer " + token && queryToken != token)
        {
            await stream.WriteAsync("HTTP/1.1 401 Unauthorized\r\nContent-Length: 0\r\n\r\n"u8.ToArray(), ct).ConfigureAwait(false);
            return null;
        }
        if (key is null) return null;
        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        var response = $"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(response), ct).ConfigureAwait(false);
        return WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true, KeepAliveInterval = TimeSpan.FromSeconds(30) });
    }
}

/// <summary>One client connection: owns its sessions and routes engine callbacks back to that client.</summary>
internal sealed class ServerSession : IAsyncDisposable
{
    private readonly JsonRpcConnection _rpc;
    private readonly IMessageTransport _transport;
    private readonly RuntimeOptions _defaults;
    private readonly ConcurrentDictionary<string, Hosted> _sessions = new();
    private readonly CancellationTokenSource _shutdown = new();
    private bool _clientHandlesPermissions;
    private bool _clientHandlesQuestions;

    private sealed class Hosted(AgentRuntime runtime, AgentSession session)
    {
        public AgentRuntime Runtime { get; } = runtime;
        public AgentSession Session { get; } = session;
        public CancellationTokenSource? TurnCts { get; set; }
    }

    public ServerSession(IMessageTransport transport, RuntimeOptions defaults)
    {
        _transport = transport;
        _defaults = defaults;
        _rpc = new JsonRpcConnection(transport) { OnRequest = HandleAsync };
    }

    public Task RunAsync(CancellationToken ct)
    {
        var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token);
        return _rpc.RunAsync(linked.Token);
    }

    private Hosted Get(JsonElement p) =>
        p.GetString("sessionId") is { } id && _sessions.TryGetValue(id, out var h) ? h
            : throw new JsonRpcException(JsonRpcException.SessionNotFound, $"Session not found: {p.GetString("sessionId")}");

    private async Task<JsonElement?> HandleAsync(string method, JsonElement p, CancellationToken ct)
    {
        switch (method)
        {
            case "initialize":
            {
                var caps = p.GetProp("capabilities");
                _clientHandlesPermissions = caps?.GetBool("permissions") ?? false;
                _clientHandlesQuestions = caps?.GetBool("questions") ?? false;
                return DotCodeJson.Build(w =>
                {
                    w.WriteStartObject();
                    w.WriteString("protocolVersion", AgentServer.ProtocolVersion);
                    w.WriteStartObject("serverInfo");
                    w.WriteString("name", "dotcode");
                    w.WriteString("version", AppInfo.Version);
                    w.WriteString("credit", AppInfo.Credit);
                    w.WriteEndObject();
                    w.WriteStartObject("capabilities");
                    w.WriteBoolean("hostTools", true);
                    w.WriteBoolean("permissions", true);
                    w.WriteBoolean("questions", true);
                    w.WriteBoolean("planReview", true);
                    w.WriteBoolean("sessionPersistence", true);
                    w.WriteBoolean("streaming", true);
                    w.WriteEndObject();
                    w.WriteEndObject();
                });
            }
            case "ping":
                return DotCodeJson.EmptyObject;
            case "shutdown":
                _ = Task.Run(async () => { await Task.Delay(100).ConfigureAwait(false); await _shutdown.CancelAsync().ConfigureAwait(false); });
                return DotCodeJson.EmptyObject;

            case "session.create":
            case "session.resume":
                return CreateSession(p, method == "session.resume");

            case "session.send":
            {
                var h = Get(p);
                if (h.Session.IsBusy) throw new JsonRpcException(JsonRpcException.Busy, "Session is busy; wait for the current turn or call session.abort");
                var prompt = p.GetString("prompt") ?? throw new JsonRpcException(JsonRpcException.InvalidParams, "prompt is required");
                var attachments = new List<ContentPart>();
                if (p.GetProp("attachments") is { ValueKind: JsonValueKind.Array } atts)
                    foreach (var a in atts.EnumerateArray())
                    {
                        if (a.GetString("type") == "image") attachments.Add(new ImagePart(a.GetString("data") ?? "", a.GetString("mediaType") ?? "image/png"));
                        else if (a.GetString("type") == "file" && a.GetString("path") is { } path) prompt += $" @\"{path}\"";
                    }
                h.TurnCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var expanded = await CommandExpander.ExpandAsync(h.Session, prompt, h.TurnCts.Token).ConfigureAwait(false);
                var result = await h.Session.RunTurnAsync(expanded.Prompt, attachments, h.TurnCts.Token).ConfigureAwait(false);
                h.TurnCts.Dispose();
                h.TurnCts = null;
                return DotCodeJson.Build(w =>
                {
                    w.WriteStartObject();
                    w.WriteString("sessionId", h.Session.Id);
                    w.WriteString("stopReason", result.StopReason.ToString());
                    w.WriteString("result", result.Text);
                    w.WriteBoolean("isError", result.IsError);
                    if (result.Error is not null) w.WriteString("error", result.Error);
                    w.WriteNumber("durationMs", (long)result.Duration.TotalMilliseconds);
                    w.WriteNumber("numModelCalls", result.ModelCalls);
                    w.WriteNumber("costUsd", result.CostUsd);
                    w.WriteNumber("totalCostUsd", h.Session.TotalCostUsd);
                    w.WritePropertyName("usage");
                    JsonSerializer.Serialize(w, result.Usage, AbstractionsJsonContext.Default.Usage);
                    w.WriteEndObject();
                });
            }
            case "session.abort":
            {
                var h = Get(p);
                h.TurnCts?.Cancel();
                return DotCodeJson.EmptyObject;
            }
            case "session.close":
            {
                if (p.GetString("sessionId") is { } id && _sessions.TryRemove(id, out var h))
                {
                    h.TurnCts?.Cancel();
                    await h.Session.DisposeAsync().ConfigureAwait(false);
                    await h.Runtime.DisposeAsync().ConfigureAwait(false);
                    // A worktree the session never changed is cleaned up; one with work in it is kept for the host.
                    if (h.Runtime.Options.Worktree is { } wt && !Worktrees.HasChanges(wt)) Worktrees.Remove(wt.RepoRoot, wt.Name);
                }
                return DotCodeJson.EmptyObject;
            }
            case "session.setModel":
            {
                var h = Get(p);
                try { h.Session.SetModel(p.GetString("model") ?? ""); }
                catch (InvalidOperationException ex) { throw new JsonRpcException(JsonRpcException.InvalidParams, ex.Message); }
                return Obj(w => w.WriteString("model", h.Session.Model.Qualified));
            }
            case "session.setMode":
            {
                var h = Get(p);
                h.Session.SetMode(PermissionModes.Parse(p.GetString("mode")));
                return Obj(w => w.WriteString("mode", h.Session.Mode.ToSetting()));
            }
            case "session.setEffort":
            {
                var h = Get(p);
                h.Session.Effort = Enum.TryParse<ReasoningEffort>(p.GetString("effort"), true, out var e) ? e : ReasoningEffort.Medium;
                return Obj(w => w.WriteString("effort", h.Session.Effort.ToString().ToLowerInvariant()));
            }
            case "session.compact":
            {
                var h = Get(p);
                await h.Session.CompactAsync(p.GetString("instructions"), ct).ConfigureAwait(false);
                return DotCodeJson.EmptyObject;
            }
            case "session.clear":
                Get(p).Session.Clear();
                return DotCodeJson.EmptyObject;
            case "session.messages":
            {
                var h = Get(p);
                return DotCodeJson.Build(w =>
                {
                    w.WriteStartObject();
                    w.WritePropertyName("messages");
                    JsonSerializer.Serialize(w, h.Session.Messages, AbstractionsJsonContext.Default.ListMessage);
                    w.WriteEndObject();
                });
            }
            case "session.info":
            {
                var h = Get(p);
                return DotCodeJson.Build(w => WriteSessionInfo(w, h.Session));
            }
            case "session.list":
            {
                var cwd = p.GetString("cwd") ?? _defaults.Cwd;
                return DotCodeJson.Build(w =>
                {
                    w.WriteStartObject();
                    w.WriteStartArray("sessions");
                    foreach (var s in SessionStore.List(cwd, p.GetInt("limit") ?? 50))
                    {
                        w.WriteStartObject();
                        w.WriteString("id", s.Id);
                        w.WriteString("title", s.Title);
                        w.WriteString("firstPrompt", s.FirstPrompt);
                        w.WriteString("modified", s.Modified.ToString("O"));
                        w.WriteNumber("messageCount", s.MessageCount);
                        w.WriteString("model", s.Model);
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                    w.WriteEndObject();
                });
            }
            case "models.list":
            {
                var options = Clone(_defaults, p);
                await using var runtime = AgentRuntime.Create(options, connectMcp: false);
                var models = await runtime.Router.ListAllModelsAsync(ct).ConfigureAwait(false);
                return DotCodeJson.Build(w =>
                {
                    w.WriteStartObject();
                    w.WriteString("default", runtime.MainModelReference);
                    w.WriteStartArray("providers");
                    foreach (var (name, cfg) in runtime.Router.Providers.Where(x => x.Key != "mock"))
                    {
                        w.WriteStartObject(); w.WriteString("name", name); w.WriteString("type", cfg.Type); w.WriteEndObject();
                    }
                    w.WriteEndArray();
                    w.WriteStartArray("models");
                    foreach (var m in models)
                    {
                        w.WriteStartObject(); w.WriteString("provider", m.ProviderId); w.WriteString("id", m.Id); w.WriteString("qualifiedId", m.QualifiedId); w.WriteEndObject();
                    }
                    w.WriteEndArray();
                    w.WriteEndObject();
                });
            }
            case "tools.list":
            {
                var h = Get(p);
                return DotCodeJson.Build(w =>
                {
                    w.WriteStartObject();
                    w.WriteStartArray("tools");
                    foreach (var t in h.Session.GetTools())
                    {
                        w.WriteStartObject();
                        w.WriteString("name", t.Name);
                        w.WriteString("description", t.Description);
                        w.WritePropertyName("inputSchema");
                        t.InputSchema.WriteTo(w);
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                    w.WriteEndObject();
                });
            }
            default:
                throw new JsonRpcException(JsonRpcException.MethodNotFound, $"Method not found: {method}");
        }
    }

    private static JsonElement Obj(Action<Utf8JsonWriter> body) => DotCodeJson.Build(w => { w.WriteStartObject(); body(w); w.WriteEndObject(); });

    private static RuntimeOptions Clone(RuntimeOptions d, JsonElement p)
    {
        var o = new RuntimeOptions
        {
            Cwd = p.GetString("cwd") is { } cwd ? Path.GetFullPath(cwd) : d.Cwd,
            Model = p.GetString("model") ?? d.Model,
            FallbackModel = p.GetString("fallbackModel") ?? d.FallbackModel,
            PermissionMode = p.GetString("permissionMode") ?? d.PermissionMode,
            DangerouslySkipPermissions = p.GetBool("dangerouslySkipPermissions") ?? d.DangerouslySkipPermissions,
            SystemPrompt = p.GetString("systemPrompt") ?? d.SystemPrompt,
            AppendSystemPrompt = p.GetString("appendSystemPrompt") ?? d.AppendSystemPrompt,
            SettingsPath = d.SettingsPath,
            SettingsJson = p.GetProp("settings") is { } s ? s.GetRawText() : d.SettingsJson,
            MaxTurns = p.GetInt("maxTurns") ?? d.MaxTurns,
            Effort = p.GetString("effort") ?? d.Effort,
            OutputStyle = p.GetString("outputStyle") ?? d.OutputStyle,
            PersistSession = p.GetBool("persistSession") ?? d.PersistSession,
            NoMcp = p.GetBool("noMcp") ?? d.NoMcp,
            StrictMcpConfig = p.GetBool("strictMcpConfig") ?? d.StrictMcpConfig,
        };
        o.AllowedTools.AddRange(d.AllowedTools);
        o.DisallowedTools.AddRange(d.DisallowedTools);
        o.AddDirs.AddRange(d.AddDirs);
        if (p.GetProp("allowedTools") is { ValueKind: JsonValueKind.Array } allowed) o.AllowedTools.AddRange(allowed.EnumerateArray().Select(x => x.GetString() ?? ""));
        if (p.GetProp("disallowedTools") is { ValueKind: JsonValueKind.Array } denied) o.DisallowedTools.AddRange(denied.EnumerateArray().Select(x => x.GetString() ?? ""));
        if (p.GetProp("tools") is { ValueKind: JsonValueKind.Array } builtins && builtins.EnumerateArray().All(x => x.ValueKind == JsonValueKind.String))
            o.Tools = [.. builtins.EnumerateArray().Select(x => x.GetString() ?? "")];
        if (p.GetProp("addDirs") is { ValueKind: JsonValueKind.Array } dirs) o.AddDirs.AddRange(dirs.EnumerateArray().Select(x => x.GetString() ?? ""));
        if (p.GetProp("mcpServers") is { ValueKind: JsonValueKind.Object } mcp) o.McpConfigs.Add("{\"mcpServers\":" + mcp.GetRawText() + "}");
        return o;
    }

    private JsonElement CreateSession(JsonElement p, bool resume)
    {
        var options = Clone(_defaults, p);
        // "worktree": true | "name" — run the session in a git worktree (.dotcode/worktrees/<name>).
        if (p.GetProp("worktree") is { } wtParam && wtParam.ValueKind is JsonValueKind.True or JsonValueKind.String)
        {
            try
            {
                var wt = Worktrees.Create(options.Cwd, wtParam.ValueKind == JsonValueKind.String ? wtParam.GetString() : null);
                options.Cwd = wt.Path;
                options.Worktree = wt;
            }
            catch (InvalidOperationException ex) { throw new JsonRpcException(JsonRpcException.InvalidParams, ex.Message); }
        }
        var runtime = AgentRuntime.Create(options);
        AgentSession session;
        try
        {
            if (resume)
            {
                var id = p.GetString("sessionId") ?? throw new JsonRpcException(JsonRpcException.InvalidParams, "sessionId is required");
                var path = SessionStore.FindPath(runtime.Cwd, id) ?? throw new JsonRpcException(JsonRpcException.SessionNotFound, $"No saved session {id}");
                session = runtime.ResumeSession(path, p.GetBool("fork") == true);
            }
            else session = runtime.CreateSession();
        }
        catch (InvalidOperationException ex)
        {
            throw new JsonRpcException(JsonRpcException.InvalidParams, ex.Message);
        }

        var sessionId = session.Id;
        session.Sink = new DelegateEventSink(e => _ = _rpc.NotifyAsync("session.event", w =>
        {
            w.WriteStartObject();
            w.WriteString("sessionId", sessionId);
            w.WritePropertyName("event");
            JsonSerializer.Serialize(w, e, AbstractionsJsonContext.Default.AgentEvent);
            w.WriteEndObject();
        }));
        session.Interaction = new RemoteInteraction(_rpc, sessionId, _clientHandlesPermissions, _clientHandlesQuestions);

        // Host-implemented tools (SDK custom tools) are proxied back to the client.
        if (p.GetProp("hostTools") is { ValueKind: JsonValueKind.Array } hostTools)
        {
            foreach (var t in hostTools.EnumerateArray())
            {
                var name = t.GetString("name") ?? continue_();
                session.ExtraTools.Add(new HostTool(name, t.GetString("description") ?? name,
                    t.GetProp("inputSchema")?.Clone() ?? DotCodeJson.Parse("{\"type\":\"object\"}"), t.GetBool("readOnly") == true,
                    async (toolUseId, input, ct) =>
                    {
                        var result = await _rpc.RequestAsync("tool.call", w =>
                        {
                            w.WriteStartObject();
                            w.WriteString("sessionId", sessionId);
                            w.WriteString("toolUseId", toolUseId);
                            w.WriteString("name", name);
                            w.WritePropertyName("input");
                            input.WriteTo(w);
                            w.WriteEndObject();
                        }, ct).ConfigureAwait(false);
                        var content = new List<ContentPart>();
                        var c = result.GetProp("content");
                        if (c is { ValueKind: JsonValueKind.String } s) content.Add(new TextPart(s.GetString()!));
                        else if (c is { ValueKind: JsonValueKind.Array } arr)
                            foreach (var part in arr.EnumerateArray())
                                content.Add(part.GetString("type") == "image"
                                    ? new ImagePart(part.GetString("data") ?? "", part.GetString("mediaType") ?? "image/png")
                                    : new TextPart(part.GetString("text") ?? part.GetRawText()));
                        else if (result.ValueKind == JsonValueKind.String) content.Add(new TextPart(result.GetString()!));
                        else content.Add(new TextPart(result.GetRawText()));
                        return (content, result.GetBool("isError") == true);
                    }));
            }
        }

        _sessions[sessionId] = new Hosted(runtime, session);
        return DotCodeJson.Build(w => WriteSessionInfo(w, session));

        static string continue_() => throw new JsonRpcException(JsonRpcException.InvalidParams, "host tool requires a name");
    }

    private static void WriteSessionInfo(Utf8JsonWriter w, AgentSession session)
    {
        w.WriteStartObject();
        w.WriteString("sessionId", session.Id);
        w.WriteString("model", session.Model.Qualified);
        w.WriteString("cwd", session.Cwd);
        w.WriteString("permissionMode", session.Mode.ToSetting());
        w.WriteNumber("messageCount", session.Messages.Count);
        w.WriteNumber("totalCostUsd", session.TotalCostUsd);
        if (session.Store?.FilePath is { } path) w.WriteString("transcriptPath", path);
        if (session.Runtime.Options.Worktree is { } wt)
        {
            w.WriteStartObject("worktree");
            w.WriteString("name", wt.Name);
            w.WriteString("path", wt.Path);
            w.WriteString("branch", wt.Branch);
            w.WriteEndObject();
        }
        w.WriteStartArray("tools");
        foreach (var t in session.GetTools()) w.WriteStringValue(t.Name);
        w.WriteEndArray();
        w.WriteEndObject();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var h in _sessions.Values)
        {
            h.TurnCts?.Cancel();
            await h.Session.DisposeAsync().ConfigureAwait(false);
            await h.Runtime.DisposeAsync().ConfigureAwait(false);
        }
        await _transport.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>Routes engine interaction callbacks to the connected client. Without a client permission handler the
/// server is deny-by-default (only rules/modes can allow tools).</summary>
internal sealed class RemoteInteraction(JsonRpcConnection rpc, string sessionId, bool permissions, bool questions) : IInteractionHandler
{
    public async ValueTask<PermissionDecision> RequestPermissionAsync(PermissionRequest request, CancellationToken ct)
    {
        if (!permissions) return await NonInteractiveHandler.Instance.RequestPermissionAsync(request, ct).ConfigureAwait(false);
        try
        {
            var result = await rpc.RequestAsync("permission.request", w =>
            {
                w.WriteStartObject();
                w.WriteString("sessionId", sessionId);
                w.WritePropertyName("request");
                JsonSerializer.Serialize(w, request, AbstractionsJsonContext.Default.PermissionRequest);
                w.WriteEndObject();
            }, ct).ConfigureAwait(false);
            var kind = (result.GetString("decision") ?? result.GetString("behavior") ?? "deny").ToLowerInvariant() switch
            {
                "allow" or "allow_once" or "allowonce" => PermissionDecisionKind.AllowOnce,
                "allow_always" or "allowalways" or "always" => PermissionDecisionKind.AllowAlways,
                "allow_session" or "allowsession" or "session" => PermissionDecisionKind.AllowSession,
                _ => PermissionDecisionKind.Deny,
            };
            return new PermissionDecision(kind, result.GetString("feedback") ?? result.GetString("message"), result.GetString("rule") ?? request.SuggestedRule, result.GetProp("updatedInput")?.Clone());
        }
        catch (JsonRpcException ex) { return PermissionDecision.Deny($"Permission handler error: {ex.Message}"); }
    }

    public async ValueTask<IReadOnlyList<UserQuestionAnswer>?> AskQuestionsAsync(IReadOnlyList<UserQuestion> qs, CancellationToken ct)
    {
        if (!questions) return null;
        var result = await rpc.RequestAsync("user.question", w =>
        {
            w.WriteStartObject();
            w.WriteString("sessionId", sessionId);
            w.WritePropertyName("questions");
            JsonSerializer.Serialize(w, qs.ToList(), AbstractionsJsonContext.Default.ListUserQuestion);
            w.WriteEndObject();
        }, ct).ConfigureAwait(false);
        return result.GetProp("answers")?.Deserialize(AbstractionsJsonContext.Default.ListUserQuestionAnswer);
    }

    public async ValueTask<PlanDecision> ReviewPlanAsync(string plan, CancellationToken ct)
    {
        if (!permissions) return new PlanDecision(PlanApproval.Approve);
        var result = await rpc.RequestAsync("plan.review", w =>
        {
            w.WriteStartObject();
            w.WriteString("sessionId", sessionId);
            w.WriteString("plan", plan);
            w.WriteEndObject();
        }, ct).ConfigureAwait(false);
        return (result.GetString("approval") ?? "approve") switch
        {
            "approve_accept_edits" or "acceptEdits" => new PlanDecision(PlanApproval.ApproveAcceptEdits),
            "reject" => new PlanDecision(PlanApproval.Reject, result.GetString("feedback")),
            _ => new PlanDecision(PlanApproval.Approve),
        };
    }
}
