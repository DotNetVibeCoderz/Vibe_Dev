# SDK: menyematkan harness DotCode

> 🇬🇧 [English](../en/sdk.md)

Engine yang sama yang menjalankan UI terminal tersedia untuk aplikasi Anda. Setiap SDK adalah klien JSON-RPC 2.0 tipis untuk `dotcode serve` (pola yang sama dengan GitHub Copilot SDK); SDK .NET juga dapat menjalankan engine langsung di dalam proses.

```
Aplikasi Anda (.NET / TypeScript / Python / Go / Java)
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
| Java (17+) | `com.github.DotNetVibeCoderz:Vibe_Dev:dotcode-java-v0.1.2` (JitPack) | [`sdk/java`](../../sdk/java) |

Konsep yang sama di semua SDK: **Client → Session → send / stream**, **tool kustom** yang diimplementasikan aplikasi Anda, **handler izin** (tanpa handler, sesi menolak secara default), handler pertanyaan dan review rencana, server MCP, serta konfigurasi provider per sesi (BYOK). Kelimanya lulus skenario konformansi yang sama.

Server dicari lewat `cliPath`, `DOTCODE_CLI_PATH`, atau `dotcode` di `PATH`.

## Python

```python
import asyncio
from dotcode_sdk import DotCodeClient, tool

@tool("kurs", "Kurs antara dua mata uang",
      {"type": "object", "properties": {"dari": {"type": "string"}, "ke": {"type": "string"}}, "required": ["dari", "ke"]})
def kurs(args):
    return f"1 {args['dari']} = 16.250 {args['ke']}"

async def main():
    async with DotCodeClient() as client:
        session = await client.create_session(model="azure:gpt-5-mini", tools=[kurs],
                                              on_permission_request=lambda r: {"decision": "allow"})
        async for e in session.stream("Berapa rupiah untuk 250 dolar AS?"):
            if e["type"] == "assistant.text.delta":
                print(e["text"], end="")

asyncio.run(main())
```

## TypeScript

```ts
import { DotCodeClient, tool } from "dotcode-sdk";
const client = new DotCodeClient();
const session = await client.createSession({
  model: "deepseek:deepseek-v4-flash",
  tools: [tool({ name: "kurs", description: "Kurs mata uang", handler: () => "1 USD = 16.250 IDR" })],
  onPermissionRequest: () => ({ decision: "allow" }),
});
console.log((await session.send("Berapa rupiah untuk 250 dolar AS?")).result);
await client.close();
```

## .NET, Go, dan Java

Lihat contoh lengkap di [halaman bahasa Inggris](../en/sdk.md) dan contoh yang bisa dijalankan di [`samples/sdk`](../../samples/sdk) (kelima bahasa telah diuji dengan LLM sungguhan).

## Opsi sesi

`model`, `fallbackModel`, `cwd`, `permissionMode`, `systemPrompt`, `appendSystemPrompt`, `allowedTools`, `disallowedTools`, `builtinTools`, `tools`, `mcpServers`, `settings` (JSON pengaturan inline, mis. `{"providers":{…}}`), `maxTurns`, `effort`, `persistSession`, `noMcp`, serta `onPermissionRequest`, `onQuestion`, `onPlanReview`, `onEvent`.

## Protokol

Kontraknya adalah [`schema/protocol.schema.json`](../../schema/protocol.schema.json) (OpenRPC). Metode klien → server: `initialize`, `session.create/resume/send/abort/close`, `session.setModel/setMode/setEffort/compact/clear/messages/info/list`, `models.list`, `tools.list`. Server → klien: notifikasi `session.event`, serta request `permission.request`, `user.question`, `plan.review`, `tool.call`.

Transport: `dotcode serve` (stdio NDJSON), `--content-length` (framing gaya LSP), `--port 8765 --token <rahasia>` (WebSocket di 127.0.0.1).
