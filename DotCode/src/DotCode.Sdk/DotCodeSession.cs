using System.Runtime.CompilerServices;
using System.Threading.Channels;
using DotCode.Abstractions;
using DotCode.Engine.Permissions;

namespace DotCode.Sdk;

/// <summary>The outcome of one turn.</summary>
public sealed record SessionResult(string Result, string StopReason, bool IsError, string? Error, Usage Usage, decimal CostUsd, decimal TotalCostUsd, long DurationMs, int ModelCalls);

/// <summary>One conversation with the agent — the same API in Spawn, Connect and InProcess modes. Dispose it to
/// disconnect (the transcript stays on disk for <see cref="DotCodeClient.ResumeSessionAsync"/>).</summary>
public abstract class DotCodeSession : IAsyncDisposable
{
    private readonly Lock _gate = new();
    private readonly List<Action<AgentEvent>> _handlers = [];

    private protected DotCodeSession(SessionConfig config) => Config = config;

    private protected SessionConfig Config { get; }

    public abstract string SessionId { get; }
    public abstract string Model { get; }

    /// <summary>Subscribes to every event; dispose the result to unsubscribe.</summary>
    public IDisposable On(Action<AgentEvent> handler)
    {
        lock (_gate) _handlers.Add(handler);
        return new Unsubscriber(() => { lock (_gate) _handlers.Remove(handler); });
    }

    /// <summary>Subscribes to one event type, e.g. <c>session.On&lt;ToolCompletedEvent&gt;(e =&gt; ...)</c>.</summary>
    public IDisposable On<TEvent>(Action<TEvent> handler) where TEvent : AgentEvent =>
        On(e => { if (e is TEvent t) handler(t); });

    private protected void Dispatch(AgentEvent e)
    {
        Config.OnEvent?.Invoke(e);
        Action<AgentEvent>[] handlers;
        lock (_gate) handlers = [.. _handlers];
        foreach (var h in handlers)
        {
            try { h(e); }
            catch (Exception) { /* a failing listener must not break the session */ }
        }
    }

    /// <summary>Starts a turn and returns once it is dispatched; follow it with <see cref="On{TEvent}"/>
    /// (<see cref="TurnCompletedEvent"/> ends it). Failures are delivered as an <see cref="ErrorEvent"/>.</summary>
    public Task SendAsync(MessageOptions message, CancellationToken ct = default)
    {
        _ = Task.Run(async () =>
        {
            try { await RunTurnAsync(message, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { Dispatch(new ErrorEvent("send_failed", ex.Message, false) { SessionId = SessionId }); }
        }, CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <summary>Runs a turn to completion and returns its result. With <paramref name="timeout"/> the turn is
    /// aborted when it takes longer (<see cref="TimeoutException"/>).</summary>
    public async Task<SessionResult> SendAndWaitAsync(MessageOptions message, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        if (timeout is not { } limit) return await RunTurnAsync(message, ct).ConfigureAwait(false);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var run = RunTurnAsync(message, cts.Token);
        if (await Task.WhenAny(run, Task.Delay(limit, ct)).ConfigureAwait(false) == run) return await run.ConfigureAwait(false);
        await AbortAsync().ConfigureAwait(false);
        await cts.CancelAsync().ConfigureAwait(false);
        throw new TimeoutException($"The turn did not complete within {limit}.");
    }

    /// <summary>Runs a turn, yielding events as they happen; the last one is <see cref="TurnCompletedEvent"/>.</summary>
    public async IAsyncEnumerable<AgentEvent> StreamAsync(MessageOptions message, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var channel = Channel.CreateUnbounded<AgentEvent>();
        using var subscription = On(e => channel.Writer.TryWrite(e));
        var run = RunTurnAsync(message, ct);
        _ = run.ContinueWith(_ => channel.Writer.TryComplete(), TaskScheduler.Default);
        await foreach (var e in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            yield return e;
            if (e is TurnCompletedEvent { ParentToolUseId: null }) break;
        }
        await run.ConfigureAwait(false);
    }

    private protected abstract Task<SessionResult> RunTurnAsync(MessageOptions message, CancellationToken ct);

    /// <summary>Cancels the running turn.</summary>
    public abstract Task AbortAsync();
    public abstract Task SetModelAsync(string model, CancellationToken ct = default);
    public abstract Task SetPermissionModeAsync(PermissionMode mode, CancellationToken ct = default);
    public abstract Task SetReasoningEffortAsync(ReasoningEffort effort, CancellationToken ct = default);
    /// <summary>Summarizes the conversation to free context.</summary>
    public abstract Task CompactAsync(string? instructions = null, CancellationToken ct = default);
    /// <summary>The conversation so far.</summary>
    public abstract Task<IReadOnlyList<Message>> GetMessagesAsync(CancellationToken ct = default);
    /// <summary>Closes the session.</summary>
    public abstract ValueTask DisposeAsync();

    // ------------------------------------------------------------------ shared callback logic

    private protected Invocation Invocation => new(SessionId);

    private protected async Task<PermissionDecision> DecidePermissionAsync(PermissionRequest request, CancellationToken ct) =>
        Config.OnPermissionRequest is { } h
            ? await h(request, Invocation, ct).ConfigureAwait(false)
            : PermissionDecision.Reject("No permission handler registered in the SDK host (deny by default).");

    private protected async Task<IReadOnlyList<UserQuestionAnswer>?> AnswerQuestionsAsync(IReadOnlyList<UserQuestion> questions, CancellationToken ct) =>
        Config.OnUserInputRequest is { } h ? await h(questions, Invocation, ct).ConfigureAwait(false) : null;

    private protected async Task<ExitPlanModeResult> ReviewPlanAsync(string plan, CancellationToken ct) =>
        Config.OnExitPlanMode is { } h ? await h(plan, Invocation, ct).ConfigureAwait(false) : ExitPlanModeResult.Approve();

    private protected Task<ToolResult> CallToolAsync(string toolUseId, string name, System.Text.Json.JsonElement input, CancellationToken ct)
    {
        var tool = Config.Tools.FirstOrDefault(t => t.Name == name);
        if (tool is null) return Task.FromResult(ToolResult.Failure($"Unknown host tool {name}"));
        return tool.InvokeAsync(new ToolInvocation(SessionId, toolUseId, name, input), ct);
    }

    private sealed class Unsubscriber(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;
        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
