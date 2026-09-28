"""Protocol types (see schema/protocol.schema.json in the DotCode repository)."""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any, Awaitable, Callable, Dict, List, Literal, Optional, TypedDict, Union


class Usage(TypedDict, total=False):
    inputTokens: int
    outputTokens: int
    cacheReadTokens: int
    cacheWriteTokens: int
    reasoningTokens: int


class AgentEvent(TypedDict, total=False):
    """Engine event. ``type`` is one of: session.started, user.message, assistant.text.delta,
    assistant.thinking.delta, assistant.message, tool.started, tool.progress, tool.completed, todo.updated,
    subagent.started, subagent.completed, context.compacted, usage.updated, model.changed, model.fallback,
    retry, notice, error, mode.changed, turn.completed."""

    type: str
    sessionId: str
    parentToolUseId: str
    text: str
    name: str
    displayName: str
    toolUseId: str
    output: str
    summary: str
    isError: bool
    resultText: str
    diff: str


class PermissionRequest(TypedDict, total=False):
    toolUseId: str
    toolName: str
    displayName: str
    input: Dict[str, Any]
    title: str
    detail: str
    suggestedRule: str
    diff: str


class PermissionDecision(TypedDict, total=False):
    decision: Literal["allow", "allow_always", "allow_session", "deny"]
    feedback: str
    rule: str
    updatedInput: Dict[str, Any]


class UserQuestion(TypedDict, total=False):
    question: str
    header: str
    options: List[Dict[str, str]]
    multiSelect: bool


class SendResult(TypedDict, total=False):
    sessionId: str
    stopReason: str
    result: str
    isError: bool
    error: str
    durationMs: int
    numModelCalls: int
    costUsd: float
    totalCostUsd: float
    usage: Usage


ToolContent = Union[str, List[Dict[str, Any]]]
ToolHandler = Callable[[Dict[str, Any]], Union[ToolContent, Awaitable[ToolContent]]]


@dataclass
class Tool:
    """A tool implemented by your application and offered to the model."""

    name: str
    description: str
    handler: ToolHandler
    input_schema: Dict[str, Any] = field(default_factory=lambda: {"type": "object", "properties": {}})
    read_only: bool = False

    def to_wire(self) -> Dict[str, Any]:
        return {
            "name": self.name,
            "description": self.description,
            "inputSchema": self.input_schema,
            "readOnly": self.read_only,
        }


PermissionHandler = Callable[[PermissionRequest], Union[PermissionDecision, Awaitable[PermissionDecision]]]
QuestionHandler = Callable[[List[UserQuestion]], Union[List[Dict[str, str]], Awaitable[List[Dict[str, str]]]]]
EventHandler = Callable[[AgentEvent], None]
