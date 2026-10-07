using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Marbots.Abstractions;
using Microsoft.Extensions.Logging;

namespace Marbots.Runtime;

/// <summary>Registered remote hosts and one-time enrollment tokens. Secrets and tokens are stored as SHA-256 hashes.</summary>
public sealed class HostRegistry(IDocumentStore<HostRecord> hosts, IDocumentStore<HostEnrollment> enrollments)
{
    public Task<IReadOnlyList<HostRecord>> ListAsync(CancellationToken ct = default) => hosts.ListAsync(ct);
    public Task<HostRecord?> GetAsync(string id, CancellationToken ct = default) => hosts.GetAsync(id, ct);
    public Task SaveAsync(HostRecord host, CancellationToken ct = default) => hosts.UpsertAsync(host, ct);
    public Task<bool> RemoveAsync(string id, CancellationToken ct = default) => hosts.DeleteAsync(id, ct);

    public static string Hash(string secret) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    public static bool Matches(string secret, string hash) =>
        hash.Length > 0 && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Hash(secret)), Encoding.ASCII.GetBytes(hash));

    private static string NewSecret(int bytes) => Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Creates a one-time enrollment token (returned once; only its hash is kept).</summary>
    public async Task<(string Token, DateTimeOffset ExpiresAt)> CreateEnrollmentAsync(string name, TimeSpan validFor, string by, CancellationToken ct = default)
    {
        var token = "mbe_" + NewSecret(24);
        var expires = DateTimeOffset.UtcNow + validFor;
        await enrollments.UpsertAsync(new HostEnrollment { Id = Ids.New("enr"), TokenHash = Hash(token), Name = name, ExpiresAt = expires, CreatedBy = by }, ct);
        return (token, expires);
    }

    /// <summary>Redeems a token: creates the host and returns its id and secret. The token can not be used again.</summary>
    public async Task<HostEnrollmentResult?> EnrollAsync(HostEnrollmentRequest request, string installedVia, CancellationToken ct = default)
    {
        var all = await enrollments.ListAsync(ct);
        foreach (var expired in all.Where(e => e.ExpiresAt < DateTimeOffset.UtcNow)) await enrollments.DeleteAsync(expired.Id, ct);
        var match = all.FirstOrDefault(e => e.ExpiresAt >= DateTimeOffset.UtcNow && Matches(request.Token, e.TokenHash));
        if (match is null) return null;
        await enrollments.DeleteAsync(match.Id, ct);
        var name = string.IsNullOrWhiteSpace(match.Name) ? request.Hello.Name : match.Name;
        var id = "host-" + (Ids.Slug(name) is { Length: > 0 } slug ? slug : "remote") + "-" + Guid.NewGuid().ToString("N")[..4];
        var secret = "mbh_" + NewSecret(32);
        await hosts.UpsertAsync(new HostRecord
        {
            Id = id, Name = name, SecretHash = Hash(secret), LastHello = request.Hello, InstalledVia = installedVia,
        }, ct);
        return new HostEnrollmentResult(id, secret, typeof(HostRegistry).Assembly.GetName().Version?.ToString(3) ?? "0.1.0");
    }

    public async Task<HostRecord?> AuthenticateAsync(string hostId, string secret, CancellationToken ct = default)
    {
        var host = await hosts.GetAsync(hostId, ct);
        return host is { Disabled: false } && Matches(secret, host.SecretHash) ? host : null;
    }
}

/// <summary>
/// Live connections to agent hosts. Calls are correlated by request id; if a host drops mid-call the call waits for it
/// to reconnect and re-sends the same request id, and the host answers from its result cache instead of running the
/// tool twice (idempotent).
/// </summary>
public sealed class HostConnectionManager(HostRegistry registry, IEventBus bus, ILogger<HostConnectionManager> log)
{
    public static readonly TimeSpan ReconnectGrace = TimeSpan.FromSeconds(60);
    private const int MaxFrameBytes = 64 * 1024 * 1024;
    private readonly ConcurrentDictionary<string, Session> _sessions = new();

