using System.Threading.Channels;
using DotCode.Abstractions;

namespace DotCode.Engine.Agent;

/// <summary>Receives engine events (TUI renderer, headless printer, JSON-RPC notifier, SDK stream).</summary>
public interface IAgentEventSink
{
    void Emit(AgentEvent e);
}

public sealed class NullEventSink : IAgentEventSink
{
    public static readonly NullEventSink Instance = new();
    public void Emit(AgentEvent e) { }
}

public sealed class DelegateEventSink(Action<AgentEvent> action) : IAgentEventSink
{
    public void Emit(AgentEvent e) => action(e);
}

public sealed class ChannelEventSink : IAgentEventSink
{
    private readonly Channel<AgentEvent> _channel = Channel.CreateUnbounded<AgentEvent>(new UnboundedChannelOptions { SingleReader = true });
    public ChannelReader<AgentEvent> Reader => _channel.Reader;
    public void Emit(AgentEvent e) => _channel.Writer.TryWrite(e);
    public void Complete() => _channel.Writer.TryComplete();
}

/// <summary>Fans events out to several sinks (e.g. UI + transcript logger).</summary>
public sealed class CompositeEventSink(params IAgentEventSink[] sinks) : IAgentEventSink
{
    public void Emit(AgentEvent e)
    {
        foreach (var s in sinks) s.Emit(e);
    }
}

public enum PlanApproval { Approve, ApproveAcceptEdits, Reject }

public sealed record PlanDecision(PlanApproval Approval, string? Feedback = null);

/// <summary>Host callbacks for decisions that need a human (or an SDK handler).</summary>
public interface IInteractionHandler
{
    ValueTask<PermissionDecision> RequestPermissionAsync(PermissionRequest request, CancellationToken ct);

    /// <summary>Returns answers, or null when no user is available.</summary>
    ValueTask<IReadOnlyList<UserQuestionAnswer>?> AskQuestionsAsync(IReadOnlyList<UserQuestion> questions, CancellationToken ct);

    ValueTask<PlanDecision> ReviewPlanAsync(string plan, CancellationToken ct);
}

/// <summary>Non-interactive default: anything that needs approval is denied with guidance (headless -p mode).</summary>
public sealed class NonInteractiveHandler : IInteractionHandler
{
    public static readonly NonInteractiveHandler Instance = new();

    public ValueTask<PermissionDecision> RequestPermissionAsync(PermissionRequest request, CancellationToken ct) =>
        ValueTask.FromResult(PermissionDecision.Deny(
            $"Permission to use {request.ToolName} was not granted (non-interactive mode). Allow it with --allowedTools \"{request.SuggestedRule ?? request.ToolName}\", --permission-mode acceptEdits, or --dangerously-skip-permissions."));

    public ValueTask<IReadOnlyList<UserQuestionAnswer>?> AskQuestionsAsync(IReadOnlyList<UserQuestion> questions, CancellationToken ct) =>
        ValueTask.FromResult<IReadOnlyList<UserQuestionAnswer>?>(null);

    public ValueTask<PlanDecision> ReviewPlanAsync(string plan, CancellationToken ct) =>
        ValueTask.FromResult(new PlanDecision(PlanApproval.Approve));
}

public sealed record TurnResult(StopReason StopReason, string Text, Usage Usage, decimal CostUsd, TimeSpan Duration, bool IsError, string? Error = null, int ModelCalls = 0);
