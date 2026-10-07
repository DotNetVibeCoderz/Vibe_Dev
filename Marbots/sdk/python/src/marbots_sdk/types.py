"""Typed models, names and options of the Marbots SDK. Wire shapes follow the server's ``/api/v1`` JSON (camelCase)."""

from __future__ import annotations

import enum
from dataclasses import dataclass, field
from typing import Any, Dict, List, Literal, Optional, Union, cast

BotStatus = Literal["Ready", "Running", "Paused", "Archived", "Degraded"]
TaskState = Literal["Queued", "Preparing", "Running", "WaitingForTool", "WaitingForAgent", "WaitingForHuman",
                    "Completed", "Failed", "Cancelled", "TimedOut"]
AutoLearnMode = Literal["Off", "MemoryOnly", "SuggestSkills"]
ApprovalScope = Literal["Once", "Session"]
ApprovalState = Literal["Pending", "Approved", "Rejected", "Expired"]
MemoryKind = Literal["Semantic", "Episodic", "Procedural", "Relational", "Artifact"]
Role = Literal["user", "assistant", "tool", "system"]

TERMINAL_STATES: frozenset[str] = frozenset({"Completed", "Failed", "Cancelled", "TimedOut"})

#: Id of the protected manager bot.
BOSS_MAN = "boss-man"

#: ``Bot.host_ref`` values besides a registered host id: the server itself, or automatic placement.
LOCAL_HOST = "local-default"
AUTO_HOST = "auto"

SkillVerdict = Literal["CollectingEvidence", "Healthy", "Underperforming", "RollbackRecommended", "ReadyToPromote", "DiscardRecommended"]


class KernelPack(str, enum.Enum):
    """Built-in tool packs a bot can enable (typos are rejected by type checkers)."""

    FILES = "files"
    SEARCH = "search"
    SHELL = "shell"
    WEB = "web"
    MEMORY = "memory"
    TODO = "todo"
    AGENTS = "agents"
    #: Computer use: screenshots, mouse and keyboard on the bot's computer (Windows hosts).
    DESKTOP = "desktop"
    #: spawn_subagents: parallel temporary copies of the bot.
    SUBAGENTS = "subagents"

    def __str__(self) -> str:
        return self.value


class PermissionProfile(str, enum.Enum):
    """What a bot may do without asking (see docs/en/security.md)."""

    READ_ONLY = "read-only"
    WORKSPACE_WRITE = "workspace-write"
    DEVELOPER_SAFE = "developer-safe"
    AUTONOMOUS = "autonomous"
    MANAGER = "manager"

    def __str__(self) -> str:
        return self.value


class EventType(str, enum.Enum):
    """Types of :class:`AgentEvent` on the live event stream."""

    BOT_CREATED = "BotCreated"
    BOT_UPDATED = "BotUpdated"
    BOT_DELETED = "BotDeleted"
    BOT_STATE_CHANGED = "BotStateChanged"
    MESSAGE_ADDED = "MessageAdded"
    TASK_CREATED = "TaskCreated"
    TASK_STATE_CHANGED = "TaskStateChanged"
    TASK_DELEGATED = "TaskDelegated"
    TASK_PROGRESSED = "TaskProgressed"
    AGENT_THINKING_STARTED = "AgentThinkingStarted"
    AGENT_THINKING_COMPLETED = "AgentThinkingCompleted"
    TOOL_CALL_STARTED = "ToolCallStarted"
    TOOL_CALL_COMPLETED = "ToolCallCompleted"
    APPROVAL_REQUESTED = "ApprovalRequested"
    APPROVAL_RESOLVED = "ApprovalResolved"
    MEMORY_WRITTEN = "MemoryWritten"
    SKILL_LOADED = "SkillLoaded"
    CONTEXT_COMPACTED = "ContextCompacted"
    AUTO_LEARN_CANDIDATE_CREATED = "AutoLearnCandidateCreated"
    SCHEDULE_TRIGGERED = "ScheduleTriggered"
    HOST_CONNECTED = "HostConnected"
    TODO_UPDATED = "TodoUpdated"

    def __str__(self) -> str:
        return self.value


