"""Typed session events. Every event the engine streams is parsed into one of these dataclasses::

    match event:
        case AssistantTextDeltaEvent(text=text):
            print(text, end="")
        case ToolCompletedEvent(name=name, output=output):
            print(f"{name}: {output}")
        case TurnCompletedEvent():
            done.set()
"""

from __future__ import annotations

from dataclasses import dataclass
from typing import Any, Dict, List, Literal, Optional, Union

TodoStatus = Literal["Pending", "InProgress", "Completed"]
StopReason = Literal["EndTurn", "ToolUse", "MaxTokens", "StopSequence", "Refusal", "Aborted", "Error"]


@dataclass(frozen=True)
class Usage:
    input_tokens: int = 0
    output_tokens: int = 0
    cache_read_tokens: int = 0
    cache_write_tokens: int = 0
    reasoning_tokens: int = 0

    @staticmethod
    def from_wire(d: Optional[Dict[str, Any]]) -> "Usage":
        d = d or {}
        return Usage(d.get("inputTokens", 0), d.get("outputTokens", 0), d.get("cacheReadTokens", 0),
                     d.get("cacheWriteTokens", 0), d.get("reasoningTokens", 0))


@dataclass(frozen=True)
class TodoItem:
    content: str
    status: TodoStatus
    active_form: Optional[str] = None


@dataclass(frozen=True)
class ToolCallInfo:
    id: str
    name: str
    input: Any


@dataclass(frozen=True)
class UserMessageEvent:
    text: str
    session_id: Optional[str] = None
    parent_tool_use_id: Optional[str] = None
    type: Literal["user.message"] = "user.message"


@dataclass(frozen=True)
class AssistantTextDeltaEvent:
    text: str
    session_id: Optional[str] = None
    parent_tool_use_id: Optional[str] = None
    type: Literal["assistant.text.delta"] = "assistant.text.delta"


@dataclass(frozen=True)
class AssistantThinkingDeltaEvent:
    text: str
    session_id: Optional[str] = None
    parent_tool_use_id: Optional[str] = None
    type: Literal["assistant.thinking.delta"] = "assistant.thinking.delta"


@dataclass(frozen=True)
class AssistantMessageEvent:
    message_id: str
    text: str
    thinking: Optional[str]
    tool_calls: List[ToolCallInfo]
    model: str
    session_id: Optional[str] = None
    parent_tool_use_id: Optional[str] = None
    type: Literal["assistant.message"] = "assistant.message"


@dataclass(frozen=True)
class ToolStartedEvent:
    tool_use_id: str
    name: str
    display_name: str
    input: Any
    session_id: Optional[str] = None
    parent_tool_use_id: Optional[str] = None
    type: Literal["tool.started"] = "tool.started"


@dataclass(frozen=True)
class ToolProgressEvent:
    tool_use_id: str
    text: str
    session_id: Optional[str] = None
    parent_tool_use_id: Optional[str] = None
    type: Literal["tool.progress"] = "tool.progress"


@dataclass(frozen=True)
class ToolCompletedEvent:
    tool_use_id: str
    name: str
    is_error: bool
    summary: str
    output: str
    diff: Optional[str] = None
    duration_ms: int = 0
    rejected: bool = False
    session_id: Optional[str] = None
    parent_tool_use_id: Optional[str] = None
    type: Literal["tool.completed"] = "tool.completed"


@dataclass(frozen=True)
class TodoUpdatedEvent:
    todos: List[TodoItem]
    session_id: Optional[str] = None
    parent_tool_use_id: Optional[str] = None
    type: Literal["todo.updated"] = "todo.updated"


@dataclass(frozen=True)
class SubagentStartedEvent:
    tool_use_id: str
    agent_type: str
    description: str
    model: str
    session_id: Optional[str] = None
    parent_tool_use_id: Optional[str] = None
    type: Literal["subagent.started"] = "subagent.started"


