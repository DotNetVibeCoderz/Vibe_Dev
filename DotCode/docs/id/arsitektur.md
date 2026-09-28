# Arsitektur

> 🇬🇧 [English](../en/architecture.md)

Desain lengkap ada di [`solution-design.md`](../../solution-design.md). Halaman ini menjelaskan apa yang sudah diimplementasikan.

## Satu engine, banyak surface, satu protokol

```
┌──────────────┐  ┌─────────────┐  ┌──────────────────────────────┐
│ TUI (dotcode)│  │ Headless -p │  │ SDK: .NET · TS · Python · Go │
└──────┬───────┘  └──────┬──────┘  └──────────────┬───────────────┘
       │ in-process       │ in-process             │ JSON-RPC 2.0 (dotcode serve)
       └──────────────────┴────────────┬───────────┘
                                ┌──────▼──────┐
                                │   Engine    │  agent loop · tools · izin · hooks
                                │             │  konteks/kompaksi · sesi · checkpoint
                                │             │  skills · commands · subagent · plugin · MCP
                                └──────┬──────┘
                                ┌──────▼──────┐
                                │  Providers  │  Anthropic · OpenAI · Azure · Gemini
                                │             │  DeepSeek · Ollama · kompatibel OpenAI
                                └─────────────┘
```

Semua surface mengonsumsi aliran `AgentEvent` yang sama, sehingga tidak ada fitur yang hanya ada di salah satunya.

## Proyek

| Proyek | Tanggung jawab |
|---|---|
| `DotCode.Abstractions` | Model pesan ternormalisasi, request/event model, kapabilitas, `IModelProvider`, `AgentEvent`, kontrak izin. Tanpa dependensi. |
| `DotCode.Providers` | Adapter di atas `HttpClient` + SSE/NDJSON + `Utf8JsonWriter`, katalog model, sanitizer skema, perbaikan JSON, text-tool-protocol, provider skrip/rekam. |
| `DotCode.Engine` | `AgentRuntime`, `AgentSession` (loop), `ToolExecutor`, `PromptBuilder`, `Compactor`, `ModelRouter`, tool bawaan, mesin izin, hook, penyimpanan sesi, checkpoint, registri ekstensi, plugin, klien MCP. |
| `DotCode.Protocol` | Peer JSON-RPC (stdio/WebSocket), `AgentServer`, proxy host tool. |
| `DotCode.Tui` | Renderer (scrollback statis + area live), editor input, markdown, tema, dialog, slash command. |
| `DotCode.Cli` | Entry point, TUI, runner headless, subcommand. NativeAOT. |
| `DotCode.Sdk` | SDK .NET (Spawn / Connect / InProcess). |
| `sdk/typescript`, `sdk/python`, `sdk/go` | SDK bahasa lain. |
| `tools/DotCode.TermCapture` | Harness ConPTY untuk screenshot. |

## Loop agen

1. Hook `UserPromptSubmit` → pesan pengguna (dengan lampiran `@file`) ditambahkan dan disimpan.
2. Loop: pastikan budget konteks (auto-compact) → susun request (system prompt, snapshot git, file memori, instruksi MCP, riwayat sejak kompaksi terakhir, skema tool) → stream pemanggilan model dengan retry/fallback → tambahkan pesan asisten.
3. Bila model memanggil tool: validasi → hook `PreToolUse` → izin → eksekusi (paralel untuk baca-saja) → hook `PostToolUse` → hasil ditambahkan. Ulangi.
4. Tanpa pemanggilan tool: hook `Stop` dapat menyuruh agen lanjut; jika tidak, kirim `TurnCompleted` dengan usage dan biaya.

## Keputusan desain

- **Model pesan sendiri** (bukan `IChatClient`) untuk mempertahankan breakpoint cache, thinking bertanda tangan, dan data milik provider; blok opaque hanya dikirim ke provider asalnya sehingga berganti model di tengah sesi aman.
- **NativeAOT**: semua JSON lewat source generator atau `Utf8JsonWriter`; peringatan AOT diperlakukan sebagai error. Hasilnya binary tunggal ~14 MB yang langsung jalan.
- **UI terminal** model Ink: blok selesai masuk ke scrollback asli, area live digambar ulang dengan synchronized update; pengukuran lebar tampilan (CJK, emoji) membuat posisi kursor tepat.
- **Kompatibel** dengan file Claude Code untuk memudahkan migrasi.

Perbedaan dari dokumen desain: beberapa proyek digabung (Tools/Permissions/Mcp/Extensibility → Engine, Host → Protocol, satu proyek Providers) agar build lebih cepat; aplikasi Desktop, ekstensi IDE, sandbox, auto mode, SDK Java/Rust, dan fitur cloud ada di [roadmap](../../PLAN.md).
