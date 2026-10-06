# Marbots — Progress / Kemajuan

Development log and current status. The roadmap is in [PLAN.md](PLAN.md).
*Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.*

## Snapshot — 2026-10-06 (v0.1.0)

| Area | Status | Notes |
|---|---|---|
| Solution & build | ✅ | .NET 10, `Marbots.slnx`, 8 projects + tests, central package management, nullable, analyzers |
| Storage | ✅ | SQLite WAL: documents, messages (per-thread seq), events, FTS5 memory |
| Model providers | ✅ | Azure OpenAI v1, OpenAI-compatible (DeepSeek/Ollama/…), retries with backoff, profile fallbacks, mock |
| Agent runtime | ✅ | Tool loop, policy checks, approvals, timeouts, metering, step limit with summary |
| Boss Man & delegation | ✅ | Protected bot; DAG delegation with parallelism, dependencies, cycle detection, depth limit, cascade cancel |
| Memory & compaction | ✅ | Long-term recall with provenance; rolling summary compaction; `/compact`; reset context; fork |
| Auto-Learn | ✅ | MemoryOnly / SuggestSkills with review queue, secret filter and near-duplicate filter |
| Skills | ✅ | SKILL.md loader, progressive disclosure, install from git/folder, 20 built-in skills |
| MCP | ✅ | stdio + streamable HTTP client, gallery (10 entries), workspace-scoped processes, custom servers, health |
| Kernel functions | ✅ | files, grep, shell, web search/fetch, remember/recall, todo, agents, management |
| Policy & approvals | ✅ | 5 permission profiles, critical-risk override, session grants, expiry, history |
| Scheduler | ✅ | Cron (5-field) + one-off, time zones, run now, misfire handling |
| Templates | ✅ | 58 built-in templates / 11 categories; custom templates; duplicate built-ins |
| `.marbot` packages | ✅ | Checksums, no secrets, bundled skills, imported MCP disabled, schema version |
| Web UI (Blazor) | ✅ | Chat, Office, Tasks, Approvals, Team, Bot editor, Templates, Skills, MCP, Schedules, Memory, Dashboard, Settings, About; EN/ID; light/dark |
| REST + SSE API | ✅ | `/api/v1`, OpenAPI, optional API key |
| A2A | 🟡 | Agent cards, `message/send`, `tasks/get`, `tasks/cancel` (no streaming yet) |
| Per-bot models | ✅ | `default` / `provider/model` / profile per bot; workspace default; fallback to default; model recorded per task; UI, API, CLI, SDKs, `create_bot` |
| Skip approvals | ✅ | Dangerous mode like `--dangerously-skip-permissions`: Settings toggle, `marbots approvals skip on`, server flag `--dangerously-skip-approvals`, API/SDK; profile denies still apply; audited |
| SDKs (typed, DotCode style) | ✅ | .NET, Python (`marbots-sdk`, mypy --strict + typo test), TypeScript (`@gravicode/marbots`, `@ts-expect-error` typo test), Go, Java (JitPack, javac -Werror), Rust (`marbots-sdk`, clippy -D warnings) — each with a conformance test against a real server, all in CI |
| CLI | ✅ | status, bots, bot *, templates, chat (streaming activity), tasks, approvals, skills, mcp, schedules, hosts, logs, themes |
| Docs | ✅ | 11 pages × EN/ID, glossary, screenshots, README EN/ID |
| Remote hosts / AgentHost | ⏳ | Phase 2 |
| Channels (Telegram, WhatsApp, …) | ⏳ | Phase 3 |
| Desktop / mobile / 3D office | ⏳ | Phases 4–5 (2D office view available) |

## Tests

`dotnet test tests/Marbots.Tests` → **81 passed, 0 failed** (≈ 30 s, no network). Python SDK: mypy --strict + 10 rejected typos + 7 conformance tests; TypeScript SDK: 13 rejected typos + 7 conformance tests. Coverage includes:

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

## Known limitations

- One host (local). Bots on the same host are isolated per thread workspace and policy, not per OS process/container.
- Model output is not streamed token by token; the UI streams step-level events instead.
- Memory search is BM25 only; vector retrieval is planned.
- Approvals granted "for this thread" are kept in memory and reset when the server restarts.
- Package publishing (NuGet/PyPI/npm) is prepared but not yet performed.