@dataclass(frozen=True)
class SubagentCompletedEvent:
    tool_use_id: str
    agent_type: str
    usage: Usage
    duration_ms: int
    tool_uses: int
    session_id: Optional[str] = None
    parent_tool_use_id: Optional[str] = None
    type: Literal["subagent.completed"] = "subagent.completed"


@dataclass(frozen=True)
class ContextCompactedEvent:
    tokens_before: int
    tokens_after: int
    automatic: bool
    session_id: Optional[str] = None
    parent_tool_use_id: Optional[str] = None
    type: Literal["context.compacted"] = "context.compacted"


@dataclass(frozen=True)
class UsageUpdatedEvent:
    turn_usage: Usage
    session_usage: Usage
    session_cost_usd: float
    context_tokens: int
    context_window: int
    session_id: Optional[str] = None
    parent_tool_use_id: Optional[str] = None
    type: Literal["usage.updated"] = "usage.updated"


@dataclass(frozen=True)
class ModelChangedEvent:
    model: str
    session_id: Optional[str] = None
    parent_tool_use_id: Optional[str] = None
    type: Literal["model.changed"] = "model.changed"


@dataclass(frozen=True)
class ModelFallbackEvent:
    from_model: str
    to_model: str
    reason: str
    session_id: Optional[str] = None
    parent_tool_use_id: Optional[str] = None
    type: Literal["model.fallback"] = "model.fallback"


@dataclass(frozen=True)
class RetryEvent:
    attempt: int
    max_attempts: int
    delay_seconds: float
    reason: str
    session_id: Optional[str] = None
    parent_tool_use_id: Optional[str] = None
    type: Literal["retry"] = "retry"


@dataclass(frozen=True)
class NoticeEvent:
    level: Literal["Info", "Warning", "Error"]
    text: str
    session_id: Optional[str] = None
    parent_tool_use_id: Optional[str] = None
    type: Literal["notice"] = "notice"


@dataclass(frozen=True)
class ErrorEvent:
    code: str
    message: str
    retryable: bool
    session_id: Optional[str] = None
    parent_tool_use_id: Optional[str] = None
    type: Literal["error"] = "error"


@dataclass(frozen=True)
class ModeChangedEvent:
    mode: str
    session_id: Optional[str] = None
    parent_tool_use_id: Optional[str] = None
    type: Literal["mode.changed"] = "mode.changed"


@dataclass(frozen=True)
class TurnCompletedEvent:
    stop_reason: StopReason
    result_text: str
    usage: Usage
    cost_usd: float
    duration_ms: int
    num_model_calls: int
    is_error: bool
    session_id: Optional[str] = None
    parent_tool_use_id: Optional[str] = None
    type: Literal["turn.completed"] = "turn.completed"


@dataclass(frozen=True)
class UnknownEvent:
    """An event type this SDK version does not know (forward compatibility)."""

    raw: Dict[str, Any]
    session_id: Optional[str] = None
    parent_tool_use_id: Optional[str] = None
    type: str = "unknown"


SessionEvent = Union[
    UserMessageEvent, AssistantTextDeltaEvent, AssistantThinkingDeltaEvent, AssistantMessageEvent,
    ToolStartedEvent, ToolProgressEvent, ToolCompletedEvent, TodoUpdatedEvent, SubagentStartedEvent,
    SubagentCompletedEvent, ContextCompactedEvent, UsageUpdatedEvent, ModelChangedEvent, ModelFallbackEvent,
    RetryEvent, NoticeEvent, ErrorEvent, ModeChangedEvent, TurnCompletedEvent, UnknownEvent,
]


