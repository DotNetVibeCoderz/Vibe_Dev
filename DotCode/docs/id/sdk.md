# SDK: menyematkan harness DotCode

> 🇬🇧 [English](../en/sdk.md)

Engine yang sama yang menjalankan UI terminal tersedia untuk aplikasi Anda. Setiap SDK adalah klien JSON-RPC 2.0 tipis untuk `dotcode serve`; SDK .NET juga dapat menjalankan engine langsung di dalam proses. API klien mengikuti konvensi [GitHub Copilot SDK](https://github.com/github/copilot-sdk) di tiap bahasa (`createSession`, `send` / `sendAndWait`, event bertipe lewat `on(...)`, `defineTool`, `approveAll`).

```
Aplikasi Anda (.NET / TypeScript / Python / Go / Java / Rust)
     │ klien SDK
     ▼ JSON-RPC 2.0 via stdio (NDJSON) atau WebSocket
dotcode serve ─► Engine ─► provider LLM apa pun
```

| Bahasa | Paket | Source |
|---|---|---|
| .NET | `DotCode.Sdk` (NuGet) | [`src/DotCode.Sdk`](../../src/DotCode.Sdk) |
| TypeScript / JavaScript | `dotcode-sdk` (npm) | [`sdk/typescript`](../../sdk/typescript) |
| Python | `dotcode-sdk` (PyPI) | [`sdk/python`](../../sdk/python) |
| Go | `github.com/DotNetVibeCoderz/Vibe_Dev/DotCode/sdk/go` | [`sdk/go`](../../sdk/go) |
| Java (17+) | `com.github.DotNetVibeCoderz:Vibe_Dev:dotcode-java-v0.2.0` (JitPack) | [`sdk/java`](../../sdk/java) |
| Rust | [`dotcode-sdk`](https://crates.io/crates/dotcode-sdk) (crates.io) | [`sdk/rust`](../../sdk/rust) |

Server dicari lewat `cliPath`, `DOTCODE_CLI_PATH`, atau `dotcode` di `PATH` (path `.dll` dijalankan dengan `dotnet`).

## Bertipe sejak awal

Tidak ada JSON atau skema yang ditulis sebagai string bebas, sehingga salah ketik tertangkap oleh compiler (atau mypy/pyright di Python):

- **Parameter tool adalah sebuah tipe** — schema builder atau Zod (TS), dataclass/TypedDict/model pydantic (Python), struct (Go), record (Java/C#), tipe `serde` + `JsonSchema` (Rust). JSON Schema dibuat dari tipe tersebut, handler menerima nilai bertipe, dan argumen divalidasi sebelum handler berjalan (ketidakcocokan dikembalikan ke model sebagai error tool).
- **Event bertipe** — discriminated union (TS), dataclass (Python), struct `Data` untuk type switch (Go), hierarki sealed (Java), enum (Rust), record (C#); berlangganan satu jenis event dengan `on("tool.completed", …)` / `on(ToolCompletedEvent, …)`.
- **Opsi bertipe** — mode izin, reasoning effort, nama tool bawaan (`BuiltinTool.Read`, `BuiltinTool.Bash.rule("npm test:*")`), provider (`ProviderConfig` dengan `type` bertipe), server MCP, system message, keputusan izin.

Objek `settings` mentah tetap tersedia sebagai jalan keluar untuk pengaturan yang jarang dipakai; semua yang umum punya opsi bertipe.

## API bersama

| Konsep | TypeScript | Python | Go | Java | Rust | .NET |
|---|---|---|---|---|---|---|
| Mulai | `client.start()` | `async with DotCodeClient()` | `client.Start(ctx)` | `client.start()` | `Client::start(opts)` | `client.StartAsync()` |
| Sesi | `createSession(config)` | `create_session(**options)` | `CreateSession(ctx, &SessionConfig{})` | `createSession(new SessionConfig())` | `create_session(SessionConfig)` | `CreateSessionAsync(new SessionConfig())` |
| Tool | `defineTool(name, {parameters, handler})` | `@define_tool(description=…)` | `DefineTool[T](name, desc, fn)` | `ToolDefinition.from(name, desc, Record.class, fn)` | `define_tool(name, desc, \|inv, p: P\| …)` | `DotCodeTool.DefineTool(name, desc, fn, json.Args)` |
| Kirim (kembali setelah dikirim) | `send(msg)` | `send(prompt)` | `Send(ctx, msg)` | `send(msg)` | `send(msg)` | `SendAsync(msg)` |
| Kirim dan tunggu | `sendAndWait(msg, timeoutMs?)` | `send_and_wait(prompt, timeout=)` | `SendAndWait(ctx, msg)` | `sendAndWait(msg[, Duration])` | `send_and_wait[_timeout]` | `SendAndWaitAsync(msg, timeout?)` |
| Event | `on("tool.completed", h)` | `on(ToolCompletedEvent, h)` | `On(func(SessionEvent))` + type switch | `on(ToolCompletedEvent.class, h)` | `on(\|e\| match &e.data …)` | `On<ToolCompletedEvent>(h)` |
| Setujui semua | `approveAll` | `PermissionHandler.approve_all` | `PermissionHandler.ApproveAll` | `PermissionHandler.APPROVE_ALL` | `.approve_all_permissions()` | `PermissionHandler.ApproveAll` |
| Tutup | `disconnect()` / `stop()` | `disconnect()` / `stop()` | `Disconnect()` / `Stop()` | `close()` | `disconnect()` / `stop()` | `DisposeAsync()` |

`send` memulai satu giliran dan langsung kembali; giliran berakhir dengan event `turn.completed` (kegagalan datang sebagai event `error`). `sendAndWait` mengembalikan hasil giliran (teks, alasan berhenti, usage, biaya). TypeScript, Python, Rust dan .NET juga menyediakan `stream(...)`, iterator atas event satu giliran.

Metode sesi lain: `abort`, `setModel`, `setPermissionMode`, `setReasoningEffort`, `compact`, `clear`, `getMessages`, `listTools`. Metode klien: `resumeSession` (dan fork), `listSessions`, `listModels`, `ping`, `forceStop`.

## .NET

```csharp
using System.ComponentModel;
using System.Text.Json.Serialization;
using DotCode.Abstractions;
using DotCode.Sdk;

var getRate = DotCodeTool.DefineTool("get_exchange_rate", "Kurs antara dua mata uang",
    args => $"1 {args.From} = 16,250 {args.To}", ToolJson.Default.RateArgs);   // args.Form tidak akan terkompilasi

await using var client = new DotCodeClient(new DotCodeClientOptions { Mode = ClientMode.InProcess }); // atau Spawn / Connect
await using var session = await client.CreateSessionAsync(new SessionConfig
{
    Model = "azure:gpt-5-mini",
    Tools = [getRate],
    OnPermissionRequest = PermissionHandler.ApproveAll,
});
using var _ = session.On<AssistantTextDeltaEvent>(e => Console.Write(e.Text));
var result = await session.SendAndWaitAsync("Berapa IDR untuk 250 USD?");

public sealed record RateArgs([property: Description("Kode ISO, mis. USD")] string From, string To);

[JsonSerializable(typeof(RateArgs))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
partial class ToolJson : JsonSerializerContext;
```

Overload dengan `JsonTypeInfo` aman untuk NativeAOT; aplikasi yang memakai refleksi dapat memanggil `DotCodeTool.DefineTool<RateArgs>(name, description, handler)` tanpa context.

## TypeScript

```ts
import { DotCodeClient, defineTool, s, approveAll } from "dotcode-sdk";

const getRate = defineTool("get_exchange_rate", {
  description: "Kurs antara dua mata uang",
  parameters: s.object({ from: s.string().describe("Kode ISO, mis. USD"), to: s.string() }),   // atau skema Zod 4
  handler: ({ from, to }) => `1 ${from} = 16,250 ${to}`,
});

const client = new DotCodeClient();
await client.start();
const session = await client.createSession({ model: "deepseek:deepseek-v4-flash", tools: [getRate], onPermissionRequest: approveAll });
session.on("assistant.text.delta", (e) => process.stdout.write(e.text));
const result = await session.sendAndWait({ prompt: "Berapa IDR untuk 250 USD?" });
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
    from_currency: Annotated[str, "Kode ISO, mis. USD"]
    to_currency: str

@define_tool(description="Kurs antara dua mata uang")
def get_rate(params: RateParams) -> str:
    return f"1 {params.from_currency} = 16,250 {params.to_currency}"

async def main():
    async with DotCodeClient() as client:
        async with await client.create_session(model="azure:gpt-5-mini", tools=[get_rate],
                                               on_permission_request=PermissionHandler.approve_all) as session:
            session.on(AssistantTextDeltaEvent, lambda e: print(e.text, end=""))
            result = await session.send_and_wait("Berapa IDR untuk 250 USD?")

asyncio.run(main())
```

## Go

```go
type rateParams struct {
    From string `json:"from" jsonschema:"Kode ISO, mis. USD"`
    To   string `json:"to"`
}
getRate := dotcode.DefineTool("get_exchange_rate", "Kurs antara dua mata uang",
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
result, _ := session.SendAndWait(ctx, dotcode.MessageOptions{Prompt: "Berapa IDR untuk 250 USD?"})
```

## Java

```java
record RateParams(@ToolParam("Kode ISO, mis. USD") String from, String to) {}

var getRate = ToolDefinition.from("get_exchange_rate", "Kurs antara dua mata uang", RateParams.class,
        p -> "1 " + p.from() + " = 16,250 " + p.to());

try (var client = new DotCodeClient()) {
    client.start().get();
    var session = client.createSession(new SessionConfig()
            .setModel("azure:gpt-5-mini")
            .addTool(getRate)
            .setOnPermissionRequest(PermissionHandler.APPROVE_ALL)).get();
    session.on(SessionEvent.AssistantTextDeltaEvent.class, e -> System.out.print(e.text()));
    SendResult result = session.sendAndWait(new MessageOptions("Berapa IDR untuk 250 USD?")).get();
}
```

## Rust

Sinkron dan berbasis thread (tidak butuh async runtime; gunakan `spawn_blocking` dari kode async).

```rust
#[derive(Deserialize, JsonSchema)]
struct RateParams {
    /// Kode ISO, mis. USD
    from: String,
    to: String,
}

let get_rate = define_tool("get_exchange_rate", "Kurs antara dua mata uang", |_inv, p: RateParams| {
    Ok::<_, String>(format!("1 {} = 16,250 {}", p.from, p.to))
});
let client = Client::start(ClientOptions::default())?;
let session = client.create_session(
    SessionConfig::default().with_model("azure:gpt-5-mini").with_tools([get_rate]).approve_all_permissions(),
)?;
let _sub = session.on(|e| if let SessionEventData::AssistantTextDelta { text } = &e.data { print!("{text}") });
let result = session.send_and_wait("Berapa IDR untuk 250 USD?")?;
```

Versi yang bisa dijalankan untuk keenam bahasa ada di [`samples/sdk`](../../samples/sdk).

## Konfigurasi sesi (semua SDK)

`model`, `fallbackModel`, `workingDirectory`, `permissionMode`, `reasoningEffort`, `systemMessage` (`append` atau `replace`), `tools` (tool Anda), `availableTools` (membatasi tool bawaan), `allowedTools` (aturan yang disetujui otomatis), `excludedTools` (tool yang dihapus / aturan yang ditolak), `mcpServers` (stdio / http / sse), `providers` (provider bernama untuk BYOK, dirujuk sebagai `"<nama>:<model>"`), `maxTurns`, `persistSession`, `worktree` (`true` atau nama — berjalan di git worktree, lihat [worktree](worktree.md)), `disableMcp`, `settings` (mentah, lanjutan); handler `onPermissionRequest`, `onUserInputRequest` (AskUserQuestion), `onExitPlanMode` (review rencana) dan `onEvent`. Penamaan mengikuti konvensi tiap bahasa (`working_directory` di Python, `WorkingDirectory` di Go/.NET, `setWorkingDirectory` di Java, `with_working_directory` di Rust).

Tanpa `onPermissionRequest`, sesi **menolak secara default**. Keputusan: setujui sekali, setujui untuk sesi ini, setujui selalu (simpan aturan), atau tolak dengan umpan balik untuk model. Tool kustom tidak pernah meminta izin; tandai read-only agar boleh berjalan di plan mode.

## Protokol

Kontraknya adalah [`schema/protocol.schema.json`](../../schema/protocol.schema.json) (OpenRPC). Ringkasan:

| Klien → server | Server → klien |
|---|---|
| `initialize`, `ping`, `shutdown` | notifikasi `session.event` (setiap event engine) |
| `session.create`, `session.resume`, `session.send`, `session.abort`, `session.close` | `permission.request` → `{decision, feedback?, rule?, updatedInput?}` |
| `session.setModel`, `session.setMode`, `session.setEffort`, `session.compact`, `session.clear` | `user.question` → `{answers}` |
| `session.messages`, `session.info`, `session.list`, `models.list`, `tools.list` | `plan.review` → `{approval, feedback?}` · `tool.call` → `{content, isError?}` |

Transport: `dotcode serve` (stdio NDJSON), `dotcode serve --content-length` (framing gaya LSP), `dotcode serve --port 8765 --token <rahasia>` (WebSocket di 127.0.0.1; kirim `Authorization: Bearer <rahasia>` atau `?token=`).

Membuat SDK untuk bahasa lain hanya butuh klien JSON-RPC: handshake dengan `initialize` (deklarasikan `capabilities.permissions`/`questions` untuk menerima callback), buat sesi, kirim prompt, dan jawab empat request dari server.

## Test

Setiap SDK lulus skenario konformansi yang sama terhadap `dotcode serve` sungguhan dengan model skrip offline: tool kustom bertipe dengan event streaming, handler izin dengan `send` + `turn.completed`, serta argumen tool yang tidak valid dilaporkan ke model. Suite TypeScript dan Python juga memeriksa saat kompilasi bahwa salah ketik ditolak (`sdk/typescript/test/typecheck.ts`, `sdk/python/tests/typecheck` dengan mypy).
