using DotCode.Abstractions;
using DotCode.Engine.Agent;
using DotCode.Engine.Permissions;
using DotCode.Protocol;

namespace DotCode.Sdk;

/// <summary>Runs the DotCode engine inside the host process (no child process, no serialization).</summary>
internal sealed class InProcessSession : DotCodeSession, IInteractionHandler
{
    private readonly AgentRuntime _runtime;
    private readonly AgentSession _session;
    private CancellationTokenSource? _turn;

    public override string SessionId => _session.Id;
    public override string Model => _session.Model.Qualified;

    private InProcessSession(AgentRuntime runtime, AgentSession session, SessionConfig config) : base(config)
    {
        _runtime = runtime;
        _session = session;
        _session.Sink = new DelegateEventSink(Dispatch);
        _session.Interaction = this;
        foreach (var t in config.Tools)
            _session.ExtraTools.Add(new HostTool(t.Name, t.Description, t.InputSchema, t.ReadOnly, async (toolUseId, input, ct) =>
            {
                var result = await CallToolAsync(toolUseId, t.Name, input, ct).ConfigureAwait(false);
                return (result.ToContent(), result.IsError);
            }));
    }

    public static InProcessSession Create(SessionConfig c, string? defaultCwd)
    {
        var options = new RuntimeOptions
        {
            Cwd = c.WorkingDirectory ?? defaultCwd ?? Environment.CurrentDirectory,
            Model = c.Model,
            FallbackModel = c.FallbackModel,
            PermissionMode = c.PermissionMode?.ToSetting(),
            SystemPrompt = c.SystemMessage is { Mode: SystemMessageMode.Replace } r ? r.Content : null,
            AppendSystemPrompt = c.SystemMessage is { Mode: SystemMessageMode.Append } a ? a.Content : null,
            SettingsJson = c.MergedSettingsJson(),
            MaxTurns = c.MaxTurns,
            Effort = c.EffortSetting,
            PersistSession = c.PersistSession,
            NoMcp = c.DisableMcp,
            Tools = c.AvailableTools?.Select(t => t.ToString()).ToList(),
        };
        options.AllowedTools.AddRange(c.AllowedTools);
        options.DisallowedTools.AddRange(c.ExcludedTools);
        if (c.McpServersJson() is { } mcp) options.McpConfigs.Add(mcp);
        if (c.Worktree || c.WorktreeName is { Length: > 0 })
        {
            var wt = DotCode.Engine.Util.Worktrees.Create(options.Cwd, c.WorktreeName);
            options.Cwd = wt.Path;
            options.Worktree = wt;
        }
        var runtime = AgentRuntime.Create(options);
        return new InProcessSession(runtime, runtime.CreateSession(), c);
    }

    private protected override async Task<SessionResult> RunTurnAsync(MessageOptions message, CancellationToken ct)
    {
        _turn = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            var prompt = message.Prompt;
            var images = new List<ContentPart>();
            foreach (var a in message.Attachments)
                switch (a)
                {
                    case Attachment.File f: prompt += $" @\"{f.Path}\""; break;
                    case Attachment.Image i: images.Add(new ImagePart(i.Base64Data, i.MediaType)); break;
                }
            var expanded = await CommandExpander.ExpandAsync(_session, prompt, _turn.Token).ConfigureAwait(false);
            var r = await _session.RunTurnAsync(expanded.Prompt, images.Count > 0 ? images : null, _turn.Token).ConfigureAwait(false);
            return new SessionResult(r.Text, r.StopReason.ToString(), r.IsError, r.Error, r.Usage, r.CostUsd, _session.TotalCostUsd, (long)r.Duration.TotalMilliseconds, r.ModelCalls);
        }
        finally
        {
            _turn.Dispose();
            _turn = null;
        }
    }

    public override Task AbortAsync() { _turn?.Cancel(); return Task.CompletedTask; }
    public override Task SetModelAsync(string model, CancellationToken ct = default) { _session.SetModel(model); return Task.CompletedTask; }
    public override Task SetPermissionModeAsync(PermissionMode mode, CancellationToken ct = default) { _session.SetMode(mode); return Task.CompletedTask; }
    public override Task SetReasoningEffortAsync(ReasoningEffort effort, CancellationToken ct = default) { _session.Effort = effort; return Task.CompletedTask; }
    public override Task CompactAsync(string? instructions = null, CancellationToken ct = default) => _session.CompactAsync(instructions, ct);
    public override Task<IReadOnlyList<Message>> GetMessagesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Message>>([.. _session.Messages]);

    public async ValueTask<PermissionDecision> RequestPermissionAsync(PermissionRequest request, CancellationToken ct) =>
        await DecidePermissionAsync(request, ct).ConfigureAwait(false);

    public async ValueTask<IReadOnlyList<UserQuestionAnswer>?> AskQuestionsAsync(IReadOnlyList<UserQuestion> questions, CancellationToken ct) =>
        await AnswerQuestionsAsync(questions, ct).ConfigureAwait(false);

    async ValueTask<PlanDecision> IInteractionHandler.ReviewPlanAsync(string plan, CancellationToken ct)
    {
        var r = await ReviewPlanAsync(plan, ct).ConfigureAwait(false);
        return !r.Approved ? new PlanDecision(PlanApproval.Reject, r.Feedback)
            : r.AcceptEdits ? new PlanDecision(PlanApproval.ApproveAcceptEdits) : new PlanDecision(PlanApproval.Approve);
    }

    public override async ValueTask DisposeAsync()
    {
        await _session.DisposeAsync().ConfigureAwait(false);
        await _runtime.DisposeAsync().ConfigureAwait(false);
        if (_runtime.Options.Worktree is { } wt && !DotCode.Engine.Util.Worktrees.HasChanges(wt)) DotCode.Engine.Util.Worktrees.Remove(wt.RepoRoot, wt.Name);
    }
}
