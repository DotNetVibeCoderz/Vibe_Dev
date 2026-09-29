# DotCode SDK — TypeScript

Embed the [DotCode](https://github.com/DotNetVibeCoderz/Vibe_Dev) coding agent in your application and drive it with **any LLM** (Anthropic, OpenAI, Azure OpenAI, Gemini, DeepSeek, Ollama, OpenAI-compatible). The SDK talks JSON-RPC to a `dotcode serve` process.

*Built by Gravicode Studios, led by Kang Fadhil.* · 🇮🇩 Dokumentasi Bahasa Indonesia: [docs/id/sdk.md](https://github.com/DotNetVibeCoderz/Vibe_Dev/blob/main/DotCode/docs/id/sdk.md)

## Install

```bash
npm install dotcode-sdk
```

The SDK needs the `dotcode` CLI: put it on `PATH` or set `DOTCODE_CLI_PATH` (a path to `dotcode.dll` is started with `dotnet`). Configure providers with environment variables (`ANTHROPIC_API_KEY`, `OPENAI_API_KEY`, `DEEPSEEK_API_KEY`, …), `~/.dotcode/settings.json`, or per session with the typed `providers` option.

## Usage

```ts
import { DotCodeClient, defineTool, s, approveAll } from "dotcode-sdk";

const getWeather = defineTool("get_weather", {
  description: "Weather for a city",
  parameters: s.object({                        // typed schema: handler args are inferred from it
    city: s.string().describe("City name"),
    unit: s.enum(["celsius", "fahrenheit"]).optional(),
  }),
  handler: async ({ city, unit }) => `${city}: 24°${unit === "fahrenheit" ? "F" : "C"}`,
});

await using client = new DotCodeClient();       // spawns `dotcode serve` (DOTCODE_CLI_PATH or PATH)
await client.start();
await using session = await client.createSession({
  model: "anthropic:claude-sonnet-4-5",         // any configured provider:model
  tools: [getWeather],
  onPermissionRequest: approveAll,              // or (req) => PermissionDecision.reject("…")
});

session.on("assistant.text.delta", (e) => process.stdout.write(e.text));   // typed per event type
session.on("tool.completed", (e) => console.log(`
✔ ${e.name}: ${e.summary}`));
const result = await session.sendAndWait({ prompt: "What's the weather in Bogor?" });
console.log(result.costUsd);
```

Zod 4 schemas work in place of `s.object(...)`. Options, events, permission decisions, built-in tool names
(`BuiltinTool.Read`), permission rules (`"Bash(npm test:*)"`) and providers are all typed, so typos are compile errors.

## API

| | |
|---|---|
| `DotCodeClient` | `start()`, `createSession(config)`, `resumeSession(id, config)`, `listSessions()`, `listModels()`, `ping()`, `stop()`, `forceStop()` |
| `DotCodeSession` | `send(message)` (returns once dispatched), `sendAndWait(message, timeoutMs?)`, `stream(message)`, `on(type, handler)` / `on(handler)`, `abort()`, `setModel()`, `setPermissionMode()`, `setReasoningEffort()`, `compact()`, `clear()`, `getMessages()`, `listTools()`, `disconnect()` |
| Tools | `defineTool(name, { description, parameters, handler, readOnly })`; handlers get `(args, invocation)` and return a string, a `ToolResultObject` or any JSON value |
| Permissions | `approveAll`, `rejectAll`, `PermissionDecision.approveOnce() / approveForSession() / approveAlways(rule) / reject(feedback)`; sessions are deny-by-default without a handler |
| Other handlers | `onUserInputRequest` (AskUserQuestion), `onExitPlanMode` (plan review), `onEvent` |
| Config | `model`, `fallbackModel`, `workingDirectory`, `permissionMode`, `reasoningEffort`, `systemMessage`, `availableTools`, `allowedTools`, `excludedTools`, `mcpServers`, `providers` (BYOK), `maxTurns`, `persistSession`, `worktree`, `disableMcp` |

Full guide: [docs/en/sdk.md](https://github.com/DotNetVibeCoderz/Vibe_Dev/blob/main/DotCode/docs/en/sdk.md) · Protocol: [schema/protocol.schema.json](https://github.com/DotNetVibeCoderz/Vibe_Dev/blob/main/DotCode/schema/protocol.schema.json)

License: MIT
