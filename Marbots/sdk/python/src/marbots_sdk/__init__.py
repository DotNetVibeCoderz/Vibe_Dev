"""Marbots SDK for Python.

Typed client for Marbots, the multi-agent collaboration platform (Boss Man orchestration, skills, MCP, A2A).
Zero dependencies; every response is a typed dataclass and every name (kernel packs, permission profiles,
event types, model settings) is an enum or helper so type checkers catch typos.

Built by Gravicode Studios, led by Kang Fadhil.

Example::

    from marbots_sdk import MarbotsClient, BotSpec, KernelPack, PermissionProfile, ModelRef, EventType

    mb = MarbotsClient("http://localhost:5170")
    bot = mb.bots.create(BotSpec(name="Sari", role="UX designer",
                                 kernel_functions=[KernelPack.FILES, KernelPack.WEB],
                                 permission_profile=PermissionProfile.WORKSPACE_WRITE,
                                 model=ModelRef.of("azure", "gpt-5.6-luna")))
    thread = mb.threads.create(bot.id)
    result = mb.threads.send(thread.id, "Draft a landing page wireframe", wait=True)
    print(result.task.model, result.text)
"""

from .client import (
    AgentHostsApi, ApprovalsApi, BotsApi, EventsApi, MarbotsClient, MarbotsError, McpApi, MemoryApi, ModelsApi, SchedulesApi,
    SkillsApi, TasksApi, TemplatesApi, ThreadsApi,
)
from .types import (
    AUTO_HOST, BOSS_MAN, LOCAL_HOST, TERMINAL_STATES, AgentEvent, BootstrapResult, ContainerProfile, EnrollmentToken,
    HostMetrics, SkillEvaluation, SkillStats, SkillVerdict, ApprovalRequest, ApprovalScope, ApprovalState, AutoLearnMode, Bot,
    BotModelInfo, BotSpec, BotStatus, BotTemplate, ChatMessage, ChatThread, EventType, HostInfo, KernelPack,
    McpServer, MemoryKind, MemoryRecord, ModelCatalog, ModelProfileInfo, ModelRef, PermissionProfile, ScheduleJob,
    ScheduleSpec, SendResult, SkillInfo, SystemInfo, TaskRecord, TaskState, ToolCall, WorkspaceFile,
)

__all__ = [
    "MarbotsClient", "MarbotsError", "BotsApi", "TemplatesApi", "ModelsApi", "ThreadsApi", "TasksApi", "ApprovalsApi",
    "SkillsApi", "McpApi", "SchedulesApi", "MemoryApi", "EventsApi", "AgentHostsApi",
    "LOCAL_HOST", "AUTO_HOST", "ContainerProfile", "HostMetrics", "EnrollmentToken", "BootstrapResult",
    "SkillStats", "SkillEvaluation", "SkillVerdict",
    "BOSS_MAN", "TERMINAL_STATES", "KernelPack", "PermissionProfile", "EventType", "ModelRef",
    "BotStatus", "TaskState", "AutoLearnMode", "ApprovalScope", "ApprovalState", "MemoryKind",
    "Bot", "BotSpec", "BotTemplate", "BotModelInfo", "ModelCatalog", "ModelProfileInfo", "ChatThread", "ChatMessage",
    "ToolCall", "TaskRecord", "SendResult", "WorkspaceFile", "ApprovalRequest", "AgentEvent", "MemoryRecord",
    "SkillInfo", "McpServer", "ScheduleSpec", "ScheduleJob", "HostInfo", "SystemInfo",
]
__version__ = "0.2.0"
