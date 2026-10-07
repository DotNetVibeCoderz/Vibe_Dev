# Marbots — Progress / Kemajuan

Development log and current status. The roadmap is in [PLAN.md](PLAN.md).
*Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.*

## Snapshot — 2026-10-07 (v0.3)

| Area | Status | Notes |
|---|---|---|
| Solution & build | ✅ | .NET 10, `Marbots.slnx`, 10 projects + tests (+ MAUI mobile outside the slnx), central package management, nullable, analyzers |
| Storage | ✅ | SQLite (WAL), PostgreSQL, SQL Server, MySQL: documents, messages (per-thread seq), events, hybrid memory (FTS5/BM25 + vectors); `tenant` on every row; automatic migration of older SQLite files |
| Multi-tenant & sign-in | ✅ | Runtime per tenant, tenant API keys, OIDC + JWT, roles, Access page, tenant switcher, `/t/<tenant>` URLs; tenancy in all six SDKs and the CLI |
| Telemetry | ✅ | OpenTelemetry GenAI spans + metrics + logs over OTLP (verified against an OTLP/HTTP receiver) |
| Push | ✅ | FCM HTTP v1, APNs, ntfy; device API; mobile ntfy registration |
| Model providers | ✅ | Azure OpenAI v1, OpenAI-compatible (DeepSeek/Ollama/…), retries with backoff, profile fallbacks, mock |
| Agent runtime | ✅ | Tool loop, policy checks, approvals, timeouts, metering, step limit with summary |
| Boss Man & delegation | ✅ | Protected bot; DAG delegation with parallelism, dependencies, cycle detection, depth limit, cascade cancel |
| Memory & compaction | ✅ | Long-term recall with provenance; rolling summary compaction; `/compact`; reset context; fork |
| Auto-Learn | ✅ | MemoryOnly / SuggestSkills with review queue, secret filter and near-duplicate filter |
| Skills | ✅ | SKILL.md loader, progressive disclosure, install from git/folder, 20 built-in skills |
| MCP | ✅ | stdio + streamable HTTP client, gallery (10 entries), workspace-scoped processes, custom servers, health |
| Kernel functions | ✅ | files, grep, shell, install_package (winget/scoop/choco, apt/dnf/…, brew, pip/npm/dotnet-tool), web search/fetch, remember/recall, todo, desktop (screenshot/click/type/keys with vision), agents, management |
| Policy & approvals | ✅ | 5 permission profiles, critical-risk override, session grants, expiry, history |
| Scheduler | ✅ | Cron (5-field) + one-off, time zones, run now, misfire handling |
| Templates | ✅ | 58 built-in templates / 11 categories; custom templates; duplicate built-ins |
| `.marbot` packages | ✅ | Checksums, no secrets, bundled skills, imported MCP disabled, schema version |
| Web UI (Blazor) | ✅ | Chat, Office, Tasks, Approvals, Team, Bot editor, Templates, Skills, MCP, Schedules, Memory, Dashboard, Settings, About; EN/ID; light/dark |
| REST + SSE API | ✅ | `/api/v1`, OpenAPI, optional API key |
| A2A | ✅ | Agent cards, `message/send`, `message/stream` (SSE), `tasks/get`, `tasks/cancel` |
| Channels | ✅ | WebChat (real-LLM tested), Webhook, Telegram, Slack, WhatsApp, Discord (provider APIs simulated in tests), E-mail (IMAP/SMTP, tested against protocol servers) |
| Triggers & suggest mode | ✅ | Webhook (secret/HMAC) and event triggers with loop guards; delegation plans can require approval |
| Per-bot models | ✅ | `default` / `provider/model` / profile per bot; workspace default; fallback to default; model recorded per task; UI, API, CLI, SDKs, `create_bot` |
| Skip approvals | ✅ | Dangerous mode like `--dangerously-skip-permissions`: Settings toggle, `marbots approvals skip on`, server flag `--dangerously-skip-approvals`, API/SDK; profile denies still apply; audited |
| SDKs (typed, DotCode style) | ✅ | .NET, Python (`marbots-sdk`, mypy --strict + typo test), TypeScript (`@gravicode/marbots`, `@ts-expect-error` typo test), Go, Java (JitPack, javac -Werror), Rust (`marbots-sdk`, clippy -D warnings) — each with a conformance test against a real server, all in CI |
| CLI | ✅ | status, bots, bot * (incl. host, container, skills, packs, profile), templates, chat (token streaming), tasks, approvals, skills (evaluations, rollback/promote/discard), mcp, schedules, hosts (token, bootstrap, update, disable/remove), logs, themes |
| Docs | ✅ | 14 pages × EN/ID (new: computers, apps), glossary, screenshots, README EN/ID |
| Remote hosts / AgentHost | ✅ | `marbots-host` (Spectre.Console dashboard), WebSocket protocol, enrollment, SSH bootstrap (Windows, Linux, macOS) + `--update`, mutual TLS, GPU-aware placement, container hosts, Docker profiles, reconnect + idempotent re-send, remote files; tested on DEV2 and an Intel Mac |
| Desktop / mobile / 3D office | ✅ | Avalonia app with the Three.Net 3D office (Rodin assets, Blender-rigged robot with 6 clips); MAUI Blazor Hybrid app with approvals and notifications |
| Streaming | ✅ | Token streaming (SSE) in web chat, CLI, desktop and mobile; transient events not stored |
| Learning evaluation | ✅ | Outcomes per skill version, verdicts, trials of drafts, history, manual/automatic rollback |

