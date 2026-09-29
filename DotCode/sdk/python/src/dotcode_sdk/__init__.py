"""DotCode SDK for Python.

Embed the DotCode multi-LLM coding agent (Anthropic, OpenAI, Azure, Gemini, DeepSeek, Ollama, …) in your
Python application. The SDK talks JSON-RPC to a ``dotcode serve`` child process.

Built by Gravicode Studios, led by Kang Fadhil.

Example::

    import asyncio
    from dataclasses import dataclass
    from typing import Annotated
    from dotcode_sdk import DotCodeClient, PermissionHandler, define_tool
    from dotcode_sdk.events import AssistantTextDeltaEvent

    @dataclass
    class WeatherParams:
        city: Annotated[str, "City name"]

    @define_tool(description="Weather for a city")
    async def get_weather(params: WeatherParams) -> str:
        return f"{params.city}: sunny"

    async def main():
        async with DotCodeClient() as client:
            async with await client.create_session(model="openai:gpt-5", tools=[get_weather],
                                                   on_permission_request=PermissionHandler.approve_all) as session:
                session.on(AssistantTextDeltaEvent, lambda e: print(e.text, end=""))
                result = await session.send_and_wait("Weather in Bogor?")
                print(result.cost_usd)

    asyncio.run(main())
"""

from .client import PROTOCOL_VERSION, DotCodeClient, DotCodeError, DotCodeSession
from .events import (
    AssistantMessageEvent, AssistantTextDeltaEvent, AssistantThinkingDeltaEvent, ContextCompactedEvent, ErrorEvent,
    ModeChangedEvent, ModelChangedEvent, ModelFallbackEvent, NoticeEvent, RetryEvent, SessionEvent,
    SubagentCompletedEvent, SubagentStartedEvent, TodoItem, TodoUpdatedEvent, ToolCompletedEvent, ToolProgressEvent,
    ToolStartedEvent, TurnCompletedEvent, UnknownEvent, Usage, UsageUpdatedEvent, UserMessageEvent,
)
from .schema import ToolArgumentError, schema_for
from .tools import BinaryResult, Tool, ToolInvocation, ToolResult, define_tool
from .types import (
    Attachment, BuiltinTool, ExitPlanModeRequest, ExitPlanModeResult, FileAttachment, ImageAttachment, Invocation,
    McpHttpServer, McpStdioServer, ModelInfo, PermissionDecision, PermissionDecisionApproveAlways,
    PermissionDecisionApproveForSession, PermissionDecisionApproveOnce, PermissionDecisionReject, PermissionHandler,
    PermissionMode, PermissionRequest, ProviderConfig, QuestionOption, ReasoningEffort, SendResult, SessionMetadata,
    SystemMessageConfig, ToolInfo, UserInputRequest, UserQuestion, UserQuestionAnswer,
)

__all__ = [
    "PROTOCOL_VERSION", "DotCodeClient", "DotCodeError", "DotCodeSession",
    "define_tool", "Tool", "ToolInvocation", "ToolResult", "BinaryResult", "ToolArgumentError", "schema_for",
    "PermissionHandler", "PermissionRequest", "PermissionDecision", "PermissionDecisionApproveOnce",
    "PermissionDecisionApproveForSession", "PermissionDecisionApproveAlways", "PermissionDecisionReject",
    "UserInputRequest", "UserQuestion", "QuestionOption", "UserQuestionAnswer", "ExitPlanModeRequest",
    "ExitPlanModeResult", "Invocation",
    "BuiltinTool", "PermissionMode", "ReasoningEffort", "ProviderConfig", "McpStdioServer", "McpHttpServer",
    "SystemMessageConfig", "Attachment", "FileAttachment", "ImageAttachment",
    "SendResult", "SessionMetadata", "ModelInfo", "ToolInfo", "Usage", "TodoItem",
    "SessionEvent", "UserMessageEvent", "AssistantTextDeltaEvent", "AssistantThinkingDeltaEvent",
    "AssistantMessageEvent", "ToolStartedEvent", "ToolProgressEvent", "ToolCompletedEvent", "TodoUpdatedEvent",
    "SubagentStartedEvent", "SubagentCompletedEvent", "ContextCompactedEvent", "UsageUpdatedEvent",
    "ModelChangedEvent", "ModelFallbackEvent", "RetryEvent", "NoticeEvent", "ErrorEvent", "ModeChangedEvent",
    "TurnCompletedEvent", "UnknownEvent",
]
__version__ = "0.2.0"