def parse_event(d: Dict[str, Any]) -> SessionEvent:
    """Parses a wire event (camelCase JSON) into its typed dataclass."""
    t = d.get("type")
    sid: Optional[str] = d.get("sessionId")
    pid: Optional[str] = d.get("parentToolUseId")
    g = d.get
    if t == "user.message":
        return UserMessageEvent(g("text", ""), session_id=sid, parent_tool_use_id=pid)
    if t == "assistant.text.delta":
        return AssistantTextDeltaEvent(g("text", ""), session_id=sid, parent_tool_use_id=pid)
    if t == "assistant.thinking.delta":
        return AssistantThinkingDeltaEvent(g("text", ""), session_id=sid, parent_tool_use_id=pid)
    if t == "assistant.message":
        calls = [ToolCallInfo(c.get("id", ""), c.get("name", ""), c.get("input")) for c in g("toolCalls") or []]
        return AssistantMessageEvent(g("messageId", ""), g("text", ""), g("thinking"), calls, g("model", ""), session_id=sid, parent_tool_use_id=pid)
    if t == "tool.started":
        return ToolStartedEvent(g("toolUseId", ""), g("name", ""), g("displayName", ""), g("input"), session_id=sid, parent_tool_use_id=pid)
    if t == "tool.progress":
        return ToolProgressEvent(g("toolUseId", ""), g("text", ""), session_id=sid, parent_tool_use_id=pid)
    if t == "tool.completed":
        return ToolCompletedEvent(g("toolUseId", ""), g("name", ""), bool(g("isError")), g("summary", ""), g("output", ""),
                                  g("diff"), g("durationMs", 0), bool(g("rejected")), session_id=sid, parent_tool_use_id=pid)
    if t == "todo.updated":
        todos = [TodoItem(i.get("content", ""), i.get("status", "Pending"), i.get("activeForm")) for i in g("todos") or []]
        return TodoUpdatedEvent(todos, session_id=sid, parent_tool_use_id=pid)
    if t == "subagent.started":
        return SubagentStartedEvent(g("toolUseId", ""), g("agentType", ""), g("description", ""), g("model", ""), session_id=sid, parent_tool_use_id=pid)
    if t == "subagent.completed":
        return SubagentCompletedEvent(g("toolUseId", ""), g("agentType", ""), Usage.from_wire(g("usage")), g("durationMs", 0),
                                      g("toolUses", 0), session_id=sid, parent_tool_use_id=pid)
    if t == "context.compacted":
        return ContextCompactedEvent(g("tokensBefore", 0), g("tokensAfter", 0), bool(g("automatic")), session_id=sid, parent_tool_use_id=pid)
    if t == "usage.updated":
        return UsageUpdatedEvent(Usage.from_wire(g("turnUsage")), Usage.from_wire(g("sessionUsage")), g("sessionCostUsd", 0.0),
                                 g("contextTokens", 0), g("contextWindow", 0), session_id=sid, parent_tool_use_id=pid)
    if t == "model.changed":
        return ModelChangedEvent(g("model", ""), session_id=sid, parent_tool_use_id=pid)
    if t == "model.fallback":
        return ModelFallbackEvent(g("from", ""), g("to", ""), g("reason", ""), session_id=sid, parent_tool_use_id=pid)
    if t == "retry":
        return RetryEvent(g("attempt", 0), g("maxAttempts", 0), g("delaySeconds", 0.0), g("reason", ""), session_id=sid, parent_tool_use_id=pid)
    if t == "notice":
        return NoticeEvent(g("level", "Info"), g("text", ""), session_id=sid, parent_tool_use_id=pid)
    if t == "error":
        return ErrorEvent(g("code", ""), g("message", ""), bool(g("retryable")), session_id=sid, parent_tool_use_id=pid)
    if t == "mode.changed":
        return ModeChangedEvent(g("mode", ""), session_id=sid, parent_tool_use_id=pid)
    if t == "turn.completed":
        return TurnCompletedEvent(g("stopReason", "EndTurn"), g("resultText", ""), Usage.from_wire(g("usage")), g("costUsd", 0.0),
                                  g("durationMs", 0), g("numModelCalls", 0), bool(g("isError")), session_id=sid, parent_tool_use_id=pid)
    return UnknownEvent(d, session_id=sid, parent_tool_use_id=pid)
