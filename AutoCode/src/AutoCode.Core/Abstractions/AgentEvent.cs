// Auto Code — Gravicode Studios (Kang Fadhil)

using System.Text.Json.Serialization;

namespace AutoCode.Core.Abstractions;

/// <summary>
/// One observable moment in an agent run. The loop emits these; the CLI renders them, the
/// <c>--output-format stream-json</c> mode serialises them verbatim, and hooks receive them as payloads.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TurnStartedEvent), "turn_started")]
[JsonDerivedType(typeof(AssistantTextEvent), "assistant_text")]
[JsonDerivedType(typeof(AssistantThinkingEvent), "assistant_thinking")]
[JsonDerivedType(typeof(ToolCallStartedEvent), "tool_call_started")]
[JsonDerivedType(typeof(ToolCallCompletedEvent), "tool_call_completed")]
[JsonDerivedType(typeof(ToolCallDeniedEvent), "tool_call_denied")]
[JsonDerivedType(typeof(TodoUpdatedEvent), "todo_updated")]
[JsonDerivedType(typeof(SubagentStartedEvent), "subagent_started")]
[JsonDerivedType(typeof(SubagentCompletedEvent), "subagent_completed")]
[JsonDerivedType(typeof(CompactionEvent), "compaction")]
[JsonDerivedType(typeof(NoticeEvent), "notice")]
[JsonDerivedType(typeof(ErrorEvent), "error")]
[JsonDerivedType(typeof(TurnCompletedEvent), "turn_completed")]
public abstract record AgentEvent
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record TurnStartedEvent(string SessionId, int Iteration) : AgentEvent;

/// <summary>A streamed slice of assistant prose. <see cref="IsFinal"/> marks the end of a message.</summary>
public sealed record AssistantTextEvent(string Text, bool IsFinal = false) : AgentEvent;

/// <summary>A streamed slice of extended reasoning, when the provider exposes it.</summary>
public sealed record AssistantThinkingEvent(string Text) : AgentEvent;

public sealed record ToolCallStartedEvent(string CallId, string ToolName, string Summary, string? Detail = null) : AgentEvent;

public sealed record ToolCallCompletedEvent(
    string CallId,
    string ToolName,
    bool Success,
    string Display,
    long ElapsedMs) : AgentEvent;

public sealed record ToolCallDeniedEvent(string CallId, string ToolName, string Reason) : AgentEvent;

public sealed record TodoUpdatedEvent(IReadOnlyList<TodoItem> Items) : AgentEvent;

public sealed record SubagentStartedEvent(string AgentName, string Description) : AgentEvent;

public sealed record SubagentCompletedEvent(string AgentName, string Summary, long ElapsedMs) : AgentEvent;

public sealed record CompactionEvent(int MessagesBefore, int MessagesAfter, string Summary) : AgentEvent;

public sealed record NoticeEvent(string Message, NoticeSeverity Severity = NoticeSeverity.Info) : AgentEvent;

public enum NoticeSeverity
{
    Info = 0,
    Warning = 1,
    Success = 2,
}

public sealed record ErrorEvent(string Message, string? Detail = null) : AgentEvent;

/// <summary>End of a user turn, carrying the accounting for it.</summary>
public sealed record TurnCompletedEvent(
    int Iterations,
    long InputTokens,
    long OutputTokens,
    decimal CostUsd,
    long ElapsedMs) : AgentEvent;
