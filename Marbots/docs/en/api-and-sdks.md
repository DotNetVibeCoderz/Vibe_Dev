# API, A2A, SDKs and CLI

[English](../en/api-and-sdks.md) · [Bahasa Indonesia](../id/api-and-sdks.md)

## REST API

Base path `/api/v1`. The OpenAPI document is at `/openapi/v1.json`. Errors use RFC 9457 Problem Details.

| Area | Endpoints |
|---|---|
| System | `GET /system`, `POST /system/providers` |
| Bots | `GET/POST /bots`, `GET/PUT/DELETE /bots/{id}`, `POST /bots/{id}/pause\|resume`, `POST /bots/from-template/{templateId}`, `GET /bots/{id}/export`, `POST /bots/import` |
| Templates | `GET /templates?q=&category=`, `GET/POST/PUT/DELETE /templates/{id}` |
| Threads | `GET/POST /threads`, `GET/PATCH/DELETE /threads/{id}`, `GET/POST /threads/{id}/messages`, `POST /threads/{id}/reset\|fork`, `GET /threads/{id}/export`, `GET /threads/{id}/events` (SSE), `GET /threads/{id}/files[/{path}]` |
| Tasks | `GET /tasks`, `GET /tasks/{id}`, `GET /tasks/{id}/transcript`, `POST /tasks/{id}/cancel\|retry` |
| Approvals | `GET /approvals?state=pending`, `POST /approvals/{id}/approve` `{scope}`, `POST /approvals/{id}/reject` |
| Skills | `GET /skills`, `POST /skills`, `POST /skills/install`, `DELETE /skills/{name}`, `POST /skills/{name}/approve\|reject` |
| MCP | `GET/POST /mcp`, `GET /mcp/status`, `POST /mcp/{id}/install\|uninstall\|test`, `DELETE /mcp/{id}` |
| Schedules | `GET/POST /schedules`, `DELETE /schedules/{id}`, `POST /schedules/{id}/run` |
| Memory | `GET /memory/{owner}`, `GET /memory/{owner}/search?q=`, `POST /memory`, `DELETE /memory/item/{id}` |
| Observe | `GET /hosts`, `GET /usage`, `GET /events` (SSE), `GET /events/recent` |

### Send a message and wait

```bash
THREAD=$(curl -s -X POST localhost:5170/api/v1/threads -H 'Content-Type: application/json' \
  -d '{"botId":"boss-man"}' | jq -r .id)
curl -s -X POST localhost:5170/api/v1/threads/$THREAD/messages -H 'Content-Type: application/json' \
  -d '{"text":"Write hello.py that prints the date","wait":true,"timeoutSeconds":600}' | jq .reply.content
```

Without `"wait": true` the call returns `202 Accepted` with the task immediately. Follow progress with SSE:

```bash
curl -N localhost:5170/api/v1/threads/$THREAD/events
```

Events: `MessageAdded`, `TaskCreated`, `TaskStateChanged`, `TaskDelegated`, `TaskProgressed`, `AgentThinkingStarted`,
`ToolCallStarted`, `ToolCallCompleted`, `ApprovalRequested`, `ApprovalResolved`, `MemoryWritten`, `SkillLoaded`,
`ContextCompacted`, `AutoLearnCandidateCreated`, `ScheduleTriggered`, `TodoUpdated`, `BotCreated`, `BotUpdated`,
`BotDeleted`, `BotStateChanged`. Pass `?after=<eventId>` to replay missed events first.

## A2A (Agent2Agent, preview)

Every bot publishes an Agent Card and accepts JSON-RPC:

- `GET /.well-known/agent-card.json` — Boss Man's card
- `GET /a2a/{bot}/.well-known/agent-card.json` — any bot's card
- `POST /a2a/{bot}` — `message/send`, `tasks/get`, `tasks/cancel`

```bash
curl -s -X POST localhost:5170/a2a/wren -H 'Content-Type: application/json' -d '{
  "jsonrpc":"2.0","id":1,"method":"message/send",
  "params":{"message":{"role":"user","messageId":"m1","parts":[{"kind":"text","text":"What makes a good README?"}]}}}'
```

The A2A `contextId` maps to a Marbots thread, so follow-up messages continue the conversation. Streaming and push
notifications are on the roadmap.

## SDKs