class ModelRef:
    """Builds a bot's model setting: the workspace default, a ``provider/model`` pair, or a named profile."""

    DEFAULT = "default"

    @staticmethod
    def of(provider: str, model: str) -> str:
        """``ModelRef.of("azure", "gpt-5.6-luna")`` → ``"azure/gpt-5.6-luna"``."""
        if not provider or not model or "/" in provider:
            raise ValueError("provider and model are required; provider cannot contain '/'")
        return f"{provider}/{model}"

    @staticmethod
    def profile(name: str) -> str:
        return name


# ---------------------------------------------------------------- helpers

Json = Dict[str, Any]


def _s(d: Json, k: str, default: str = "") -> str:
    v = d.get(k)
    return v if isinstance(v, str) else default


def _os(d: Json, k: str) -> Optional[str]:
    v = d.get(k)
    return v if isinstance(v, str) else None


def _i(d: Json, k: str) -> int:
    v = d.get(k)
    return int(v) if isinstance(v, (int, float)) else 0


def _f(d: Json, k: str) -> float:
    v = d.get(k)
    return float(v) if isinstance(v, (int, float)) else 0.0


def _b(d: Json, k: str) -> bool:
    return d.get(k) is True


def _ls(d: Json, k: str) -> List[str]:
    v = d.get(k)
    return [x for x in v if isinstance(x, str)] if isinstance(v, list) else []


def _lo(d: Json, k: str) -> List[Json]:
    v = d.get(k)
    return [x for x in v if isinstance(x, dict)] if isinstance(v, list) else []


# ---------------------------------------------------------------- bots & templates


@dataclass(frozen=True)
class ContainerProfile:
    """Run the bot's shell commands in a throwaway Docker container with these quotas."""

    image: str
    cpus: float = 1.0
    memory_mb: int = 1024
    network: bool = True

    def to_wire(self) -> Json:
        return {"image": self.image, "cpus": self.cpus, "memoryMb": self.memory_mb, "network": self.network}

    @staticmethod
    def from_wire(d: Optional[Json]) -> Optional["ContainerProfile"]:
        if not d:
            return None
        return ContainerProfile(_s(d, "image"), _f(d, "cpus") or 1.0, _i(d, "memoryMb") or 1024, bool(d.get("network", True)))


@dataclass(frozen=True)
class Bot:
    id: str
    name: str
    role: str
    description: str
    persona: str
    color: str
    #: ``"default"`` (workspace default model), a profile name, or ``"provider/model"``.
    model: str
    kernel_functions: List[str]
    skills: List[str]
    mcp_servers: List[str]
    permission_profile: str
    auto_learn: AutoLearnMode
    short_term_memory: bool
    long_term_memory: bool
    max_steps: int
    status: BotStatus
    is_system: bool
    template_id: Optional[str] = None
    #: Where the bot's files/shell/desktop tools run: ``LOCAL_HOST``, a host id, or ``AUTO_HOST``.
    host_ref: str = LOCAL_HOST
    container: Optional[ContainerProfile] = None

    @property
    def uses_default_model(self) -> bool:
        return self.model in ("", ModelRef.DEFAULT)

    @staticmethod
    def from_wire(d: Json) -> "Bot":
        return Bot(_s(d, "id"), _s(d, "name"), _s(d, "role"), _s(d, "description"), _s(d, "persona"), _s(d, "color"),
                   _s(d, "modelProfile", "default"), _ls(d, "kernelFunctions"), _ls(d, "skills"), _ls(d, "mcpServers"),
                   _s(d, "permissionProfile"), cast(AutoLearnMode, _s(d, "autoLearn", "Off")), _b(d, "shortTermMemory"),
                   _b(d, "longTermMemory"), _i(d, "maxSteps"), cast(BotStatus, _s(d, "status", "Ready")),
                   _b(d, "isSystem"), _os(d, "templateId"), _s(d, "hostRef", LOCAL_HOST),
                   ContainerProfile.from_wire(d.get("container") if isinstance(d.get("container"), dict) else None))


