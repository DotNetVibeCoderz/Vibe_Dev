# DotCode SDK — Python

Embed the [DotCode](https://github.com/DotNetVibeCoderz/Vibe_Dev) coding agent in your application and drive it with **any LLM** (Anthropic, OpenAI, Azure OpenAI, Gemini, DeepSeek, Ollama, OpenAI-compatible). The SDK talks JSON-RPC to a `dotcode serve` process.

*Built by Gravicode Studios, led by Kang Fadhil.* · 🇮🇩 Dokumentasi Bahasa Indonesia: [docs/id/sdk.md](https://github.com/DotNetVibeCoderz/Vibe_Dev/blob/main/DotCode/docs/id/sdk.md)

## Install

```bash
pip install dotcode-sdk
```

The SDK needs the `dotcode` CLI: put it on `PATH` or set `DOTCODE_CLI_PATH` (a path to `dotcode.dll` is started with `dotnet`). Configure providers with environment variables (`ANTHROPIC_API_KEY`, `OPENAI_API_KEY`, `DEEPSEEK_API_KEY`, …), `~/.dotcode/settings.json`, or per session with the typed `providers` option.

## Usage

```python
import asyncio
from dataclasses import dataclass
from typing import Annotated
from dotcode_sdk import DotCodeClient, PermissionHandler, ToolCompletedEvent, AssistantTextDeltaEvent, define_tool

@dataclass
class WeatherParams:                       # parameters are a class, never a hand-written JSON schema
    city: Annotated[str, "City name"]

@define_tool(description="Weather for a city")
async def get_weather(params: WeatherParams) -> str:
    return f"{params.city}: 24°C"          # a typo like params.cty is flagged by mypy/pyright

async def main():
    async with DotCodeClient() as client:  # spawns `dotcode serve`
        async with await client.create_session(
            model="deepseek:deepseek-v4-flash",
            tools=[get_weather],
            on_permission_request=PermissionHandler.approve_all,
        ) as session:
            session.on(AssistantTextDeltaEvent, lambda e: print(e.text, end=""))
            session.on(ToolCompletedEvent, lambda e: print(f"
✔ {e.name}: {e.summary}"))
            result = await session.send_and_wait("What's the weather in Bogor?")
            print(result.cost_usd)

asyncio.run(main())
```

Parameter classes can be `@dataclass`es, `TypedDict`s or pydantic models (`pip install dotcode-sdk[pydantic]`).
Arguments are validated and converted before your handler runs; mismatches are reported back to the model.
Options, events (`match event: case ToolCompletedEvent(name=n): ...`), permission decisions, built-in tool names
(`BuiltinTool.READ`, `BuiltinTool.BASH.rule("npm test:*")`) and providers (`ProviderConfig(type="ollama")`) are
all typed, and the package ships `py.typed`.

## API

| | |
|---|---|
| `DotCodeClient` | `start()`, `create_session(...)`, `resume_session(id, fork=..., ...)`, `list_sessions()`, `list_models()`, `ping()`, `stop()`, `force_stop()` |
| `DotCodeSession` | `send(prompt)` (returns once dispatched), `send_and_wait(prompt, timeout=)`, `stream(prompt)`, `on(handler)` / `on(EventClass, handler)`, `abort()`, `set_model()`, `set_permission_mode()`, `set_reasoning_effort()`, `compact()`, `clear()`, `get_messages()`, `list_tools()`, `disconnect()` |
| Tools | `@define_tool(description=..., read_only=...)`; handlers take `(params)`, `(params, invocation)`, `(invocation)` or nothing and return a string, a `ToolResult` or any JSON value |
| Permissions | `PermissionHandler.approve_all` / `reject_all`, or return `PermissionDecisionApproveOnce()`, `PermissionDecisionApproveForSession()`, `PermissionDecisionApproveAlways(rule)`, `PermissionDecisionReject(feedback)`; sessions are deny-by-default without a handler |
| Other handlers | `on_user_input_request` (AskUserQuestion), `on_exit_plan_mode` (plan review), `on_event` |
| Options | `model`, `fallback_model`, `working_directory`, `permission_mode`, `reasoning_effort`, `system_message`, `available_tools`, `allowed_tools`, `excluded_tools`, `mcp_servers` (`McpStdioServer` / `McpHttpServer`), `providers` (BYOK), `max_turns`, `persist_session`, `worktree`, `disable_mcp` |

Full guide: [docs/en/sdk.md](https://github.com/DotNetVibeCoderz/Vibe_Dev/blob/main/DotCode/docs/en/sdk.md) · Protocol: [schema/protocol.schema.json](https://github.com/DotNetVibeCoderz/Vibe_Dev/blob/main/DotCode/schema/protocol.schema.json)

License: MIT