## Tests

`dotnet test tests/Marbots.Tests` → **176 passed, 0 failed** (≈ 75 s, no network; storage tests also on PostgreSQL, SQL Server and MySQL when `MARBOTS_TEST_*` are set, as in CI). Python SDK: mypy --strict + 10 rejected typos + 7 conformance tests; TypeScript SDK: 13 rejected typos + 7 conformance tests. Coverage includes:

- Policy profiles, critical-risk override, session grants, deny precedence
- Cron parsing/next-run (steps, ranges, DOM/DOW OR rule, time zones, invalid input)
- Front-matter parsing; every built-in skill parses
- Workspace path traversal and glob matching
- Context repair (orphaned/interrupted tool calls), old tool-output truncation, system prompt composition
- Provider request building, response parsing, endpoint resolution
- Storage: memory ranking and owner isolation, FTS injection safety, concurrent message sequencing, event replay
- End-to-end runtime with a scripted model: chat round trip, file tools, traversal refusal, **parallel delegation with
  dependencies**, cycle/unknown-bot rejection, **shell approval reject/approve**, cancellation, manual compaction,
  memory recall, `.marbot` round trip without secrets, tampered package rejection, scheduler, event emission
- Auto-Learn similarity and JSON extraction

## Real-LLM trials (Azure OpenAI gpt-5-mini)

Seven concurrent jobs plus A2A: **16/16 tasks completed**, ~1.02 M tokens, ≈ $0.41. Details in
[docs/en/trials.md](docs/en/trials.md); artifacts in `samples/trials/outputs`.

## Log

- **2026-10-06**: Initial implementation of phases 0–1 and most of phase 3/5 scope; Blazor UI with the "glass marbles"
  design system; SDKs for 4 languages; CLI; bilingual docs; real-LLM trial run and screenshots.
  - Found and fixed during trials: static web assets when running from source in Production; MCP test endpoint now
    returns Problem Details instead of an HTML error page; removed `mcp-server-sqlite` from the default gallery (the
    upstream package crashes with the current MCP Python library); office desk spacing; workspace listings hide tool
    caches; CLI `NO_COLOR` output; Auto-Learn near-duplicate memories.

- **2026-10-07**: Moved into the Vibe_Dev monorepo. Per-bot model selection with a workspace default (real-LLM check:
  Atlas on azure/gpt-5.6-luna, Wren on deepseek/deepseek-v4-flash, Alice on the default azure/gpt-5-mini). Dangerous
  "skip approvals" mode (UI, CLI, server flag, API). SDKs rewritten in DotCode's typed style for .NET, Python and
  TypeScript with compile-time typo tests and conformance tests against a real server. GitHub Actions workflow.

- **2026-10-07 (b)**: Phase 3 complete: channel gateway (web chat, webhook, Telegram, Slack, WhatsApp, Discord),
  webhook/event triggers, suggest-mode delegation, A2A streaming. 94 tests. Real-LLM web chat conversation in Indonesian.