@dataclass
class BotSpec:
    """Options for creating or updating a bot. Unset fields take the server defaults."""

    name: str
    role: str = ""
    description: str = ""
    persona: str = ""
    color: str = "#2C3BA3"
    #: ``ModelRef.DEFAULT``, ``ModelRef.of(provider, model)`` or a profile name.
    model: str = ModelRef.DEFAULT
    kernel_functions: List[KernelPack] = field(default_factory=lambda: [KernelPack.FILES, KernelPack.SEARCH, KernelPack.WEB, KernelPack.MEMORY, KernelPack.TODO])
    skills: List[str] = field(default_factory=list)
    mcp_servers: List[str] = field(default_factory=list)
    permission_profile: PermissionProfile = PermissionProfile.DEVELOPER_SAFE
    auto_learn: AutoLearnMode = "Off"
    short_term_memory: bool = True
    long_term_memory: bool = True
    max_steps: int = 24
    host_ref: str = LOCAL_HOST
    container: Optional[ContainerProfile] = None

    def to_wire(self, bot_id: str = "") -> Json:
        return {"id": bot_id, "name": self.name, "role": self.role, "description": self.description, "persona": self.persona,
                "color": self.color, "modelProfile": self.model, "kernelFunctions": [k.value for k in self.kernel_functions],
                "skills": self.skills, "mcpServers": self.mcp_servers, "permissionProfile": self.permission_profile.value,
                "autoLearn": self.auto_learn, "shortTermMemory": self.short_term_memory,
                "longTermMemory": self.long_term_memory, "maxSteps": self.max_steps, "hostRef": self.host_ref,
                "container": self.container.to_wire() if self.container else None}


@dataclass(frozen=True)
class BotTemplate:
    id: str
    name: str
    category: str
    role: str
    description: str
    persona: str
    skills: List[str]
    mcp_servers: List[str]
    kernel_functions: List[str]
    tags: List[str]
    permission_profile: str
    model: str
    is_built_in: bool

    @staticmethod
    def from_wire(d: Json) -> "BotTemplate":
        return BotTemplate(_s(d, "id"), _s(d, "name"), _s(d, "category"), _s(d, "role"), _s(d, "description"),
                           _s(d, "persona"), _ls(d, "skills"), _ls(d, "mcpServers"), _ls(d, "kernelFunctions"),
                           _ls(d, "tags"), _s(d, "permissionProfile"), _s(d, "modelProfile", "default"), _b(d, "isBuiltIn"))


@dataclass(frozen=True)
class BotModelInfo:
    bot_id: str
    #: The bot's own setting (``"default"``, profile or ``provider/model``).
    setting: str
    #: The ``provider/model`` the bot actually runs on.
    effective: str
    uses_default: bool
    warning: Optional[str] = None

    @staticmethod
    def from_wire(d: Json) -> "BotModelInfo":
        return BotModelInfo(_s(d, "botId"), _s(d, "setting"), _s(d, "effective"), _b(d, "usesDefault"), _os(d, "warning"))


@dataclass(frozen=True)
class ModelProfileInfo:
    name: str
    provider: str
    model: str
    fallbacks: List[str]


@dataclass(frozen=True)
class ModelCatalog:
    #: The workspace default ``provider/model``.
    default: str
    #: ``provider/model`` pairs offered in model pickers.
    choices: List[str]
    profiles: List[ModelProfileInfo]

    @staticmethod
    def from_wire(d: Json) -> "ModelCatalog":
        return ModelCatalog(_s(d, "default"), _ls(d, "choices"),
                            [ModelProfileInfo(_s(p, "name"), _s(p, "provider"), _s(p, "model"), _ls(p, "fallbacks")) for p in _lo(d, "profiles")])


# ---------------------------------------------------------------- threads, messages, tasks


