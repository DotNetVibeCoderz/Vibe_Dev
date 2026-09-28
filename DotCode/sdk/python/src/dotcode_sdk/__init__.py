"""DotCode SDK for Python.

Embed the DotCode multi-LLM coding agent (Anthropic, OpenAI, Azure, Gemini, DeepSeek, Ollama, …) in your
Python application. The SDK talks JSON-RPC to a ``dotcode serve`` child process.

Built by Gravicode Studios, led by Kang Fadhil.

Example::

    import asyncio
    from dotcode_sdk import DotCodeClient

    async def main():
        async with DotCodeClient() as client:
            session = await client.create_session(model="openai:gpt-5")
            result = await session.send("Summarize README.md")
            print(result["result"])

    asyncio.run(main())
"""

from .client import DotCodeClient, Session, DotCodeError, tool, PROTOCOL_VERSION
from .types import (
    AgentEvent,
    PermissionDecision,
    PermissionRequest,
    SendResult,
    Tool,
    UserQuestion,
)

__all__ = [
    "DotCodeClient",
    "Session",
    "DotCodeError",
    "tool",
    "PROTOCOL_VERSION",
    "AgentEvent",
    "PermissionDecision",
    "PermissionRequest",
    "SendResult",
    "Tool",
    "UserQuestion",
]
__version__ = "0.1.0"
