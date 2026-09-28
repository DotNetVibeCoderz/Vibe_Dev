# SDK: embed the DotCode harness

> 🇮🇩 [Bahasa Indonesia](../id/sdk.md)

The same engine that powers the terminal UI is available to your applications. Every SDK is a thin JSON-RPC 2.0 client of `dotcode serve` (the pattern used by the GitHub Copilot SDK); the .NET SDK can also run the engine in-process.

```
Your app (.NET / TypeScript / Python / Go / Java)
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
| Java (17+) | `com.gravicode:dotcode-sdk` (Maven) | [`sdk/java`](../../sdk/java) |

All SDKs share the same concepts: **Client → Session → send / stream**, **custom tools** implemented by your app, a **permission handler** (sessions are deny-by-default without one), question and plan-review handlers, MCP servers, and per-session provider configuration (BYOK). They pass the same conformance scenarios (`tests/…SdkConformanceTests`, `sdk/*/test*`).

The spawned server is found via `cliPath`, `DOTCODE_CLI_PATH`, or `dotcode` on `PATH` (a `.dll` path is started with `dotnet`).

## .NET

```csharp
using DotCode.Abstractions;
using DotCode.Sdk;

await using var client = new DotCodeClient(new DotCodeClientOptions { Mode = ClientMode.InProcess }); // or Spawn / Connect
await using var session = await client.CreateSessionAsync(new SessionOptions
{
    Model = "azure:gpt-5-mini",
    Tools = [DotCodeTool.Create("get_exchange_rate", "Exchange rate between two currencies",
        """{"type":"object","properties":{"from":{"type":"string"},"to":{"type":"string"}},"required":["from","to"]}""",
        input => $"1 {input.GetString("from")} = 16,250 {input.GetString("to")}", readOnly: true)],
    OnPermissionRequest = (req, ct) => Task.FromResult(PermissionDecision.AllowOnce),
});

await foreach (var e in session.StreamAsync("How many IDR is 250 USD?"))
    if (e is AssistantTextDeltaEvent d) Console.Write(d.Text);
```

## TypeScript

```ts
import { DotCodeClient, tool } from "dotcode-sdk";

const client = new DotCodeClient();
const session = await client.createSession({
  model: "deepseek:deepseek-v4-flash",
  tools: [tool({
    name: "get_exchange_rate", description: "Exchange rate between two currencies",
    inputSchema: { type: "object", properties: { from: { type: "string" }, to: { type: "string" } }, required: ["from", "to"] },
    handler: ({ from, to }) => `1 ${from} = 16,250 ${to}`,
  })],
  onPermissionRequest: (req) => ({ decision: req.toolName === "Read" ? "allow" : "deny" }),
});
for await (const e of session.stream("How many IDR is 250 USD?"))
  if (e.type === "assistant.text.delta") process.stdout.write(e.text);
await client.close();
```

## Python

```python
import asyncio
from dotcode_sdk import DotCodeClient, tool

@tool("get_exchange_rate", "Exchange rate between two currencies",
      {"type": "object", "properties": {"from": {"type": "string"}, "to": {"type": "string"}}, "required": ["from", "to"]})
def rate(args):
    return f"1 {args['from']} = 16,250 {args['to']}"

async def main():
    async with DotCodeClient() as client:
        session = await client.create_session(model="azure:gpt-5-mini", tools=[rate],
                                              on_permission_request=lambda r: {"decision": "allow"})
        async for e in session.stream("How many IDR is 250 USD?"):
            if e["type"] == "assistant.text.delta":
                print(e["text"], end="")

asyncio.run(main())
```

## Go

```go
client, _ := dotcode.NewClient(ctx, dotcode.ClientOptions{})
defer client.Close()
session, _ := client.CreateSession(ctx, dotcode.SessionOptions{
    Model: "azure:gpt-5-mini",
    Tools: []dotcode.Tool{{Name: "get_exchange_rate", Description: "Exchange rate",
        InputSchema: map[string]any{"type": "object", "properties": map[string]any{"from": map[string]any{"type": "string"}, "to": map[string]any{"type": "string"}}},
        Handler: func(ctx context.Context, in json.RawMessage) (string, error) { return "1 USD = 16,250 IDR", nil }}},
    OnPermissionRequest: func(r dotcode.PermissionRequest) dotcode.PermissionDecision { return dotcode.Allow },
})
events, result := session.Stream(ctx, "How many IDR is 250 USD?")
for e := range events { if e.Type == "assistant.text.delta" { fmt.Print(e.Text) } }
<-result
```

## Java

```java
try (var client = DotCodeClient.start(new DotCodeClient.Options())) {
    var session = client.createSession(SessionOptions.builder()
            .model("azure:gpt-5-mini")
            .tool(new Tool("get_exchange_rate", "Exchange rate", Map.of("type", "object"), true, in -> "1 USD = 16,250 IDR"))
            .onPermissionRequest(r -> PermissionDecision.allow()));
    session.stream("How many IDR is 250 USD?", e -> { if ("assistant.text.delta".equals(e.type())) System.out.print(e.text()); });
}
```

Runnable versions of all five live in [`samples/sdk`](../../samples/sdk).

## Session options (all SDKs)

`model`, `fallbackModel`, `cwd`, `permissionMode`, `systemPrompt`, `appendSystemPrompt`, `allowedTools`, `disallowedTools`, `builtinTools` (restrict built-ins), `tools` (host tools), `mcpServers`, `settings` (inline settings JSON, e.g. `{"providers":{…}}` for BYOK), `maxTurns`, `effort`, `persistSession`, `noMcp`, plus `onPermissionRequest`, `onQuestion`, `onPlanReview`, `onEvent`.

Session methods: `send`, `stream`, `abort`, `setModel`, `setPermissionMode`, `compact`, `messages`, `close`. Client methods: `createSession`, `resumeSession`, `listModels`, `listSessions`, `close`.

## Protocol

The contract is [`schema/protocol.schema.json`](../../schema/protocol.schema.json) (OpenRPC). Summary:

| Client → server | Server → client |
|---|---|
| `initialize`, `ping`, `shutdown` | `session.event` notification (every engine event) |
| `session.create`, `session.resume`, `session.send`, `session.abort`, `session.close` | `permission.request` → `{decision, feedback?, rule?, updatedInput?}` |
| `session.setModel`, `session.setMode`, `session.setEffort`, `session.compact`, `session.clear` | `user.question` → `{answers}` |
| `session.messages`, `session.info`, `session.list`, `models.list`, `tools.list` | `plan.review` → `{approval}` · `tool.call` → `{content, isError?}` |

Transports: `dotcode serve` (stdio NDJSON), `dotcode serve --content-length` (LSP-style framing), `dotcode serve --port 8765 --token <secret>` (WebSocket on 127.0.0.1; send `Authorization: Bearer <secret>` or `?token=`).

Writing an SDK for another language needs only a JSON-RPC client: handshake with `initialize` (declare `capabilities.permissions`/`questions` to receive callbacks), create a session, send prompts and answer the four server requests.
