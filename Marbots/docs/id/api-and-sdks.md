# API, A2A, SDK, dan CLI

[English](../en/api-and-sdks.md) · [Bahasa Indonesia](../id/api-and-sdks.md)

## REST API

Path dasar `/api/v1`. Dokumen OpenAPI ada di `/openapi/v1.json`. Error memakai RFC 9457 Problem Details.

| Area | Endpoint |
|---|---|
| Sistem | `GET /system`, `POST /system/providers` |
| Bot | `GET/POST /bots`, `GET/PUT/DELETE /bots/{id}`, `POST /bots/{id}/pause\|resume`, `POST /bots/from-template/{templateId}`, `GET /bots/{id}/export`, `POST /bots/import` |
| Templat | `GET /templates?q=&category=`, `GET/POST/PUT/DELETE /templates/{id}` |
| Utas | `GET/POST /threads`, `GET/PATCH/DELETE /threads/{id}`, `GET/POST /threads/{id}/messages`, `POST /threads/{id}/reset\|fork`, `GET /threads/{id}/export`, `GET /threads/{id}/events` (SSE), `GET /threads/{id}/files[/{path}]` |
| Tugas | `GET /tasks`, `GET /tasks/{id}`, `GET /tasks/{id}/transcript`, `POST /tasks/{id}/cancel\|retry` |
| Persetujuan | `GET /approvals?state=pending`, `POST /approvals/{id}/approve` `{scope}`, `POST /approvals/{id}/reject` |
| Skill | `GET /skills`, `POST /skills`, `POST /skills/install`, `DELETE /skills/{name}`, `POST /skills/{name}/approve\|reject` |
| MCP | `GET/POST /mcp`, `GET /mcp/status`, `POST /mcp/{id}/install\|uninstall\|test`, `DELETE /mcp/{id}` |
| Jadwal | `GET/POST /schedules`, `DELETE /schedules/{id}`, `POST /schedules/{id}/run` |
| Memori | `GET /memory/{owner}`, `GET /memory/{owner}/search?q=`, `POST /memory`, `DELETE /memory/item/{id}` |
| Pantau | `GET /hosts`, `GET /usage`, `GET /events` (SSE), `GET /events/recent` |

### Kirim pesan dan tunggu hasilnya

```bash
THREAD=$(curl -s -X POST localhost:5170/api/v1/threads -H 'Content-Type: application/json' \
  -d '{"botId":"boss-man"}' | jq -r .id)
curl -s -X POST localhost:5170/api/v1/threads/$THREAD/messages -H 'Content-Type: application/json' \
  -d '{"text":"Tulis hello.py yang mencetak tanggal hari ini","wait":true,"timeoutSeconds":600}' | jq .reply.content
```

Tanpa `"wait": true`, panggilan langsung mengembalikan `202 Accepted` beserta tugasnya. Ikuti progres lewat SSE:

```bash
curl -N localhost:5170/api/v1/threads/$THREAD/events
```

Event: `MessageAdded`, `TaskCreated`, `TaskStateChanged`, `TaskDelegated`, `TaskProgressed`, `AgentThinkingStarted`,
`ToolCallStarted`, `ToolCallCompleted`, `ApprovalRequested`, `ApprovalResolved`, `MemoryWritten`, `SkillLoaded`,
`ContextCompacted`, `AutoLearnCandidateCreated`, `ScheduleTriggered`, `TodoUpdated`, `BotCreated`, `BotUpdated`,
`BotDeleted`, `BotStateChanged`. Tambahkan `?after=<eventId>` untuk memutar ulang event yang terlewat terlebih dahulu.

## A2A (Agent2Agent, pratinjau)

Setiap bot menerbitkan Agent Card dan menerima JSON-RPC:

- `GET /.well-known/agent-card.json`: kartu Boss Man
- `GET /a2a/{bot}/.well-known/agent-card.json`: kartu bot mana pun
- `POST /a2a/{bot}`: `message/send`, `tasks/get`, `tasks/cancel`

