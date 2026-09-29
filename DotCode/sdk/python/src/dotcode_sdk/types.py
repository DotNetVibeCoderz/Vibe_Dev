"""Typed configuration, permission and tool types of the DotCode SDK."""

from __future__ import annotations

import enum
from dataclasses import dataclass, field
from typing import Any, Awaitable, Callable, Dict, List, Literal, Optional, Union

from .events import Usage, StopReason

PermissionMode = Literal["default", "acceptEdits", "auto", "plan", "bypassPermissions"]
ReasoningEffort = Literal["off", "low", "medium", "high", "xhigh"]


class BuiltinTool(str, enum.Enum):
    """Built-in tool names (use them for ``available_tools`` and permission rules)."""

    READ = "Read"
    WRITE = "Write"
    EDIT = "Edit"
    NOTEBOOK_EDIT = "NotebookEdit"
    GLOB = "Glob"
    GREP = "Grep"
    BASH = "Bash"
    POWERSHELL = "PowerShell"
    BASH_OUTPUT = "BashOutput"
    KILL_SHELL = "KillShell"
    WEB_FETCH = "WebFetch"
    WEB_SEARCH = "WebSearch"
    TODO_WRITE = "TodoWrite"
    AGENT = "Agent"
    SKILL = "Skill"
    ASK_USER_QUESTION = "AskUserQuestion"
    EXIT_PLAN_MODE = "ExitPlanMode"
    LSP = "LSP"

    def __str__(self) -> str:
        return self.value

    def rule(self, specifier: str) -> str:
        """A permission rule for this tool, e.g. ``BuiltinTool.BASH.rule("npm test:*")`` → ``Bash(npm test:*)``."""
        return f"{self.value}({specifier})"


# ---------------------------------------------------------------- permissions


@dataclass(frozen=True)
class PermissionRequest:
    tool_use_id: str
    tool_name: str
    display_name: str
    input: Dict[str, Any]
    title: str
    detail: Optional[str] = None
    #: Rule the user could persist, e.g. ``Bash(npm test:*)``.
    suggested_rule: Optional[str] = None
    #: Unified diff preview for file edits.
    diff: Optional[str] = None
    parent_tool_use_id: Optional[str] = None

    @staticmethod
    def from_wire(d: Dict[str, Any]) -> "PermissionRequest":
        return PermissionRequest(d.get("toolUseId", ""), d.get("toolName", ""), d.get("displayName", ""), d.get("input") or {},
                                 d.get("title", ""), d.get("detail"), d.get("suggestedRule"), d.get("diff"), d.get("parentToolUseId"))


@dataclass(frozen=True)
class PermissionDecisionApproveOnce:
    """Allow this single call."""

    updated_input: Optional[Dict[str, Any]] = None


@dataclass(frozen=True)
class PermissionDecisionApproveForSession:
    """Allow matching calls for the rest of the session."""

    updated_input: Optional[Dict[str, Any]] = None


@dataclass(frozen=True)
class PermissionDecisionApproveAlways:
    """Allow and persist a rule (defaults to the request's suggested rule)."""

    rule: Optional[str] = None


@dataclass(frozen=True)
class PermissionDecisionReject:
    """Deny; ``feedback`` is returned to the model."""

    feedback: Optional[str] = None


PermissionDecision = Union[
    PermissionDecisionApproveOnce, PermissionDecisionApproveForSession, PermissionDecisionApproveAlways, PermissionDecisionReject
]


@dataclass(frozen=True)
class Invocation:
    """Context passed to handlers."""

    session_id: str


PermissionHandlerFunc = Callable[[PermissionRequest, Invocation], Union[PermissionDecision, Awaitable[PermissionDecision]]]


class PermissionHandler:
    """Ready-made permission handlers."""

    @staticmethod
    def approve_all(request: PermissionRequest, invocation: Invocation) -> PermissionDecision:
        return PermissionDecisionApproveOnce()

    @staticmethod
    def reject_all(request: PermissionRequest, invocation: Invocation) -> PermissionDecision:
        return PermissionDecisionReject("Rejected by the SDK host.")


def decision_to_wire(d: PermissionDecision) -> Dict[str, Any]:
    if isinstance(d, PermissionDecisionApproveOnce):
        return _compact({"decision": "allow", "updatedInput": d.updated_input})
    if isinstance(d, PermissionDecisionApproveForSession):
        return _compact({"decision": "allow_session", "updatedInput": d.updated_input})
    if isinstance(d, PermissionDecisionApproveAlways):
        return _compact({"decision": "allow_always", "rule": d.rule})
    if isinstance(d, PermissionDecisionReject):
        return _compact({"decision": "deny", "feedback": d.feedback})
    raise TypeError(f"Not a PermissionDecision: {d!r}")


# ---------------------------------------------------------------- user input & plan mode


@dataclass(frozen=True)
class QuestionOption:
    label: str
    description: Optional[str] = None


@dataclass(frozen=True)
class UserQuestion:
    question: str
    header: str
    options: List[QuestionOption]
    multi_select: bool = False


@dataclass(frozen=True)
class UserInputRequest:
    questions: List[UserQuestion]

    @staticmethod
    def from_wire(items: List[Dict[str, Any]]) -> "UserInputRequest":
        return UserInputRequest([
            UserQuestion(q.get("question", ""), q.get("header", ""),
                         [QuestionOption(o.get("label", ""), o.get("description")) for o in q.get("options") or []],
                         bool(q.get("multiSelect")))
            for q in items
        ])


@dataclass(frozen=True)
class UserQuestionAnswer:
    question: str
    #: The chosen option label(s) (comma-separated for multi-select) or free text.
    answer: str