| Language | Location | Install |
|---|---|---|
| .NET | `src/Marbots.Sdk` | `dotnet add package Marbots.Sdk` |
| Python | `sdk/python` | `pip install marbots-sdk` (zero dependencies, typed) |
| TypeScript / Node | `sdk/typescript` | `npm install @gravicode/marbots` |
| Go | `sdk/go` | `go get github.com/DotNetVibeCoderz/Vibe_Dev/Marbots/sdk/go` |
| Java 17+ | `sdk/java` | JitPack `com.github.DotNetVibeCoderz:Vibe_Dev:marbots-java-v0.1.0` |
| Rust | `sdk/rust` | `cargo add marbots-sdk` |

```csharp
using var mb = new MarbotsClient(new Uri("http://localhost:5170"));
var t = await mb.Threads.CreateAsync("boss-man");
var r = await mb.Threads.SendAsync(t.Id, "Plan a product launch", wait: true);
await foreach (var e in mb.Events.StreamAsync(t.Id)) Console.WriteLine(e.Type);
```

```python
from marbots_sdk import MarbotsClient
print(MarbotsClient().chat("atlas", "Top 3 AI news today, 1 line each"))
```

```ts
import { MarbotsClient } from "@gravicode/marbots";
console.log(await new MarbotsClient().chat("wren", "Write a haiku about marbles"));
```

```go
reply, _ := marbots.New("http://localhost:5170").Chat(ctx, marbots.BossMan, "Hello")
```

```java
String reply = MarbotsClient.create("http://localhost:5170").chat("boss-man", "Hello");
```

```rust
let reply = marbots_sdk::Client::new("http://localhost:5170").chat(marbots_sdk::BOSS_MAN, "Hello")?;
```

### Typed names

Every SDK exposes the platform's names as constants/enums, so a misspelling is a compile (or `mypy`) error, not a
runtime surprise: kernel packs (`KernelPack.Files`), permission profiles (`PermissionProfile.DeveloperSafe`), event
types (`EventType.TaskStateChanged`), task states, approval scopes, and model settings (`ModelRef.Default`,
`ModelRef.Of("azure", "gpt-5.6-luna")`). Each SDK ships a test that proves deliberate typos fail to compile and a
conformance test that drives a real server.

### What's new in 0.2.0 (all SDKs)

| Area | .NET | Python | TypeScript | Go | Java | Rust |
|---|---|---|---|---|---|---|
| Computers (agent hosts) | `client.Hosts` | `mb.agent_hosts` | `mb.agentHosts` | `mb.AgentHosts` | `mb.agentHosts()` | `mb.agent_hosts()` |
| Learning evaluation | `client.Skills.EvaluationsAsync/RollbackAsync/PromoteAsync` | `mb.skills.evaluations/rollback/promote` | `mb.skills.evaluations/rollback/promote` | `mb.Skills.Evaluations/Rollback/Promote` | `mb.skills().evaluations()/rollback()` | `mb.skills().evaluations()/rollback()` |
| Bot placement | `HostRef`, `Container` | `host_ref`, `container` | `hostRef`, `container` | `HostRef`, `Container` | `.hostRef()`, `.container()` | `.host_ref()`, `.container()` |
| New packs | `KernelPacks.Desktop/Subagents` | `KernelPack.DESKTOP/SUBAGENTS` | `KernelPack.Desktop/Subagents` | `KernelPackDesktop/Subagents` | `KernelPack.DESKTOP/SUBAGENTS` | `KernelPack::Desktop/Subagents` |

Each SDK keeps its compile-time typo checks and a conformance test against a real server for these calls.

## Per-bot models

```bash
curl -X PUT localhost:5170/api/v1/bots/atlas/model -H 'Content-Type: application/json' -d '{"model":"azure/gpt-5.6-luna"}'
curl -X PUT localhost:5170/api/v1/models/default -H 'Content-Type: application/json' -d '{"model":"azure/gpt-5-mini"}'
curl localhost:5170/api/v1/models        # default, choices, profiles
```

## CLI

```text
marbots status | bots | templates [q] | tasks | hosts | schedules
marbots bot inspect|hire|export|import|pause|resume|delete …
marbots chat <bot> [message]        # interactive when no message
marbots approvals | approve <id> [--session] | reject <id>
marbots approvals skip on|off|status   # dangerous: no approvals
marbots models | models default <provider/model> | bot model <bot> [model]
marbots skills [install <src>] | mcp [install <id>]
marbots logs [--thread <id>]
marbots theme set default|aurora|matrix|mono|high-contrast
```

Environment: `MARBOTS_URL`, `MARBOTS_API_KEY`, `NO_COLOR`.

---
*Marbots — Created by Gravicode Studios, led by Kang Fadhil.*