@dataclass(frozen=True)
class ChatThread:
    id: str
    title: str
    bot_id: str
    pinned: bool
    archived: bool
    updated_at: str

    @staticmethod
    def from_wire(d: Json) -> "ChatThread":
        return ChatThread(_s(d, "id"), _s(d, "title"), _s(d, "botId"), _b(d, "pinned"), _b(d, "archived"), _s(d, "updatedAt"))


@dataclass(frozen=True)
class ToolCall:
    id: str
    name: str
    arguments: str


@dataclass(frozen=True)
class ChatMessage:
    id: str
    thread_id: str
    seq: int
    role: Role
    author: str
    content: str
    tool_calls: List[ToolCall]
    tool_call_id: Optional[str]
    tool_name: Optional[str]
    task_id: Optional[str]
    created_at: str

    @staticmethod
    def from_wire(d: Json) -> "ChatMessage":
        return ChatMessage(_s(d, "id"), _s(d, "threadId"), _i(d, "seq"), cast(Role, _s(d, "role", "user")), _s(d, "author"),
                           _s(d, "content"), [ToolCall(_s(c, "id"), _s(c, "name"), _s(c, "arguments", "{}")) for c in _lo(d, "toolCalls")],
                           _os(d, "toolCallId"), _os(d, "toolName"), _os(d, "taskId"), _s(d, "createdAt"))


@dataclass(frozen=True)
class TaskRecord:
    id: str
    parent_task_id: Optional[str]
    thread_id: str
    bot_id: str
    depth: int
    objective: str
    state: TaskState
    result: Optional[str]
    error: Optional[str]
    current_activity: Optional[str]
    #: ``provider/model`` that served the latest step.
    model: Optional[str]
    steps: int
    input_tokens: int
    output_tokens: int
    cost_usd: float

    @property
    def is_terminal(self) -> bool:
        return self.state in TERMINAL_STATES

    @staticmethod
    def from_wire(d: Json) -> "TaskRecord":
        return TaskRecord(_s(d, "id"), _os(d, "parentTaskId"), _s(d, "threadId"), _s(d, "botId"), _i(d, "depth"),
                          _s(d, "objective"), cast(TaskState, _s(d, "state", "Queued")), _os(d, "result"), _os(d, "error"),
                          _os(d, "currentActivity"), _os(d, "model"), _i(d, "steps"), _i(d, "inputTokens"),
                          _i(d, "outputTokens"), _f(d, "costUsd"))


@dataclass(frozen=True)
class SendResult:
    task: TaskRecord
    #: The bot's final reply (only when sent with ``wait=True``).
    reply: Optional[ChatMessage]

    @property
    def text(self) -> str:
        return self.reply.content if self.reply else (self.task.result or self.task.error or "")

    @staticmethod
    def from_wire(d: Json) -> "SendResult":
        reply = d.get("reply")
        task = d.get("task")
        return SendResult(TaskRecord.from_wire(task if isinstance(task, dict) else {}),
                          ChatMessage.from_wire(reply) if isinstance(reply, dict) else None)


@dataclass(frozen=True)
class WorkspaceFile:
    path: str
    size: int
    modified: str
    #: Set when the file lives on a remote agent host (download with ``?host=<id>``).
    host: Optional[str] = None
    host_name: Optional[str] = None

    @staticmethod
    def from_wire(d: Json) -> "WorkspaceFile":
        return WorkspaceFile(_s(d, "path"), _i(d, "size"), _s(d, "modified"), _os(d, "host"), _os(d, "hostName"))


# ---------------------------------------------------------------- approvals, events, memory, skills, mcp, schedules


@dataclass(frozen=True)
class ApprovalRequest:
    id: str
    task_id: str
    thread_id: str
    bot_id: str
    tool_name: str
    arguments: str
    category: str
    risk: str
    reason: str
    state: ApprovalState

    @staticmethod
    def from_wire(d: Json) -> "ApprovalRequest":
        return ApprovalRequest(_s(d, "id"), _s(d, "taskId"), _s(d, "threadId"), _s(d, "botId"), _s(d, "toolName"),
                               _s(d, "arguments", "{}"), _s(d, "category"), _s(d, "risk"), _s(d, "reason"),
                               cast(ApprovalState, _s(d, "state", "Pending")))


