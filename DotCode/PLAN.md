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
| **M4 — Extensibility** | Skills, subagents, plugins + marketplaces, output styles, MCP HTTP, WebFetch/WebSearch, auto mode | ✅ Done (incl. git worktrees, OS sandbox) · LSP tool pending |
| **M5 — Automation & enterprise** | Managed settings, budgets, fallback chains, OpenTelemetry, audit log ✅ · agent view/daemon, `/loop`, `/schedule`, workflows, CI actions, Rust SDK | 🟡 Partial |
| **M6 — Desktop & IDE** | Avalonia desktop app (true font selection), VS Code extension, JetBrains plugin — all thin protocol clients | ⏳ Planned |
| **M7 — Optional** | Web/cloud runner, computer use, artifacts, voice, channels, agent teams | ⏳ Backlog |

## Next up (priority order)

1. **Release engineering** — CI matrix (win/linux/macOS × x64/arm64) producing NativeAOT binaries, signing, SBOM, installers (winget, Homebrew, scoop, apt), `curl | bash` script, per-platform npm/PyPI packages that bundle the binary so SDK users don't install the CLI separately.
2. **Sandboxing** — ✅ Linux bubblewrap, macOS sandbox-exec, Windows Job Objects for Bash/PowerShell, auto-allow when isolated, escape hatch, network allow/deny (v0.2) · ⏳ per-domain network allowlist (proxy), Windows file-system isolation (AppContainer), seccomp filters.
3. ~~**Auto mode**~~ ✅ done (v0.2) — classifier model that approves low-risk actions automatically.
4. **Git worktrees & parallel agents** — ✅ `--worktree`, `dotcode worktree`, subagent `isolation: worktree`, SDK `worktree` option (v0.2) · ⏳ agent view, cross-session messaging.
5. **LSP tool** — diagnostics, go-to-definition, references via language servers (OmniSharp/Roslyn LSP, tsserver, pyright, gopls).
6. ~~**Observability**~~ ✅ done (v0.2) — OpenTelemetry OTLP/HTTP traces + metrics (opt-in), hash-chained audit log with `dotcode audit verify`.
7. **TUI polish** — ✅ vim mode, Ctrl+R history search, transcript viewer (Ctrl+O), terminal bell/OSC notifications, bilingual UI strings (EN/ID) (v0.2) · ⏳ image paste, clickable file links.
8. **Providers** — Bedrock/Vertex transports for Anthropic, Entra ID / ADC token providers, Gemini context caching, model capability auto-probing for compat servers, token counting via tokenizers.
9. **SDKs** — Rust SDK and schema-driven code generation for all SDKs; WebSocket "connect" mode in TypeScript/Python/Go; in-process WASM/host bindings investigation.
10. **Desktop & IDE** — Avalonia app sharing the event stream; VS Code extension (diff review, inline permissions).

## Principles (unchanged from the design)

Engine-first, protocol-first, rich normalization (no lowest common denominator), safe by default, deterministic tests via record/replay, AOT-friendly, observability built in.