```bash
curl -s -X POST localhost:5170/a2a/wren -H 'Content-Type: application/json' -d '{
  "jsonrpc":"2.0","id":1,"method":"message/send",
  "params":{"message":{"role":"user","messageId":"m1","parts":[{"kind":"text","text":"Apa ciri README yang baik?"}]}}}'
```

`contextId` A2A dipetakan ke utas Marbots, sehingga pesan lanjutan meneruskan percakapan yang sama. Streaming dan
notifikasi push ada di peta jalan.

## SDK

| Bahasa | Lokasi | Instalasi |
|---|---|---|
| .NET | `src/Marbots.Sdk` | `dotnet add package Marbots.Sdk` |
| Python | `sdk/python` | `pip install marbots-sdk` (tanpa dependensi, bertipe) |
| TypeScript / Node | `sdk/typescript` | `npm install @gravicode/marbots` |
| Go | `sdk/go` | `go get github.com/DotNetVibeCoderz/Vibe_Dev/Marbots/sdk/go` |
| Java 17+ | `sdk/java` | JitPack `com.github.DotNetVibeCoderz:Vibe_Dev:marbots-java-v0.1.0` |
| Rust | `sdk/rust` | `cargo add marbots-sdk` |

```csharp
using var mb = new MarbotsClient(new Uri("http://localhost:5170"));
var t = await mb.Threads.CreateAsync("boss-man");
var r = await mb.Threads.SendAsync(t.Id, "Rencanakan peluncuran produk", wait: true);
await foreach (var e in mb.Events.StreamAsync(t.Id)) Console.WriteLine(e.Type);
```

```python
from marbots_sdk import MarbotsClient
print(MarbotsClient().chat("atlas", "3 berita AI teratas hari ini, masing-masing 1 kalimat"))
```

```ts
import { MarbotsClient } from "@gravicode/marbots";
console.log(await new MarbotsClient().chat("wren", "Tulis haiku tentang kelereng"));
```

```go
reply, _ := marbots.New("http://localhost:5170").Chat(ctx, marbots.BossMan, "Halo")
```

```java
String reply = MarbotsClient.create("http://localhost:5170").chat("boss-man", "Halo");
```

```rust
let reply = marbots_sdk::Client::new("http://localhost:5170").chat(marbots_sdk::BOSS_MAN, "Halo")?;
```

### Nama bertipe

Setiap SDK menyediakan nama-nama platform sebagai konstanta/enum, sehingga salah ketik menjadi error saat compile
(atau `mypy`), bukan kejutan saat runtime: paket kernel (`KernelPack.Files`), profil izin
(`PermissionProfile.DeveloperSafe`), tipe event (`EventType.TaskStateChanged`), status tugas, cakupan persetujuan, dan
pengaturan model (`ModelRef.Default`, `ModelRef.Of("azure", "gpt-5.6-luna")`). Setiap SDK memiliki tes yang
membuktikan salah ketik yang disengaja gagal di-compile, serta tes conformance terhadap server sungguhan.

## Model per bot

```bash
curl -X PUT localhost:5170/api/v1/bots/atlas/model -H 'Content-Type: application/json' -d '{"model":"azure/gpt-5.6-luna"}'
curl -X PUT localhost:5170/api/v1/models/default -H 'Content-Type: application/json' -d '{"model":"azure/gpt-5-mini"}'
curl localhost:5170/api/v1/models        # default, pilihan, profil
```

## CLI

```text
marbots status | bots | templates [q] | tasks | hosts | schedules
marbots bot inspect|hire|export|import|pause|resume|delete …
marbots chat <bot> [pesan]          # interaktif jika tanpa pesan
marbots approvals | approve <id> [--session] | reject <id>
marbots approvals skip on|off|status   # berbahaya: tanpa persetujuan
marbots models | models default <provider/model> | bot model <bot> [model]
marbots skills [install <sumber>] | mcp [install <id>]
marbots logs [--thread <id>]
marbots theme set default|aurora|matrix|mono|high-contrast
```

Variabel lingkungan: `MARBOTS_URL`, `MARBOTS_API_KEY`, `NO_COLOR`.

---
*Marbots — Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.*
