# Marbots — Development Plan / Rencana Pengembangan

> Roadmap for Marbots (Marvelous Bots). Status is tracked in [Progress.md](Progress.md); the architecture is in
> [solution-design.md](solution-design.md).
> *Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.*

Legend: ✅ done · 🟡 partial / preview · ⏳ planned

## Guiding decisions

| # | Decision | Why |
|---|---|---|
| ADR-001 | .NET 10 is the primary platform | One language for runtime, API, web UI and SDK; strong async + perf |
| ADR-002 | Boss Man is a protected system bot | Stable orchestration entry point |
| ADR-003 | Bot identity ≠ runtime instance | Restart/migrate bots without changing who they are |
| ADR-004 | MCP for tools, A2A for agents | Open protocols, no lock-in |
| ADR-005 | Modular monolith first | Low ops cost, hard module boundaries (projects) |
| ADR-006 | Event-oriented observability | Web, CLI, SDKs and Office view share one event stream |
| ADR-007 | Auto-Learn can't elevate privileges | Learned output is untrusted until a human approves |
| ADR-008 | Rust only for measured hot paths | No hot path has justified native code yet (see Performance) |

## Phase 0 — Foundations ✅
- ✅ Solution layout, central package management, deterministic builds, analyzers
- ✅ Domain contracts (bots, templates, threads, tasks, approvals, memory, schedules, events)
- ✅ Model abstraction (`IModelProvider`, `IModelRouter` with fallbacks) — Azure OpenAI + any OpenAI-compatible endpoint + offline mock
- ✅ Kernel function abstraction (`IKernelFunction`) with schema, permission category, risk and timeout
- ✅ SQLite storage (WAL) for documents, messages, events and FTS5 memory
- ✅ Deterministic policy engine + approvals

## Phase 1 — Boss Man + local bots ✅
- ✅ Boss Man persistent default, cannot be deleted
- ✅ Bot CRUD (UI, API, CLI, SDKs, conversational `create_bot`)
- ✅ Direct chat with any bot; threads (pin, archive, reset context, fork, export transcript)
- ✅ Delegation DAG with parallel fan-out, dependencies, cycle detection, depth limit, cancellation cascade
- ✅ Short-term memory, long-term memory (BM25 + confidence + recency), context compaction (auto + `/compact`)
- ✅ Skills (SKILL.md, progressive disclosure, install from git/folder) and MCP (stdio + streamable HTTP)
- ✅ Kernel packs: files, search, shell, web (Tavily or DuckDuckGo), memory, todo, agents
- ✅ Blazor web UI (chat, team, template gallery, tasks, approvals, office, skills, MCP, schedules, memory, dashboard, settings)
- ✅ CLI (`marbots`) with themes
- ✅ 58 built-in bot templates across 11 categories; 20 built-in skills

## Phase 2 — Distributed BotAgent ⏳
- ⏳ `Marbots.AgentHost` service (separate process) speaking a gRPC protocol (`protocols/agenthost.proto`)
- ⏳ Host registry with enrollment: one-time token → host certificate → mTLS
- ⏳ SSH bootstrap (preflight, install service, register) — credentials only during bootstrap
- ⏳ Docker runner (per-bot container profile, CPU/RAM quotas) and VM hosts
- ⏳ Placement scoring (capability, load, GPU, data locality, affinity)
- ⏳ Offline/reconnect with idempotent event sync; host drift + rolling updates
- 🟡 Today: one local host (`local-default`) with live metrics; per-thread workspaces isolate concurrent work

## Phase 3 — Productivity + ecosystem ✅
- ✅ Scheduler (cron + one-shot, time zones, run now, misfire on startup)
- ✅ Skill Gallery and MCP Gallery (catalogue + custom servers, trust labels, health)
- ✅ `.marbot` export/import (checksums, secrets stripped, imported MCP disabled until reviewed)
- ✅ A2A: Agent Cards + JSON-RPC `message/send`, `message/stream` (SSE), `tasks/get`, `tasks/cancel` (push notifications later)
- ✅ Official SDKs: .NET, Python, TypeScript, Go
- ✅ Channel gateway: WebChat, Webhook (Teams/Zapier/n8n), Telegram, Slack, WhatsApp, Discord; per-conversation threads; allow-lists
- ⏳ Email channel (IMAP/SMTP)
- ✅ Webhook triggers (secret or HMAC) and event-triggered tasks with loop guards
- ✅ Suggest-mode delegation (Boss Man proposes the plan, user approves)

## Phase 4 — Rich clients ⏳
- ⏳ Avalonia desktop app (embedded runtime or remote control plane)
- ⏳ .NET MAUI Hybrid mobile app (chat, approvals, push notifications)
- ✅ Streaming token output in chat and CLI (OpenAI-compatible SSE, transient `AssistantDelta` events)

## Phase 5 — Auto-Learn + 3D 🟡
- ✅ Auto-Learn: `MemoryOnly` and `SuggestSkills` (review queue, secret scanning, never auto-published)
- 🟡 Office view: live 2D floor plan driven by events (fallback mode from the design)
- ⏳ Three.Net 3D office (Rust/wgpu) with Blender-authored assets, LOD, instancing
- ⏳ Learning evaluation (promote/rollback skills based on outcomes)

## Cross-cutting backlog
- ⏳ Vector retrieval (`IVectorStore`) alongside BM25 for hybrid memory search
- ⏳ OpenTelemetry exporters (traces per delegation tree, metrics)
- ⏳ Multi-tenant mode (TenantId/WorkspaceId everywhere, PostgreSQL, OIDC, RBAC)
- ⏳ Signed skill packages and SBOM for releases
- ⏳ Rust native library only if profiling shows a hot path (candidates: large-workspace grep, tokenizer)
- 🟡 Publish packages: NuGet (`Marbots.Abstractions`, `Marbots.Sdk`, `Marbots.Cli` tool), PyPI (`marbots-sdk`), npm (`@gravicode/marbots`)
- ✅ Go SDK in the typed DotCode style + module tag; Java (JitPack; Maven Central once the `com.gravicode` namespace is verified) and Rust (`marbots-sdk`, crates.io) SDKs
