using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotCode.Abstractions;

/// <summary>Events emitted by the engine. The TUI (in-process) and SDK clients (over JSON-RPC) consume the exact
/// same stream, so no feature can exist in only one surface. <see cref="ParentToolUseId"/> is set for events
/// produced inside a subagent.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SessionStartedEvent), "session.started")]
[JsonDerivedType(typeof(UserMessageEvent), "user.message")]
[JsonDerivedType(typeof(AssistantTextDeltaEvent), "assistant.text.delta")]
[JsonDerivedType(typeof(AssistantThinkingDeltaEvent), "assistant.thinking.delta")]
[JsonDerivedType(typeof(AssistantMessageEvent), "assistant.message")]
[JsonDerivedType(typeof(ToolStartedEvent), "tool.started")]
[JsonDerivedType(typeof(ToolProgressEvent), "tool.progress")]
[JsonDerivedType(typeof(ToolCompletedEvent), "tool.completed")]
[JsonDerivedType(typeof(TodoUpdatedEvent), "todo.updated")]
[JsonDerivedType(typeof(SubagentStartedEvent), "subagent.started")]
[JsonDerivedType(typeof(SubagentCompletedEvent), "subagent.completed")]
[JsonDerivedType(typeof(ContextCompactedEvent), "context.compacted")]
[JsonDerivedType(typeof(UsageUpdatedEvent), "usage.updated")]
[JsonDerivedType(typeof(ModelChangedEvent), "model.changed")]
[JsonDerivedType(typeof(ModelFallbackEvent), "model.fallback")]
[JsonDerivedType(typeof(RetryEvent), "retry")]
[JsonDerivedType(typeof(NoticeEvent), "notice")]
[JsonDerivedType(typeof(ErrorEvent), "error")]
[JsonDerivedType(typeof(ModeChangedEvent), "mode.changed")]
[JsonDerivedType(typeof(TurnCompletedEvent), "turn.completed")]
public abstract record AgentEvent
{
    public string? SessionId { get; init; }
    public string? ParentToolUseId { get; init; }
}

public sealed record SessionStartedEvent(string Model, string Cwd, IReadOnlyList<string> Tools, string PermissionMode) : AgentEvent;
public sealed record UserMessageEvent(string Text) : AgentEvent;
public sealed record AssistantTextDeltaEvent(string Text) : AgentEvent;
public sealed record AssistantThinkingDeltaEvent(string Text) : AgentEvent;
/// <summary>Completed assistant message for one model call (text and tool calls).</summary>
public sealed record AssistantMessageEvent(string MessageId, string Text, string? Thinking, IReadOnlyList<ToolCallInfo> ToolCalls, string Model) : AgentEvent;
public sealed record ToolCallInfo(string Id, string Name, JsonElement Input);

public sealed record ToolStartedEvent(string ToolUseId, string Name, string DisplayName, JsonElement Input) : AgentEvent;
public sealed record ToolProgressEvent(string ToolUseId, string Text) : AgentEvent;
public sealed record ToolCompletedEvent(string ToolUseId, string Name, bool IsError, string Summary, string Output) : AgentEvent
{
    /// <summary>Unified diff for file mutations, rendered by the UI.</summary>
    public string? Diff { get; init; }
    public long DurationMs { get; init; }
    /// <summary>True when the user rejected the call at the permission prompt.</summary>
    public bool Rejected { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<TodoStatus>))]
public enum TodoStatus { Pending, InProgress, Completed }

public sealed record TodoItem(string Content, TodoStatus Status, string? ActiveForm = null);
public sealed record TodoUpdatedEvent(IReadOnlyList<TodoItem> Todos) : AgentEvent;

public sealed record SubagentStartedEvent(string ToolUseId, string AgentType, string Description, string Model) : AgentEvent;
public sealed record SubagentCompletedEvent(string ToolUseId, string AgentType, Usage Usage, long DurationMs, int ToolUses) : AgentEvent;

public sealed record ContextCompactedEvent(long TokensBefore, long TokensAfter, bool Automatic) : AgentEvent;
public sealed record UsageUpdatedEvent(Usage TurnUsage, Usage SessionUsage, decimal SessionCostUsd, long ContextTokens, int ContextWindow) : AgentEvent;
public sealed record ModelChangedEvent(string Model) : AgentEvent;
public sealed record ModelFallbackEvent(string From, string To, string Reason) : AgentEvent;
public sealed record RetryEvent(int Attempt, int MaxAttempts, double DelaySeconds, string Reason) : AgentEvent;

[JsonConverter(typeof(JsonStringEnumConverter<NoticeLevel>))]
public enum NoticeLevel { Info, Warning, Error }
public sealed record NoticeEvent(NoticeLevel Level, string Text) : AgentEvent;
public sealed record ErrorEvent(string Code, string Message, bool Retryable) : AgentEvent;
public sealed record ModeChangedEvent(string Mode) : AgentEvent;
public sealed record TurnCompletedEvent(StopReason StopReason, string ResultText, Usage Usage, decimal CostUsd, long DurationMs, int NumModelCalls, bool IsError) : AgentEvent;

// ---------- Interaction contracts (engine -> host callbacks) ----------

[JsonConverter(typeof(JsonStringEnumConverter<PermissionBehavior>))]
public enum PermissionBehavior { Allow, Deny, Ask }

/// <summary>Request shown to the user (TUI dialog) or sent to an SDK permission handler.</summary>
public sealed record PermissionRequest(
    string ToolUseId,
    string ToolName,
    string DisplayName,
    JsonElement Input,
    string Title,
    string? Detail,
    // Rule the user can persist with "don't ask again", e.g. Bash(npm test:*)
    string? SuggestedRule,
    // Unified diff preview for edits
    string? Diff,
    string? ParentToolUseId = null);

[JsonConverter(typeof(JsonStringEnumConverter<PermissionDecisionKind>))]
public enum PermissionDecisionKind { AllowOnce, AllowAlways, AllowSession, Deny }

public sealed record PermissionDecision(PermissionDecisionKind Kind, string? Feedback = null, string? Rule = null, JsonElement? UpdatedInput = null)
{
    public bool Allowed => Kind != PermissionDecisionKind.Deny;
    public static readonly PermissionDecision AllowOnce = new(PermissionDecisionKind.AllowOnce);
    public static PermissionDecision Deny(string? feedback = null) => new(PermissionDecisionKind.Deny, feedback);
}

public sealed record QuestionOption(string Label, string? Description = null);
public sealed record UserQuestion(string Question, string Header, IReadOnlyList<QuestionOption> Options, bool MultiSelect = false);
public sealed record UserQuestionAnswer(string Question, string Answer);
