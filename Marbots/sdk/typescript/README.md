# @gravicode/marbots

Typed TypeScript / JavaScript SDK for [Marbots](https://github.com/DotNetVibeCoderz/Vibe_Dev/tree/main/Marbots), the
multi-agent collaboration platform. Kernel packs, permission profiles, event types, task states and model settings are
typed constants and literal unions, so typos are **compile errors** (see `test/typecheck.ts`).

```bash
npm install @gravicode/marbots
```

```ts
import { MarbotsClient, KernelPack, PermissionProfile, ModelRef, EventType, isTaskFinished } from "@gravicode/marbots";

const mb = new MarbotsClient({ baseUrl: "http://localhost:5170" });   // apiKey if the server requires one

// Every bot can run on its own model; ModelRef.Default follows the workspace default.
const sari = await mb.bots.create({
  name: "Sari", role: "UX designer",
  kernelFunctions: [KernelPack.Files, KernelPack.Web],
  permissionProfile: PermissionProfile.WorkspaceWrite,
  model: ModelRef.of("azure", "gpt-5.6-luna"),
});
await mb.bots.setModel("atlas", ModelRef.Default);
console.log((await mb.models.list()).default);                       // e.g. azure/gpt-5-mini

const thread = await mb.threads.create(sari.id);
await mb.threads.send(thread.id, "Sketch a landing-page wireframe");
for await (const e of mb.events(thread.id)) {
  if (e.type === EventType.ToolCallStarted) console.log("tool:", e.message);
  if (isTaskFinished(e)) break;
}
```

| Area | API |
|---|---|
| Bots | `bots.list/get/create(spec)/update/delete/hire/getModel/setModel/pause/resume/export/importPackage` |
| Models | `models.list()` → `ModelCatalog`, `models.setDefault("provider/model")` |
| Chat | `threads.create/send/messages/files/fileUrl/exportTranscript`, `chat(bot, text)` |
| Work | `tasks`, `approvals.approve(id, "Session")`, `schedules.create(spec)`, `memory`, `skills`, `mcp` |
| Live | `events(threadId, signal)` → `AsyncGenerator<AgentEvent>` |

`npm test` type-checks `test/typecheck.ts` (every `@ts-expect-error` line must fail) and runs the conformance tests
against a real Marbots server.

Built by Gravicode Studios, led by Kang Fadhil. MIT license.
