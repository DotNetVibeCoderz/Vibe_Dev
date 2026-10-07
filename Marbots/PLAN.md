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
| ADR-008 | Rust only for measured hot paths | Native code must be justified by a profile (see ADR-011) |
| ADR-009 | Host protocol: JSON over an outbound WebSocket, not gRPC | One port, works behind NAT without h2c setup, source-generated JSON shared with the rest of the platform |
| ADR-010 | Agent loop stays on the control plane; hosts run tools | Model keys, memory and approvals never leave the server; a host compromise exposes only its own workspaces |
| ADR-011 | No Rust library (profiled 2026-10-07) | grep over 104 MB and hybrid search over 5,000 memories were algorithmic, not language-bound: 1,323→183 ms and 284→85 ms in C#, now near the I/O floor and far below model latency (docs/en/operations.md#performance) |
| ADR-012 | One runtime per tenant, rows stamped with `tenant` | Strong isolation (own engine, scheduler, MCP processes, host CA, data folder) without changing the runtime code; shared server databases filter every query by tenant |
| ADR-013 | Tenant runtime services are never `IDisposable` | Request and circuit scopes resolve tenant singletons through forwarding; disposal by a scope would break the tenant (guarded by a test) |

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

## Phase 2 — Distributed BotAgent ✅
- ✅ `Marbots.AgentHost` (`marbots-host`, self-contained single file) executing files/search/shell/install_package/desktop tools in per-thread workspaces; Spectre.Console live dashboard ("who is working on this computer")
- ✅ Host protocol: JSON frames over one outbound WebSocket (`/api/v1/hosts/connect`) — see ADR-009
- ✅ Enrollment: one-time token (hash only) → host id + secret (hash on server, DPAPI on Windows hosts); disable/remove
- ✅ SSH bootstrap (preflight incl. reachability, upload, enroll, logon task / systemd user unit, wait online) — credentials used once, never stored; `--update` rolling updates keep the enrollment
- ✅ Docker runner: per-bot container profile (image, CPU, memory, network) for `run_shell`
- ✅ Placement (`HostRef: auto`): capability filter + load/memory scoring + thread data locality
- ✅ Offline/reconnect: back-off reconnect, in-flight calls re-sent with the same request id, host result cache (idempotent)
- ✅ `install_package`: winget/scoop/choco, apt/dnf/yum/pacman/apk/zypper, brew, pip/npm/dotnet-tool; .NET via dotnet-install (no admin); PATH refresh
- ✅ Remote workspace files listed and downloaded through the server; skills' files copied to `.skills/<name>/` on load
- ✅ Tested end to end on a second Windows PC (DEV2) with a real LLM: bots created via CLI, SDK, web UI and Boss Man
- ✅ Mutual TLS: per-tenant host CA, client certificates from a CSR at enrollment, renewal/revocation, private server CA trust (tested over HTTPS)
- ✅ Disposable container hosts (`hosts provision`) on the server or any Docker host, deprovisioned on removal (tested on DEV2)
- ✅ GPU detection (NVIDIA, AMD ROCm, Apple Metal, WMI) and GPU-aware placement (`requires: ["cuda", "gpu:16"]`; tested on DEV2 and a Mac)
- ✅ macOS hosts over SSH (osx-x64/arm64, ad-hoc codesign, launchd agent)

## Phase 3 — Productivity + ecosystem ✅
- ✅ Scheduler (cron + one-shot, time zones, run now, misfire on startup)
- ✅ Skill Gallery and MCP Gallery (catalogue + custom servers, trust labels, health)
- ✅ `.marbot` export/import (checksums, secrets stripped, imported MCP disabled until reviewed)
- ✅ A2A: Agent Cards + JSON-RPC `message/send`, `message/stream` (SSE), `tasks/get`, `tasks/cancel` (push notifications later)
- ✅ Official SDKs: .NET, Python, TypeScript, Go
- ✅ Channel gateway: WebChat, Webhook (Teams/Zapier/n8n), Telegram, Slack, WhatsApp, Discord; per-conversation threads; allow-lists
- ✅ Email channel (IMAP polling, threaded SMTP replies, quote stripping, loop protection)
- ✅ Webhook triggers (secret or HMAC) and event-triggered tasks with loop guards
- ✅ Suggest-mode delegation (Boss Man proposes the plan, user approves)

## Phase 4 — Rich clients ✅
- ✅ Avalonia desktop app: 3D office, chat (streaming), approvals, connect or start a local server
- ✅ .NET MAUI Blazor Hybrid mobile app: team, chat (streaming), approvals, activity, local notifications (Android/iOS/Mac/Windows)
- ✅ Remote push notifications when the app is closed: FCM HTTP v1, APNs, ntfy (mobile app registers an ntfy topic)
- ✅ Streaming token output in chat and CLI (OpenAI-compatible SSE, transient `AssistantDelta` events)

## Phase 5 — Auto-Learn + 3D ✅
- ✅ Auto-Learn: `MemoryOnly` and `SuggestSkills` (review queue, secret scanning, never auto-published)
- ✅ Office view: live 2D floor plan in the web app; full 3D office in the desktop app (below)
- ✅ Three.Net 3D office in the desktop app: Rodin-generated furniture and robot, Blender rigging + 6 animation clips, event-driven movement along aisles
- ✅ Learning evaluation: outcomes per skill version, verdicts, trials of auto-learned drafts, version history, manual/automatic rollback

## Cross-cutting
- ✅ Boss Man installs MCP servers from the curated gallery via chat (`list_mcp_catalog`, `install_mcp` with approval + health check)
- ✅ Boss Man gives skills to bots via chat; curated catalog skills are downloaded after approval (`list_skill_catalog`, `install_skill`)
- ✅ Optional sub-agents (`subagents` pack, `spawn_subagents`): parallel temporary copies of a bot with its persona, skills, model and host

## Cross-cutting backlog
- ✅ Hybrid memory search: keyword (FTS5/BM25) + vectors (offline hashing or any `/embeddings` model), reciprocal rank fusion
- ✅ OpenTelemetry: GenAI spans per delegation tree (`invoke_agent`, `chat`, `execute_tool`, `delegate`), metrics, OTLP export
- ✅ Multi-tenant mode: runtime per tenant, SQLite/PostgreSQL/SQL Server/MySQL, tenant API keys, OIDC + JWT, RBAC (Viewer/Operator/Admin/Owner), Access page, tenancy in all SDKs and the CLI
- ✅ Signed skill packages (ES256 manifest, trusted publishers, strict mode) and CycloneDX SBOMs on releases
- ✅ Rust decision: profiled, fixed in C#, no native library (ADR-011)
- 🟡 Publish packages: NuGet (`Marbots.Abstractions`, `Marbots.Sdk`, `Marbots.Cli` tool), PyPI (`marbots-sdk`), npm (`@gravicode/marbots`)
- ✅ Go SDK in the typed DotCode style + module tag; Java (JitPack; Maven Central once the `com.gravicode` namespace is verified) and Rust (`marbots-sdk`, crates.io) SDKs