    private sealed class Session(string hostId, WebSocket socket)
    {
        public string HostId { get; } = hostId;
        public WebSocket Socket { get; } = socket;
        public SemaphoreSlim SendLock { get; } = new(1, 1);
        public ConcurrentDictionary<string, TaskCompletionSource<HostFrame>> Pending { get; } = new();
        public HostHello? Hello { get; set; }
        public HostMetrics? Metrics { get; set; }
        public TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public bool IsOnline(string hostId) => _sessions.TryGetValue(hostId, out var s) && s.Socket.State == WebSocketState.Open;
    public HostHello? HelloOf(string hostId) => _sessions.TryGetValue(hostId, out var s) ? s.Hello : null;
    public HostMetrics? MetricsOf(string hostId) => _sessions.TryGetValue(hostId, out var s) ? s.Metrics : null;
    public IReadOnlyCollection<string> OnlineHosts => _sessions.Keys.Where(IsOnline).ToList();

    /// <summary>Serves one authenticated host connection until it closes.</summary>
    public async Task RunAsync(HostRecord host, WebSocket socket, CancellationToken ct)
    {
        var session = new Session(host.Id, socket);
        if (_sessions.TryRemove(host.Id, out var old))
        {
            old.Closed.TrySetResult();
            try { await old.Socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "replaced by a new connection", CancellationToken.None); } catch (WebSocketException) { }
        }
        _sessions[host.Id] = session;
        try
        {
            await SendAsync(session, new HostFrame { Type = HostProtocol.Frames.Welcome }, ct);
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var frame = await ReceiveAsync(socket, ct);
                if (frame is null) break;
                await HandleAsync(host, session, frame, ct);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or JsonException or IOException)
        {
            log.LogInformation("Host {Host} disconnected: {Reason}", host.Id, ex.Message);
        }
        finally
        {
            _sessions.TryRemove(new KeyValuePair<string, Session>(host.Id, session));
            session.Closed.TrySetResult();
            var latest = await registry.GetAsync(host.Id, CancellationToken.None) ?? host;
            latest.LastSeen = DateTimeOffset.UtcNow;
            await registry.SaveAsync(latest, CancellationToken.None);
            await bus.PublishAsync(new AgentEvent { Type = EventTypes.HostDisconnected, Message = $"{host.Name} went offline", Data = host.Id }, CancellationToken.None);
        }
    }

    private async Task HandleAsync(HostRecord host, Session session, HostFrame frame, CancellationToken ct)
    {
        switch (frame.Type)
        {
            case HostProtocol.Frames.Hello when frame.Hello is { } hello:
                session.Hello = hello;
                var record = await registry.GetAsync(host.Id, ct) ?? host;
                record.LastHello = hello;
                record.LastSeen = DateTimeOffset.UtcNow;
                await registry.SaveAsync(record, ct);
                await bus.PublishAsync(new AgentEvent { Type = EventTypes.HostConnected, Message = $"{record.Name} is online ({hello.Os}; {string.Join(", ", hello.Capabilities)})", Data = host.Id }, ct);
                break;
            case HostProtocol.Frames.Heartbeat:
                session.Metrics = frame.Metrics;
                break;
            default:
                if (frame.RequestId is not null && session.Pending.TryRemove(frame.RequestId, out var waiter)) waiter.TrySetResult(frame);
                break;
        }
    }

    /// <summary>Runs a tool on the host. Survives a short disconnect (re-sends the same request id).</summary>
    public async Task<FunctionResult> InvokeAsync(string hostId, HostInvoke invoke, TimeSpan timeout, CancellationToken ct)
    {
        var requestId = $"{invoke.TaskId}:{invoke.CallId}";
        var response = await RequestAsync(hostId, new HostFrame { Type = HostProtocol.Frames.Invoke, RequestId = requestId, Invoke = invoke }, timeout, ct);
        return response switch
        {
            { Result: { } r } => r,
            { Error: { } e } => FunctionResult.Fail(e),
            _ => FunctionResult.Fail("The host returned no result."),
        };
    }

    public async Task<IReadOnlyList<HostFileEntry>> ListFilesAsync(string hostId, string workspace, CancellationToken ct)
    {
        var r = await RequestAsync(hostId, new HostFrame { Type = HostProtocol.Frames.ListFiles, RequestId = Ids.New("req"), Workspace = workspace }, TimeSpan.FromSeconds(30), ct);
        return r.Error is { } e ? throw new InvalidOperationException(e) : r.Files ?? [];
    }

