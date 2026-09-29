# DotCode — Development Plan / Rencana Pengembangan

*Gravicode Studios, led by Kang Fadhil.* Status of each item is tracked in [Progress.md](Progress.md); the full design is in [solution-design.md](solution-design.md).

> 🇮🇩 Rencana ini mengikuti fase M0–M7 dari dokumen desain. Status tiap butir dilacak di [Progress.md](Progress.md).

## Milestones

| Phase | Scope | Status |
|---|---|---|
| **M0 — Foundations** | Solution skeleton, abstractions, NativeAOT pipeline, streaming HTTP, record/replay provider | ✅ Done |
| **M1 — Core MVP** | Agent loop, core tools, permissions & modes, JSONL sessions, compaction, `DOTCODE.md`, Anthropic + OpenAI + Ollama, headless `-p`, slash commands, hooks, MCP stdio | ✅ Done |
| **M2 — UX parity** | Full TUI (diffs, dialogs, todos, status line, `@`/`!`/`#`, themes, pickers), `/resume`, `/rewind`, checkpoints, plan mode, Gemini + DeepSeek + compat, graceful degradation, `/context`, `/model` | ✅ Done (incl. vim mode, transcript viewer, Ctrl+R, EN/ID UI) |
| **M3 — Protocol & SDK** | `dotcode serve` (stdio + WebSocket), OpenRPC schema, SDKs for .NET (in-proc + remote), TypeScript, Python, **Go**, **Java**; host tools, permission callbacks, conformance tests, docs | ✅ Done |
| **M4 — Extensibility** | Skills, subagents, plugins + marketplaces, output styles, MCP HTTP + legacy SSE, WebFetch/WebSearch, auto mode | ✅ Done (incl. git worktrees, OS sandbox, LSP tool) |
| **M5 — Automation & enterprise** | Managed settings, budgets, fallback chains, OpenTelemetry, audit log ✅ · agent view/daemon, `/loop`, `/schedule`, workflows, CI actions | 🟡 Partial |
| **M6 — Desktop & IDE** | VS Code extension ✅ · Avalonia desktop app (true font selection), JetBrains plugin — all thin protocol clients | 🟡 Partial |
| **M7 — Optional** | Web/cloud runner, computer use, artifacts, voice, channels, agent teams | ⏳ Backlog |

## Next up (priority order)

1. **Release engineering** — ✅ 6 NativeAOT platforms (win/linux/macOS × x64/arm64), SHA256SUMS, SPDX SBOM, build-provenance attestations, `install.sh` / `install.ps1`, `dotcode update`, Scoop manifest (v0.2) · ⏳ code signing (Authenticode, Apple notarization), winget/Homebrew/apt, musl builds, per-platform npm/PyPI packages bundling the binary.
2. **Sandboxing** — ✅ Linux bubblewrap, macOS sandbox-exec, Windows Job Objects for Bash/PowerShell, auto-allow when isolated, escape hatch, network allow/deny (v0.2) · ⏳ per-domain network allowlist (proxy), Windows file-system isolation (AppContainer), seccomp filters.
3. ~~**Auto mode**~~ ✅ done (v0.2) — classifier model that approves low-risk actions automatically.
4. **Git worktrees & parallel agents** — ✅ `--worktree`, `dotcode worktree`, subagent `isolation: worktree`, SDK `worktree` option (v0.2) · ⏳ agent view, cross-session messaging.
5. ~~**LSP tool**~~ ✅ done (v0.2) — definition, implementation, references, hover, symbols, diagnostics via typescript-language-server, pyright, gopls, rust-analyzer, csharp-ls, clangd, jdtls or configured servers; errors reported after edits.
6. ~~**Observability**~~ ✅ done (v0.2) — OpenTelemetry OTLP/HTTP traces + metrics (opt-in), hash-chained audit log with `dotcode audit verify`.
7. **TUI polish** — ✅ vim mode, Ctrl+R history search, transcript viewer (Ctrl+O), terminal bell/OSC notifications, bilingual UI strings (EN/ID) (v0.2) · ⏳ image paste, clickable file links.
8. **Providers** — ✅ Amazon Bedrock (SigV4 / API key, event stream), Google Vertex AI (Claude + Gemini, ADC incl. service-account JWT), Azure OpenAI with Microsoft Entra ID (v0.2) · ⏳ live-account verification of those three, Gemini context caching, model capability auto-probing for compat servers, token counting via tokenizers.
9. **SDKs** — ✅ Rust SDK on crates.io (v0.2) · ⏳ schema-driven code generation for all SDKs; WebSocket "connect" mode in TypeScript/Python/Go; in-process WASM/host bindings investigation.
10. **Desktop & IDE** — ✅ VS Code extension (chat, diff review, inline permissions; v0.2) · ✅ published on the VS Code Marketplace (GravicodeStudios.dotcode-vscode) · ⏳ Open VSX, Avalonia app, JetBrains plugin.

## Principles (unchanged from the design)

Engine-first, protocol-first, rich normalization (no lowest common denominator), safe by default, deterministic tests via record/replay, AOT-friendly, observability built in.