@dataclass(frozen=True)
class AgentEvent:
    id: int
    #: An :class:`EventType` value (compare with ``EventType.TASK_STATE_CHANGED``); unknown future types are kept as text.
    type: Union[EventType, str]
    timestamp: str
    thread_id: Optional[str]
    task_id: Optional[str]
    bot_id: Optional[str]
    message: Optional[str]
    data: Optional[str]

    def is_task_finished(self) -> bool:
        return self.type == EventType.TASK_STATE_CHANGED and (self.data or "") in TERMINAL_STATES

    @staticmethod
    def from_wire(d: Json) -> "AgentEvent":
        raw = _s(d, "type")
        try:
            t: Union[EventType, str] = EventType(raw)
        except ValueError:
            t = raw
        return AgentEvent(_i(d, "id"), t, _s(d, "timestamp"), _os(d, "threadId"), _os(d, "taskId"), _os(d, "botId"),
                          _os(d, "message"), _os(d, "data"))


@dataclass(frozen=True)
class MemoryRecord:
    id: str
    owner: str
    kind: MemoryKind
    content: str
    source: str
    confidence: float
    created_at: str

    @staticmethod
    def from_wire(d: Json) -> "MemoryRecord":
        return MemoryRecord(_s(d, "id"), _s(d, "owner"), cast(MemoryKind, _s(d, "kind", "Semantic")), _s(d, "content"),
                            _s(d, "source"), _f(d, "confidence"), _s(d, "createdAt"))


@dataclass(frozen=True)
class SkillInfo:
    name: str
    version: str
    description: str
    trust: str
    source: str
    pending: bool

    @staticmethod
    def from_wire(d: Json) -> "SkillInfo":
        return SkillInfo(_s(d, "name"), _s(d, "version"), _s(d, "description"), _s(d, "trust"), _s(d, "source"), _b(d, "pending"))


@dataclass(frozen=True)
class McpServer:
    id: str
    name: str
    description: str
    transport: str
    command: Optional[str]
    args: List[str]
    url: Optional[str]
    trust: str
    installed: bool

    @staticmethod
    def from_wire(d: Json) -> "McpServer":
        return McpServer(_s(d, "id"), _s(d, "name"), _s(d, "description"), _s(d, "transport"), _os(d, "command"),
                         _ls(d, "args"), _os(d, "url"), _s(d, "trust"), not _b(d, "isCatalogEntry"))


@dataclass
class ScheduleSpec:
    """A recurring (``cron``) or one-off (``run_at``, ISO-8601) job that sends ``prompt`` to ``bot_id``."""

    name: str
    bot_id: str
    prompt: str
    cron: str = ""
    run_at: Optional[str] = None
    time_zone: str = "UTC"
    enabled: bool = True

    def to_wire(self) -> Json:
        return {"name": self.name, "botId": self.bot_id, "prompt": self.prompt, "cron": self.cron, "runAt": self.run_at,
                "timeZone": self.time_zone, "enabled": self.enabled}


@dataclass(frozen=True)
class ScheduleJob:
    id: str
    name: str
    bot_id: str
    prompt: str
    cron: str
    time_zone: str
    enabled: bool
    next_run_at: Optional[str]
    last_run_at: Optional[str]

    @staticmethod
    def from_wire(d: Json) -> "ScheduleJob":
        return ScheduleJob(_s(d, "id"), _s(d, "name"), _s(d, "botId"), _s(d, "prompt"), _s(d, "cron"), _s(d, "timeZone"),
                           _b(d, "enabled"), _os(d, "nextRunAt"), _os(d, "lastRunAt"))