    public async Task<byte[]?> ReadFileAsync(string hostId, string workspace, string path, CancellationToken ct)
    {
        var r = await RequestAsync(hostId, new HostFrame { Type = HostProtocol.Frames.ReadFile, RequestId = Ids.New("req"), Workspace = workspace, Path = path }, TimeSpan.FromMinutes(2), ct);
        return r.Data is { } d ? Convert.FromBase64String(d) : null;
    }

    /// <summary>Writes files into a workspace on the host (e.g. a skill's scripts).</summary>
    public async Task PutFilesAsync(string hostId, string workspace, List<HostFilePayload> files, CancellationToken ct)
    {
        var r = await RequestAsync(hostId, new HostFrame { Type = HostProtocol.Frames.PutFiles, RequestId = Ids.New("req"), Workspace = workspace, Upload = files }, TimeSpan.FromMinutes(2), ct);
        if (r.Error is { } e) throw new InvalidOperationException(e);
    }

    private async Task<HostFrame> RequestAsync(string hostId, HostFrame request, TimeSpan timeout, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout + ReconnectGrace);
        while (true)
        {
            var session = await WaitForSessionAsync(hostId, deadline.Token)
                ?? throw new HostOfflineException(hostId);
            var waiter = new TaskCompletionSource<HostFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
            session.Pending[request.RequestId!] = waiter;
            try
            {
                await SendAsync(session, request, deadline.Token);
                var done = await Task.WhenAny(waiter.Task, session.Closed.Task, Task.Delay(Timeout.Infinite, deadline.Token));
                if (done == waiter.Task) return await waiter.Task;
                if (done == session.Closed.Task) continue; // dropped: wait for the host to come back and re-send
                throw new OperationCanceledException(deadline.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return new HostFrame { Error = $"The host did not answer within {timeout.TotalSeconds:0}s." };
            }
            catch (OperationCanceledException)
            {
                if (IsOnline(hostId) && _sessions.TryGetValue(hostId, out var s))
                    try { await SendAsync(s, new HostFrame { Type = HostProtocol.Frames.Cancel, RequestId = request.RequestId }, CancellationToken.None); } catch (WebSocketException) { }
                throw;
            }
            catch (WebSocketException)
            {
                await Task.Delay(500, deadline.Token);
            }
            finally
            {
                session.Pending.TryRemove(request.RequestId!, out _);
            }
        }
    }

    private async Task<Session?> WaitForSessionAsync(string hostId, CancellationToken ct)
    {
        var until = DateTimeOffset.UtcNow + ReconnectGrace;
        while (DateTimeOffset.UtcNow < until)
        {
            if (_sessions.TryGetValue(hostId, out var s) && s.Socket.State == WebSocketState.Open) return s;
            try { await Task.Delay(500, ct); } catch (OperationCanceledException) { return null; }
        }
        return null;
    }

    private static async Task SendAsync(Session session, HostFrame frame, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(frame, MarbotsJsonContext.Default.HostFrame);
        await session.SendLock.WaitAsync(ct);
        try { await session.Socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct); }
        finally { session.SendLock.Release(); }
    }

    /// <summary>Reads one whole frame (fragments joined). Null when the peer closes.</summary>
    public static async Task<HostFrame?> ReceiveAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();
        while (true)
        {
            var r = await socket.ReceiveAsync(buffer, ct);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buffer, 0, r.Count);
            if (ms.Length > MaxFrameBytes) throw new IOException("Frame too large.");
            if (r.EndOfMessage) break;
        }
        return JsonSerializer.Deserialize(ms.GetBuffer().AsSpan(0, (int)ms.Length), MarbotsJsonContext.Default.HostFrame);
    }
}

public sealed class HostOfflineException(string hostId) : InvalidOperationException($"Host '{hostId}' is offline.")
{
    public string HostId { get; } = hostId;
}

/// <summary>A kernel function whose execution happens on the bot's host (same descriptor, so policy is unchanged).</summary>
public sealed class RemoteFunction(IKernelFunction inner, string hostId, string hostName, HostConnectionManager hosts) : IKernelFunction
{
    public FunctionDescriptor Descriptor => inner.Descriptor;
    public string HostId => hostId;

