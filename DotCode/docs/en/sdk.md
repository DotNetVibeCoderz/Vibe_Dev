# SDK: embed the DotCode harness

> 🇮🇩 [Bahasa Indonesia](../id/sdk.md)

The same engine that powers the terminal UI is available to your applications. Every SDK is a thin JSON-RPC 2.0 client of `dotcode serve`; the .NET SDK can also run the engine in-process. The client APIs follow the conventions of the [GitHub Copilot SDK](https://github.com/github/copilot-sdk) in each language (`createSession`, `send` / `sendAndWait`, typed `on(...)` events, `defineTool`, `approveAll`).

```
Your app (.NET / TypeScript / Python / Go / Java / Rust)
     │ SDK client
     ▼ JSON-RPC 2.0 over stdio (NDJSON) or WebSocket
dotcode serve ─► Engine ─► any LLM provider
```

| Language | Package | Source |
|---|---|---|
| .NET | `DotCode.Sdk` (NuGet) | [`src/DotCode.Sdk`](../../src/DotCode.Sdk) |
| TypeScript / JavaScript | `dotcode-sdk` (npm) | [`sdk/typescript`](../../sdk/typescript) |
| Python | `dotcode-sdk` (PyPI) | [`sdk/python`](../../sdk/python) |
| Go | `github.com/DotNetVibeCoderz/Vibe_Dev/DotCode/sdk/go` | [`sdk/go`](../../sdk/go) |
| Java (17+) | `com.github.DotNetVibeCoderz:Vibe_Dev:dotcode-java-v0.2.0` (JitPack) | [`sdk/java`](../../sdk/java) |
| Rust | [`dotcode-sdk`](https://crates.io/crates/dotcode-sdk) (crates.io) | [`sdk/rust`](../../sdk/rust) |

The spawned server is found via `cliPath`, `DOTCODE_CLI_PATH`, or `dotcode` on `PATH` (a `.dll` path is started with `dotnet`).

## Typed by design

Nothing is written as free-form JSON or schema strings, so typos are caught by the compiler (or by mypy/pyright in Python):

- **Tool parameters are a type** — a schema builder or Zod (TS), a dataclass/TypedDict/pydantic model (Python), a struct (Go), a record (Java/C#), a `serde` + `JsonSchema` type (Rust). The JSON Schema is generated from it, the handler receives a typed value, and arguments are validated before the handler runs (mismatches go back to the model as a tool error).
- **Events are typed** — a discriminated union (TS), dataclasses (Python), `Data` structs for a type switch (Go), a sealed hierarchy (Java), an enum (Rust), records (C#); subscribe to one type with `on("tool.completed", …)` / `on(ToolCompletedEvent, …)`.
- **Options are typed** — permission modes, reasoning effort, built-in tool names (`BuiltinTool.Read`, `BuiltinTool.Bash.rule("npm test:*")`), providers (`ProviderConfig` with a typed `type`), MCP servers, system message, permission decisions.

A raw `settings` object remains as an escape hatch for rare settings; everything common has a typed option.

## Common API

| Concept | TypeScript | Python | Go | Java | Rust | .NET |
|---|---|---|---|---|---|---|
| Start | `client.start()` | `async with DotCodeClient()` | `client.Start(ctx)` | `client.start()` | `Client::start(opts)` | `client.StartAsync()` |
| Session | `createSession(config)` | `create_session(**options)` | `CreateSession(ctx, &SessionConfig{})` | `createSession(new SessionConfig())` | `create_session(SessionConfig)` | `CreateSessionAsync(new SessionConfig())` |
| Tool | `defineTool(name, {parameters, handler})` | `@define_tool(description=…)` | `DefineTool[T](name, desc, fn)` | `ToolDefinition.from(name, desc, Record.class, fn)` | `define_tool(name, desc, \|inv, p: P\| …)` | `DotCodeTool.DefineTool(name, desc, fn, json.Args)` |
| Send (returns once dispatched) | `send(msg)` | `send(prompt)` | `Send(ctx, msg)` | `send(msg)` | `send(msg)` | `SendAsync(msg)` |
| Send and wait | `sendAndWait(msg, timeoutMs?)` | `send_and_wait(prompt, timeout=)` | `SendAndWait(ctx, msg)` | `sendAndWait(msg[, Duration])` | `send_and_wait[_timeout]` | `SendAndWaitAsync(msg, timeout?)` |
| Events | `on("tool.completed", h)` | `on(ToolCompletedEvent, h)` | `On(func(SessionEvent))` + type switch | `on(ToolCompletedEvent.class, h)` | `on(\|e\| match &e.data …)` | `On<ToolCompletedEvent>(h)` |
| Approve all | `approveAll` | `PermissionHandler.approve_all` | `PermissionHandler.ApproveAll` | `PermissionHandler.APPROVE_ALL` | `.approve_all_permissions()` | `PermissionHandler.ApproveAll` |
| Close | `disconnect()` / `stop()` | `disconnect()` / `stop()` | `Disconnect()` / `Stop()` | `close()` | `disconnect()` / `stop()` | `DisposeAsync()` |

`send` starts a turn and returns immediately; the turn ends with a `turn.completed` event (failures arrive as an `error` event). `sendAndWait` returns the turn result (text, stop reason, usage, cost). TypeScript, Python, Rust and .NET also offer `stream(...)`, an iterator over one turn's events.

Other session methods: `abort`, `setModel`, `setPermissionMode`, `setReasoningEffort`, `compact`, `clear`, `getMessages`, `listTools`. Client methods: `resumeSession` (and fork), `listSessions`, `listModels`, `ping`, `forceStop`.

## .NET

```csharp
using System.ComponentModel;
using System.Text.Json.Serialization;
using DotCode.Abstractions;
using DotCode.Sdk;

var getRate = DotCodeTool.DefineTool("get_exchange_rate", "Exchange rate between two currencies",
    args => $"1 {args.From} = 16,250 {args.To}", ToolJson.Default.RateArgs);   // args.Form would not compile

await using var client = new DotCodeClient(new DotCodeClientOptions { Mode = ClientMode.InProcess }); // or Spawn / Connect
await using var session = await client.CreateSessionAsync(new SessionConfig
{
    Model = "azure:gpt-5-mini",
    Tools = [getRate],
    OnPermissionRequest = PermissionHandler.ApproveAll,
});
using var _ = session.On<AssistantTextDeltaEvent>(e => Console.Write(e.Text));
var result = await session.SendAndWaitAsync("How many IDR is 250 USD?");

public sealed record RateArgs([property: Description("ISO code, e.g. USD")] string From, string To);

[JsonSerializable(typeof(RateArgs))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
partial class ToolJson : JsonSerializerContext;
```

The `JsonTypeInfo` overloads are NativeAOT-safe; apps that use reflection can call `DotCodeTool.DefineTool<RateArgs>(name, description, handler)` without a context.

## TypeScript

```ts
import { DotCodeClient, defineTool, s, approveAll } from "dotcode-sdk";

const getRate = defineTool("get_exchange_rate", {
  description: "Exchange rate between two currencies",
  parameters: s.object({ from: s.string().describe("ISO code, e.g. USD"), to: s.string() }),   // or a Zod 4 schema
  handler: ({ from, to }) => `1 ${from} = 16,250 ${to}`,
});

const client = new DotCodeClient();
await client.start();
const session = await client.createSession({ model: "deepseek:deepseek-v4-flash", tools: [getRate], onPermissionRequest: approveAll });
session.on("assistant.text.delta", (e) => process.stdout.write(e.text));
const result = await session.sendAndWait({ prompt: "How many IDR is 250 USD?" });
await client.stop();
```

## Python

```python
import asyncio
from dataclasses import dataclass
from typing import Annotated
from dotcode_sdk import AssistantTextDeltaEvent, DotCodeClient, PermissionHandler, define_tool

@dataclass
class RateParams:
    from_currency: Annotated[str, "ISO code, e.g. USD"]
    to_currency: str

@define_tool(description="Exchange rate between two currencies")
def get_rate(params: RateParams) -> str:
    return f"1 {params.from_currency} = 16,250 {params.to_currency}"

async def main():
    async with DotCodeClient() as client:
        async with await client.create_session(model="azure:gpt-5-mini", tools=[get_rate],
                                               on_permission_request=PermissionHandler.approve_all) as session:
            session.on(AssistantTextDeltaEvent, lambda e: print(e.text, end=""))
            result = await session.send_and_wait("How many IDR is 250 USD?")

asyncio.run(main())
```

## Go

```go
type rateParams struct {
    From string `json:"from" jsonschema:"ISO code, e.g. USD"`
    To   string `json:"to"`
}
getRate := dotcode.DefineTool("get_exchange_rate", "Exchange rate between two currencies",
    func(p rateParams, _ dotcode.ToolInvocation) (any, error) { return "1 " + p.From + " = 16,250 " + p.To, nil })

client := dotcode.NewClient(nil)
if err := client.Start(ctx); err != nil { log.Fatal(err) }
defer client.Stop()
session, _ := client.CreateSession(ctx, &dotcode.SessionConfig{
    Model: "azure:gpt-5-mini", Tools: []dotcode.Tool{getRate}, OnPermissionRequest: dotcode.PermissionHandler.ApproveAll,
})
session.On(func(e dotcode.SessionEvent) {
    if d, ok := e.Data.(*dotcode.AssistantTextDeltaData); ok { fmt.Print(d.Text) }
})
result, _ := session.SendAndWait(ctx, dotcode.MessageOptions{Prompt: "How many IDR is 250 USD?"})
```

## Java

```java
record RateParams(@ToolParam("ISO code, e.g. USD") String from, String to) {}

var getRate = ToolDefinition.from("get_exchange_rate", "Exchange rate between two currencies", RateParams.class,
        p -> "1 " + p.from() + " = 16,250 " + p.to());

try (var client = new DotCodeClient()) {
    client.start().get();
    var session = client.createSession(new SessionConfig()
            .setModel("azure:gpt-5-mini")
            .addTool(getRate)
            .setOnPermissionRequest(PermissionHandler.APPROVE_ALL)).get();
    session.on(SessionEvent.AssistantTextDeltaEvent.class, e -> System.out.print(e.text()));
    SendResult result = session.sendAndWait(new MessageOptions("How many IDR is 250 USD?")).get();
}
```

## Rust

Synchronous and thread-based (no async runtime needed; use `spawn_blocking` from async code).

```rust
#[derive(Deserialize, JsonSchema)]
struct RateParams {
    /// ISO code, e.g. USD
    from: String,
    to: String,
}

let get_rate = define_tool("get_exchange_rate", "Exchange rate between two currencies", |_inv, p: RateParams| {
    Ok::<_, String>(format!("1 {} = 16,250 {}", p.from, p.to))
});
let client = Client::start(ClientOptions::default())?;
let session = client.create_session(
    SessionConfig::default().with_model("azure:gpt-5-mini").with_tools([get_rate]).approve_all_permissions(),
)?;
let _sub = session.on(|e| if let SessionEventData::AssistantTextDelta { text } = &e.data { print!("{text}") });
let result = session.send_and_wait("How many IDR is 250 USD?")?;
```

Runnable versions of all six live in [`samples/sdk`](../../samples/sdk).

## Session configuration (all SDKs)

`model`, `fallbackModel`, `workingDirectory`, `permissionMode`, `reasoningEffort`, `systemMessage` (`append` or `replace`), `tools` (your tools), `availableTools` (restrict built-ins), `allowedTools` (pre-approved rules), `excludedTools` (removed tools / denied rules), `mcpServers` (stdio / http / sse), `providers` (named providers for BYOK, referenced as `"<name>:<model>"`), `maxTurns`, `persistSession`, `worktree` (`true` or a name — run in a git worktree, see [worktrees](worktrees.md)), `disableMcp`, `settings` (raw, advanced); handlers `onPermissionRequest`, `onUserInputRequest` (AskUserQuestion), `onExitPlanMode` (plan review) and `onEvent`. Names follow each language's conventions (`working_directory` in Python, `WorkingDirectory` in Go/.NET, `setWorkingDirectory` in Java, `with_working_directory` in Rust).

Sessions are **deny-by-default** without `onPermissionRequest`. Decisions: approve once, approve for the session, approve always (persist a rule), or reject with feedback for the model. Custom tools never prompt; mark them read-only to allow them in plan mode.

## Protocol

The contract is [`schema/protocol.schema.json`](../../schema/protocol.schema.json) (OpenRPC). Summary:

| Client → server | Server → client |
|---|---|
| `initialize`, `ping`, `shutdown` | `session.event` notification (every engine event) |
| `session.create`, `session.resume`, `session.send`, `session.abort`, `session.close` | `permission.request` → `{decision, feedback?, rule?, updatedInput?}` |
| `session.setModel`, `session.setMode`, `session.setEffort`, `session.compact`, `session.clear` | `user.question` → `{answers}` |
| `session.messages`, `session.info`, `session.list`, `models.list`, `tools.list` | `plan.review` → `{approval, feedback?}` · `tool.call` → `{content, isError?}` |

Transports: `dotcode serve` (stdio NDJSON), `dotcode serve --content-length` (LSP-style framing), `dotcode serve --port 8765 --token <secret>` (WebSocket on 127.0.0.1; send `Authorization: Bearer <secret>` or `?token=`).

Writing an SDK for another language needs only a JSON-RPC client: handshake with `initialize` (declare `capabilities.permissions`/`questions` to receive callbacks), create a session, send prompts and answer the four server requests.

## Tests

Every SDK passes the same conformance scenarios against a real `dotcode serve` with the offline scripted model: a typed custom tool with streamed events, a permission handler with `send` + `turn.completed`, and invalid tool arguments reported to the model. The TypeScript and Python suites also check at compile time that typos are rejected (`sdk/typescript/test/typecheck.ts`, `sdk/python/tests/typecheck` with mypy).