@dataclass(frozen=True)
class HostMetrics:
    cpu_percent: float
    free_memory_mb: int
    running_calls: int
    free_disk_mb: int

    @staticmethod
    def from_wire(d: Optional[Json]) -> Optional["HostMetrics"]:
        if not d:
            return None
        return HostMetrics(_f(d, "cpuPercent"), _i(d, "freeMemoryMb"), _i(d, "runningCalls"), _i(d, "freeDiskMb"))


@dataclass(frozen=True)
class HostInfo:
    id: str
    name: str
    kind: str
    os: str
    status: str
    processor_count: int
    architecture: str = ""
    agent_version: str = ""
    #: shell, files, desktop, docker, dotnet, node, python, ``pkg:winget`` …
    capabilities: List[str] = field(default_factory=list)
    metrics: Optional[HostMetrics] = None
    installed_via: Optional[str] = None

    @staticmethod
    def from_wire(d: Json) -> "HostInfo":
        m = d.get("metrics")
        return HostInfo(_s(d, "id"), _s(d, "name"), _s(d, "kind"), _s(d, "os"), _s(d, "status"), _i(d, "processorCount"),
                        _s(d, "architecture"), _s(d, "agentVersion"), _ls(d, "capabilities"),
                        HostMetrics.from_wire(m if isinstance(m, dict) else None), _os(d, "installedVia"))


@dataclass(frozen=True)
class EnrollmentToken:
    """One-time token for ``marbots-host enroll`` (valid until ``expires_at``)."""

    token: str
    expires_at: str
    enroll_command: str

    @staticmethod
    def from_wire(d: Json) -> "EnrollmentToken":
        return EnrollmentToken(_s(d, "token"), _s(d, "expiresAt"), _s(d, "enrollCommand"))


@dataclass(frozen=True)
class BootstrapResult:
    success: bool
    host_id: Optional[str]
    log: List[str]
    error: Optional[str] = None

    @staticmethod
    def from_wire(d: Json) -> "BootstrapResult":
        return BootstrapResult(_b(d, "success"), _os(d, "hostId"), _ls(d, "log"), _os(d, "error"))


@dataclass(frozen=True)
class SkillStats:
    name: str
    version: str
    loads: int
    successes: int
    failures: int

    @property
    def runs(self) -> int:
        return self.successes + self.failures

    @property
    def success_rate(self) -> float:
        return self.successes / self.runs if self.runs else 0.0

    @staticmethod
    def from_wire(d: Optional[Json]) -> Optional["SkillStats"]:
        if not d:
            return None
        return SkillStats(_s(d, "name"), _s(d, "version"), _i(d, "loads"), _i(d, "successes"), _i(d, "failures"))


@dataclass(frozen=True)
class SkillEvaluation:
    """Learning evaluation of one skill: outcomes of its current version and a verdict."""

    name: str
    version: str
    pending: bool
    current: SkillStats
    verdict: SkillVerdict
    reason: str
    previous_version: Optional[str] = None
    previous: Optional[SkillStats] = None

    @property
    def can_rollback(self) -> bool:
        return self.previous_version is not None

    @staticmethod
    def from_wire(d: Json) -> "SkillEvaluation":
        cur = d.get("current")
        prev = d.get("previous")
        current = SkillStats.from_wire(cur if isinstance(cur, dict) else None) or SkillStats(_s(d, "name"), _s(d, "version"), 0, 0, 0)
        return SkillEvaluation(_s(d, "name"), _s(d, "version"), _b(d, "pending"), current,
                               cast(SkillVerdict, _s(d, "verdict", "CollectingEvidence")), _s(d, "reason"),
                               _os(d, "previousVersion"), SkillStats.from_wire(prev if isinstance(prev, dict) else None))


@dataclass(frozen=True)
class SystemInfo:
    product: str
    version: str
    credits: str
    credits_en: str
    model_configured: bool

    @staticmethod
    def from_wire(d: Json) -> "SystemInfo":
        return SystemInfo(_s(d, "product"), _s(d, "version"), _s(d, "credits"), _s(d, "creditsEn"), _b(d, "modelConfigured"))