    public async ValueTask<FunctionResult> InvokeAsync(FunctionCall call, FunctionExecutionContext context, CancellationToken cancellationToken)
    {
        var invoke = new HostInvoke
        {
            Function = Descriptor.Name, CallId = call.Id, Arguments = call.Arguments.ValueKind == JsonValueKind.Undefined ? "{}" : call.Arguments.GetRawText(),
            BotId = context.Bot.Id, BotName = context.Bot.Name, TaskId = context.TaskId, ThreadId = context.ThreadId,
            Workspace = Ids.Slug(context.ThreadId) is { Length: > 0 } slug ? slug : "default", Container = context.Bot.Container,
        };
        try
        {
            return await hosts.InvokeAsync(hostId, invoke, TimeSpan.FromSeconds(Descriptor.TimeoutSeconds + 30), cancellationToken);
        }
        catch (HostOfflineException)
        {
            return FunctionResult.Fail($"Host '{hostName}' is offline, so {Descriptor.Name} could not run there. Tell the user; an operator can restart the host or move the bot.");
        }
    }
}

/// <summary>load_skill for a bot on a remote host: also copies the skill's files into the host workspace.</summary>
public sealed class RemoteSkillLoader(IKernelFunction inner, SkillRegistry skills, string hostId, HostConnectionManager hosts) : IKernelFunction
{
    public FunctionDescriptor Descriptor => inner.Descriptor;

    public async ValueTask<FunctionResult> InvokeAsync(FunctionCall call, FunctionExecutionContext context, CancellationToken cancellationToken)
    {
        var result = await inner.InvokeAsync(call, context, cancellationToken);
        if (!result.Success || skills.ForBot(context.Bot).FirstOrDefault(s => s.Name.Equals(call.GetString("name"), StringComparison.OrdinalIgnoreCase)) is not { } skill) return result;
        var files = SkillRegistry.Materialize(skill, null).Select(f => new HostFilePayload(f.Relative, Convert.ToBase64String(File.ReadAllBytes(f.Full)))).ToList();
        if (files.Count == 0) return result;
        try
        {
            await hosts.PutFilesAsync(hostId, Ids.Slug(context.ThreadId), files, cancellationToken);
            return result with { Content = result.Content + $"\n\nThe skill's files are copied to .skills/{Ids.Slug(skill.Name)}/ in your workspace; run its scripts from there." };
        }
        catch (Exception ex) when (ex is HostOfflineException or InvalidOperationException)
        {
            return result with { Content = result.Content + $"\n\n(Could not copy the skill's files to the host: {ex.Message})" };
        }
    }
}

/// <summary>
/// Chooses a host for bots with HostRef "auto": keeps a thread on the host that has its workspace (data locality),
/// otherwise scores online hosts on capabilities, load and free memory.
/// </summary>
public sealed class PlacementService(HostConnectionManager connections, HostRegistry registry, IDocumentStore<ThreadHost> affinity)
{
    public async Task<string> ResolveAsync(BotDefinition bot, string threadId, CancellationToken ct)
    {
        if (!string.Equals(bot.HostRef, WellKnown.AutoHost, StringComparison.OrdinalIgnoreCase)) return bot.HostRef;
        if (await affinity.GetAsync(threadId, ct) is { } pinned && (pinned.HostId == WellKnown.LocalHostId || connections.IsOnline(pinned.HostId)))
            return pinned.HostId;
        var candidates = new List<HostCandidate> { new(WellKnown.LocalHostId, LocalCapabilities(), null, true, LocalGpus) };
        foreach (var h in await registry.ListAsync(ct))
            if (!h.Disabled && connections.IsOnline(h.Id))
            {
                var hello = connections.HelloOf(h.Id) ?? h.LastHello;
                candidates.Add(new(h.Id, hello?.Capabilities ?? [], connections.MetricsOf(h.Id), false, hello?.Gpus ?? []));
            }
        var best = Choose(Required(bot), candidates, bot.Container is not null);
        var record = await affinity.GetAsync(threadId, ct) ?? new ThreadHost { Id = threadId };
        record.HostId = best;
        await affinity.UpsertAsync(record, ct);
        return best;
    }