UserInputHandler = Callable[[UserInputRequest, Invocation], Union[List[UserQuestionAnswer], Awaitable[List[UserQuestionAnswer]]]]


@dataclass(frozen=True)
class ExitPlanModeRequest:
    #: The plan (markdown) the agent wants to execute.
    plan: str


@dataclass(frozen=True)
class ExitPlanModeResult:
    approved: bool
    #: Continue in ``acceptEdits`` mode (when approved).
    accept_edits: bool = False
    #: Why the plan was rejected (returned to the model).
    feedback: Optional[str] = None


ExitPlanModeHandler = Callable[[ExitPlanModeRequest, Invocation], Union[ExitPlanModeResult, Awaitable[ExitPlanModeResult]]]


# ---------------------------------------------------------------- configuration

ProviderType = Literal["anthropic", "openai", "azure", "gemini", "deepseek", "ollama", "openai-compatible",
                       "bedrock", "vertex", "vertex-gemini", "mock"]


@dataclass
class ProviderConfig:
    """A named model provider (BYOK). Values may reference environment variables: ``"${env:MY_KEY}"``."""

    type: ProviderType
    base_url: Optional[str] = None
    api_key: Optional[str] = None
    #: OpenAI family: wire API.
    api: Optional[Literal["responses", "chat"]] = None
    headers: Optional[Dict[str, str]] = None
    #: Quirk profile for OpenAI-compatible servers (deepseek, openrouter, lmstudio, vllm, litellm, groq, together, azure).
    profile: Optional[str] = None
    #: Models to advertise when the API cannot list them.
    models: Optional[List[str]] = None
    timeout_seconds: Optional[int] = None
    #: Ollama context window.
    num_ctx: Optional[int] = None
    #: AWS region (bedrock) or Google Cloud location (vertex).
    region: Optional[str] = None
    aws_profile: Optional[str] = None
    #: Google Cloud project (vertex).
    project: Optional[str] = None
    credentials_file: Optional[str] = None
    #: Azure: ``"entra"`` for Microsoft Entra ID tokens instead of an API key.
    auth: Optional[Literal["key", "entra"]] = None
    tenant_id: Optional[str] = None
    client_id: Optional[str] = None
    client_secret: Optional[str] = None
    #: Scripted provider (tests): path to a script file.
    script: Optional[str] = None

    def to_wire(self) -> Dict[str, Any]:
        return _compact({
            "type": self.type, "baseUrl": self.base_url, "apiKey": self.api_key, "api": self.api, "headers": self.headers,
            "profile": self.profile, "models": self.models, "timeoutSeconds": self.timeout_seconds, "numCtx": self.num_ctx,
            "region": self.region, "awsProfile": self.aws_profile, "project": self.project,
            "credentialsFile": self.credentials_file, "auth": self.auth, "tenantId": self.tenant_id,
            "clientId": self.client_id, "clientSecret": self.client_secret, "script": self.script,
        })


@dataclass
class McpStdioServer:
    command: str
    args: List[str] = field(default_factory=list)
    env: Dict[str, str] = field(default_factory=dict)

    def to_wire(self) -> Dict[str, Any]:
        return _compact({"type": "stdio", "command": self.command, "args": self.args or None, "env": self.env or None})


@dataclass
class McpHttpServer:
    url: str
    headers: Dict[str, str] = field(default_factory=dict)
    #: ``"sse"`` for legacy SSE servers.
    transport: Literal["http", "sse"] = "http"

    def to_wire(self) -> Dict[str, Any]:
        return _compact({"type": self.transport, "url": self.url, "headers": self.headers or None})


McpServerConfig = Union[McpStdioServer, McpHttpServer]


@dataclass
class SystemMessageConfig:
    content: str
    #: ``append`` (default) adds to DotCode's system prompt; ``replace`` swaps it entirely.
    mode: Literal["append", "replace"] = "append"


@dataclass(frozen=True)
class FileAttachment:
    path: str


@dataclass(frozen=True)
class ImageAttachment:
    #: Base64 data.
    data: str
    media_type: Literal["image/png", "image/jpeg", "image/gif", "image/webp"] = "image/png"


Attachment = Union[FileAttachment, ImageAttachment]


def attachment_to_wire(a: Attachment) -> Dict[str, Any]:
    if isinstance(a, FileAttachment):
        return {"type": "file", "path": a.path}
    return {"type": "image", "data": a.data, "mediaType": a.media_type}


# ---------------------------------------------------------------- results


@dataclass(frozen=True)
class SendResult:
    session_id: str
    stop_reason: StopReason
    result: str
    is_error: bool
    error: Optional[str]
    duration_ms: int
    num_model_calls: int
    cost_usd: float
    total_cost_usd: float
    usage: Usage

    @staticmethod
    def from_wire(d: Dict[str, Any]) -> "SendResult":
        return SendResult(d.get("sessionId", ""), d.get("stopReason", "EndTurn"), d.get("result", ""), bool(d.get("isError")),
                          d.get("error"), d.get("durationMs", 0), d.get("numModelCalls", 0), d.get("costUsd", 0.0),
                          d.get("totalCostUsd", 0.0), Usage.from_wire(d.get("usage")))


@dataclass(frozen=True)
class SessionMetadata:
    id: str
    first_prompt: str
    modified: str
    message_count: int
    title: Optional[str] = None


@dataclass(frozen=True)
class ModelInfo:
    provider: str
    id: str
    qualified_id: str


@dataclass(frozen=True)
class ToolInfo:
    name: str
    description: str
    input_schema: Any


def _compact(d: Dict[str, Any]) -> Dict[str, Any]:
    return {k: v for k, v in d.items() if v is not None}
