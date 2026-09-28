using System.Runtime.CompilerServices;
using System.Threading.Channels;
using DotCode.Abstractions;
using DotCode.Engine.Agent;
using DotCode.Engine.Permissions;
using DotCode.Protocol;

namespace DotCode.Sdk;

/// <summary>Runs the DotCode engine inside the host process (no child process, no serialization).</summary>
internal sealed class InProcessSession : IDotCodeSession, IInteractionHandler
{
    private readonly AgentRuntime _runtime;
    private readonly AgentSession _session;
    private readonly SessionOptions _options;
    private CancellationTokenSource? _turn;
    private Channel<AgentEvent>? _stream;

    public string Id => _session.Id;
    public string Model => _session.Model.Qualified;
    public event Action<AgentEvent>? EventReceived;

    private InProcessSession(AgentRuntime runtime, AgentSession session, SessionOptions options)
    {
        _runtime = runtime;
        _session = session;
        _options = options;
        _session.Sink = new DelegateEventSink(e =>
        {
            EventReceived?.Invoke(e);
            _options.OnEvent?.Invoke(e);
            _stream?.Writer.TryWrite(e);
        });
        _session.Interaction = this;
        foreach (var t in options.Tools)
            _session.ExtraTools.Add(new HostTool(t.Name, t.Description, t.InputSchema, t.ReadOnly, async (_, input, ct) =>
            {
                try { return ([new TextPart(await t.Handler(input, ct).ConfigureAwait(false))], false); }
                catch (Exception ex) when (ex is not OperationCanceledException) { return ([new TextPart("Error: " + ex.Message)], true); }
            }));
    }

    public static InProcessSession Create(SessionOptions o, string? defaultCwd)
    {
        var options = new RuntimeOptions
        {
            Cwd = o.Cwd ?? defaultCwd ?? Environment.CurrentDirectory,
            Model = o.Model,
            FallbackModel = o.FallbackModel,
            PermissionMode = o.PermissionMode,
            SystemPrompt = o.SystemPrompt,
            AppendSystemPrompt = o.AppendSystemPrompt,
            SettingsJson = o.SettingsJson,
            MaxTurns = o.MaxTurns,
            Effort = o.Effort,
            PersistSession = o.PersistSession,
            NoMcp = o.NoMcp,
            Tools = o.BuiltinTools?.ToList(),
        };
        options.AllowedTools.AddRange(o.AllowedTools);
        options.DisallowedTools.AddRange(o.DisallowedTools);
        var runtime = AgentRuntime.Create(options);
        return new InProcessSession(runtime, runtime.CreateSession(), o);
    }

    public async Task<SessionResult> SendAsync(string prompt, CancellationToken ct = default)
    {
        _turn = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            var expanded = await CommandExpander.ExpandAsync(_session, prompt, _turn.Token).ConfigureAwait(false);
            var r = await _session.RunTurnAsync(expanded.Prompt, null, _turn.Token).ConfigureAwait(false);
            return new SessionResult(r.Text, r.StopReason.ToString(), r.IsError, r.Error, r.Usage, r.CostUsd, _session.TotalCostUsd, (long)r.Duration.TotalMilliseconds, r.ModelCalls);
        }
        finally
        {
            _turn.Dispose();
            _turn = null;
        }
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

    public Task AbortAsync() { _turn?.Cancel(); return Task.CompletedTask; }
    public Task SetModelAsync(string model, CancellationToken ct = default) { _session.SetModel(model); return Task.CompletedTask; }
    public Task SetPermissionModeAsync(string mode, CancellationToken ct = default) { _session.SetMode(PermissionModes.Parse(mode)); return Task.CompletedTask; }
    public Task CompactAsync(string? instructions = null, CancellationToken ct = default) => _session.CompactAsync(instructions, ct);
    public Task<IReadOnlyList<Message>> GetMessagesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Message>>([.. _session.Messages]);

    public async ValueTask<PermissionDecision> RequestPermissionAsync(PermissionRequest request, CancellationToken ct) =>
        _options.OnPermissionRequest is { } h ? await h(request, ct).ConfigureAwait(false) : PermissionDecision.Deny("No permission handler registered in the SDK host (deny by default).");

    public async ValueTask<IReadOnlyList<UserQuestionAnswer>?> AskQuestionsAsync(IReadOnlyList<UserQuestion> questions, CancellationToken ct) =>
        _options.OnQuestion is { } q ? await q(questions, ct).ConfigureAwait(false) : null;

    public async ValueTask<PlanDecision> ReviewPlanAsync(string plan, CancellationToken ct) =>
        _options.OnPlanReview is null || await _options.OnPlanReview(plan, ct).ConfigureAwait(false) ? new PlanDecision(PlanApproval.Approve) : new PlanDecision(PlanApproval.Reject);

    public async ValueTask DisposeAsync()
    {
        await _session.DisposeAsync().ConfigureAwait(false);
        await _runtime.DisposeAsync().ConfigureAwait(false);
    }
}