    /// <summary>Remote hosts holding files of a thread's workspace.</summary>
    public async Task<IReadOnlyList<string>> HostsOfThreadAsync(string threadId, CancellationToken ct) =>
        (await affinity.GetAsync(threadId, ct))?.Used ?? [];

    public async Task MarkUsedAsync(string threadId, string hostId, CancellationToken ct)
    {
        var record = await affinity.GetAsync(threadId, ct) ?? new ThreadHost { Id = threadId };
        if (record.Used.Contains(hostId)) return;
        record.Used.Add(hostId);
        await affinity.UpsertAsync(record, ct);
    }

    public sealed record HostCandidate(string Id, IReadOnlyCollection<string> Capabilities, HostMetrics? Metrics, bool IsLocal, IReadOnlyList<GpuInfo>? Gpus = null);

    public static IReadOnlyList<string> Required(BotDefinition bot)
    {
        var req = new List<string>();
        if (bot.KernelFunctions.Contains("shell", StringComparer.OrdinalIgnoreCase)) req.Add("shell");
        if (bot.KernelFunctions.Contains("desktop", StringComparer.OrdinalIgnoreCase)) req.Add("desktop");
        if (bot.Container is not null) req.Add("docker");
        foreach (var r in bot.Requires)
            if (!string.IsNullOrWhiteSpace(r) && !req.Contains(r.Trim(), StringComparer.OrdinalIgnoreCase)) req.Add(r.Trim().ToLowerInvariant());
        return req;
    }

    /// <summary>Does the candidate meet one requirement? "gpu:16" means at least 16 GB on a single GPU.</summary>
    public static bool Meets(HostCandidate c, string requirement)
    {
        if (requirement.StartsWith("gpu:", StringComparison.OrdinalIgnoreCase))
            return double.TryParse(requirement[4..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var gb)
                && (c.Gpus ?? []).Any(g => g.MemoryMb >= gb * 1024);
        return c.Capabilities.Contains(requirement, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Scoring (pure): hosts missing a required capability are skipped; then lower CPU, more free memory and fewer running calls win; remote hosts get a small bonus so work spreads off the control plane.</summary>
    public static string Choose(IReadOnlyList<string> required, IReadOnlyList<HostCandidate> candidates, bool needsContainer)
    {
        double Score(HostCandidate c)
        {
            if (required.Any(r => !Meets(c, r))) return double.MinValue;
            var m = c.Metrics;
            var score = 100.0;
            if (m is not null)
            {
                score -= m.CpuPercent * 0.6;
                score += Math.Min(m.FreeMemoryMb, 16_000) / 400.0;
                score -= m.RunningCalls * 8;
            }
            // GPU work: prefer idle GPUs with the most free memory (the largest GPU when there are no live metrics).
            if (required.Any(r => r.StartsWith("gpu", StringComparison.OrdinalIgnoreCase) || r is "cuda" or "rocm" or "metal"))
            {
                var free = m?.FreeGpuMemoryMb ?? (c.Gpus is { Count: > 0 } g ? g.Max(x => x.MemoryMb - (x.UsedMemoryMb ?? 0)) : 0);
                score += Math.Min(free, 96_000) / 1000.0;
                score -= (m?.GpuPercent ?? 0) * 0.5;
            }
            if (!c.IsLocal) score += 5;
            return score;
        }
        var best = candidates.Select(c => (c.Id, Score: Score(c))).Where(x => x.Score > double.MinValue).OrderByDescending(x => x.Score).FirstOrDefault();
        return best.Id ?? WellKnown.LocalHostId;
    }

    private static readonly Lazy<List<GpuInfo>> _localGpus = new(Marbots.Kernel.GpuDetector.Detect);

    /// <summary>The control plane's own GPUs (detected once).</summary>
    public static List<GpuInfo> LocalGpus => _localGpus.Value;

    public static List<string> LocalCapabilities()
    {
        var caps = new List<string> { "shell" };
        if (OperatingSystem.IsWindows()) caps.Add("desktop");
        caps.AddRange(Marbots.Kernel.GpuDetector.Capabilities(LocalGpus));
        return caps;
    }
}