- **2026-10-07 (c)**: Token streaming: providers stream when the caller asks (SSE text deltas, tool calls assembled by
  index, usage in the final chunk); the runtime publishes transient `AssistantDelta` events (never stored); the web chat
  renders live Markdown with a caret and the CLI prints text as it arrives. 97 tests. Verified with Azure gpt-5-mini
  (CLI) and DeepSeek (web UI). SDKs published: NuGet, PyPI, npm, crates.io, Go module, JitPack.

- **2026-10-07 (d)**: Phases 2, 4 and 5 completed.
  - **Phase 2.** `Marbots.AgentHost` plus the host protocol, enrollment, SSH bootstrap (rolling updates), placement,
    Docker container profiles, `install_package`, computer use with vision, remote workspace files, and skills' files
    copied to hosts.
  - **Phase 4.** Avalonia desktop app with a 3D office; MAUI mobile app.
  - **Phase 5.** Rodin and Nano Banana assets, Blender rigging and animation, learning evaluation.
  - **Trials on a second PC (DEV2).** Bots were created via the CLI, SDK, web UI and Boss Man. They made PPTX, DOCX,
    XLSX and PDF files with Anthropic skills after Tavily research, built Blazor, Avalonia and console apps on .NET 10,
    used Playwright, Docker and computer use. See docs/en/trials.md, including what fell short.
  - **Bugs found by the trials and fixed:** `create_bot` approval timeout, pip/npm packages, skill scripts on hosts,
    the container note, and SSE resume dropping transient events.
- **2026-10-07 (e)**: Release 0.2.0. All six SDKs (.NET, Python, TypeScript, Go, Java, Rust) gained agent hosts
  (list, enrollment, SSH bootstrap, disable/enable/remove), learning evaluation (evaluations, rollback, promote,
  discard, auto-rollback), bot placement (`hostRef`, container profile) and the desktop/subagents packs, each with
  typo checks and conformance tests. `marbots-host` binaries are attached to the GitHub release.

- **2026-10-07 (f)**: Backlog completed (0.3).
  - **Storage and tenancy.** PostgreSQL, SQL Server and MySQL storage with a tenant column, plus hybrid memory search.
    Multi-tenant mode runs one runtime per tenant, with tenant API keys, OIDC/JWT sign-in, roles, the Access page and
    a tenant switcher. All six SDKs and the CLI support tenancy.
  - **Operations.** OpenTelemetry (GenAI spans, metrics, OTLP); an e-mail channel; push notifications (FCM, APNs,
    ntfy).
  - **Hosts.** Mutual TLS for agent hosts; GPU detection and placement; disposable container hosts; macOS hosts
    (osx-x64, launchd).
  - **Supply chain.** Signed skill packages; CycloneDX SBOMs and host binaries attached to releases by CI; CI service
    containers for the database tests.
  - **Performance.** Profiling sped up `grep` 7x and hybrid memory search 3.3x in C#; the decision is no Rust
    (ADR-011). SSH.NET was upgraded to 2026.0.0 (two high-severity advisories).
  - **Trials.** Real data migrated, with providers re-registered. On DEV2, GPU auto-placement (`gpu:2`) and a container
    host (Ubuntu, 1 GiB limit) were run with a real LLM, and deprovisioning was tested. The Intel Mac was bootstrapped
    over SSH with a launchd agent and both GPUs detected. mTLS was tested over HTTPS. Multi-tenant mode was tested on
    MySQL with an interactive Blazor circuit.
  - **Bugs found and fixed:** request scopes disposed tenant singletons; `POST /threads` without a bot crashed; SFTP
    chmod for Linux/macOS hosts; numeric arguments for `run_shell` in provisioning; macOS GPU memory and vendor
    parsing; e-mail signature detection.

## Known limitations

- Bots on the same host are isolated per thread workspace and policy; use a container profile or a container host for process isolation.
- MCP servers run on the control plane, also for bots placed on other computers.
- The mobile app registers ntfy topics; native FCM/APNs registration in the app needs your own Firebase/Apple setup (the server side is complete). Windows toast delivery was not verified.
- The FCM, APNs and ntfy integrations were verified against faithful test doubles (signatures checked), not against the live Google/Apple services.
- Container hosts were trialled on Docker Desktop (Windows); Linux Docker engines were covered by unit tests only.
- In multi-tenant mode without OIDC, the web UI signs in with API keys.
- Approvals granted "for this thread" are kept in memory and reset when the server restarts.
- Java SDK is distributed through JitPack (the `com.gravicode` Maven Central namespace is not verified yet).
