# DotCode SDK — Python

Embed the [DotCode](https://github.com/DotNetVibeCoderz/Vibe_Dev) coding agent in your application and drive it with **any LLM** (Anthropic, OpenAI, Azure OpenAI, Gemini, DeepSeek, Ollama, OpenAI-compatible). The SDK talks JSON-RPC to a `dotcode serve` process.

*Built by Gravicode Studios, led by Kang Fadhil.* · 🇮🇩 Dokumentasi Bahasa Indonesia: [docs/id/sdk.md](https://github.com/DotNetVibeCoderz/Vibe_Dev/blob/main/DotCode/docs/id/sdk.md)

## Install

```bash
pip install dotcode-sdk
```

The SDK needs the `dotcode` CLI: put it on `PATH` or set `DOTCODE_CLI_PATH` (a path to `dotcode.dll` is started with `dotnet`). Configure providers with environment variables (`ANTHROPIC_API_KEY`, `OPENAI_API_KEY`, `DEEPSEEK_API_KEY`, …), `~/.dotcode/settings.json`, or per session with the `settings` option.

## Usage

```python
import asyncio
from dotcode_sdk import DotCodeClient, tool

@tool("now", "Current time")
def now(args):
    import datetime
    return datetime.datetime.now().isoformat()

async def main():
    async with DotCodeClient() as client:          # spawns `dotcode serve`
        session = await client.create_session(model="deepseek:deepseek-v4-flash", tools=[now],
                                              on_permission_request=lambda r: {"decision": "allow"})
        async for e in session.stream("What time is it? Then list the files here."):
            if e["type"] == "assistant.text.delta":
                print(e["text"], end="")

asyncio.run(main())
```

## Features

- Sessions with streaming events (text deltas, tool calls, diffs, todos, usage, cost)
- **Custom tools** implemented in your app
- **Permission handler** — sessions are deny-by-default without one; decisions: `allow`, `allow_always`, `allow_session`, `deny` (with feedback)
- Question (AskUserQuestion) and plan-review handlers
- MCP servers, allowed/disallowed tool rules, permission modes, system prompt overrides, model switching, compaction, transcripts, resume/fork

Full guide: [docs/en/sdk.md](https://github.com/DotNetVibeCoderz/Vibe_Dev/blob/main/DotCode/docs/en/sdk.md) · Protocol: [schema/protocol.schema.json](https://github.com/DotNetVibeCoderz/Vibe_Dev/blob/main/DotCode/schema/protocol.schema.json)

License: MIT
