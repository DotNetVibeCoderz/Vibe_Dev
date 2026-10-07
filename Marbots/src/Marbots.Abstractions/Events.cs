namespace Marbots.Abstractions;

/// <summary>
/// Every observable change in the platform is an <see cref="AgentEvent"/>. Web, CLI, SDKs and the
/// Office view consume the same stream, so visualisation never depends on agent-loop internals.
/// </summary>
public sealed class AgentEvent
{
    public long Id { get; set; }
    public string Type { get; set; } = "";
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    public string? ThreadId { get; set; }
    public string? TaskId { get; set; }
    public string? BotId { get; set; }
    public string? Message { get; set; }
    /// <summary>Optional JSON payload.</summary>
    public string? Data { get; set; }
}

public static class EventTypes
{
    public const string BotCreated = "BotCreated";
    public const string BotUpdated = "BotUpdated";
    public const string BotDeleted = "BotDeleted";
    public const string BotStateChanged = "BotStateChanged";
    public const string MessageAdded = "MessageAdded";
    public const string TaskCreated = "TaskCreated";
    public const string TaskStateChanged = "TaskStateChanged";
    public const string TaskDelegated = "TaskDelegated";
    public const string TaskProgressed = "TaskProgressed";
    public const string AgentThinkingStarted = "AgentThinkingStarted";
    public const string AgentThinkingCompleted = "AgentThinkingCompleted";
    public const string ToolCallStarted = "ToolCallStarted";
    public const string ToolCallCompleted = "ToolCallCompleted";
    public const string ApprovalRequested = "ApprovalRequested";
    public const string ApprovalResolved = "ApprovalResolved";
    public const string MemoryWritten = "MemoryWritten";
    public const string SkillLoaded = "SkillLoaded";
    public const string SkillRolledBack = "SkillRolledBack";
    public const string ContextCompacted = "ContextCompacted";
    public const string AutoLearnCandidateCreated = "AutoLearnCandidateCreated";
    public const string ScheduleTriggered = "ScheduleTriggered";
    public const string HostConnected = "HostConnected";
    public const string HostDisconnected = "HostDisconnected";
    public const string TodoUpdated = "TodoUpdated";
    public const string SettingsChanged = "SettingsChanged";
    /// <summary>Streaming text from a model (transient: not stored). Message = the new text fragment.</summary>
    public const string AssistantDelta = "AssistantDelta";
    public const string ChannelMessageReceived = "ChannelMessageReceived";
    public const string ChannelMessageSent = "ChannelMessageSent";
    public const string TriggerFired = "TriggerFired";
}

public interface IEventBus
{
    /// <summary>Persist and fan out an event. Returns the event with its assigned id.</summary>
    ValueTask<AgentEvent> PublishAsync(AgentEvent evt, CancellationToken cancellationToken = default);

    /// <summary>Fan out without persisting (high-frequency events such as streaming text deltas).</summary>
    void PublishTransient(AgentEvent evt);

    /// <summary>Synchronous in-process subscription (used by Blazor circuits). Handlers must be fast.</summary>
    IDisposable Subscribe(Action<AgentEvent> handler);

    /// <summary>Bounded streaming subscription; slow consumers drop the oldest events.</summary>
    IAsyncEnumerable<AgentEvent> StreamAsync(Func<AgentEvent, bool>? filter, CancellationToken cancellationToken);
}
