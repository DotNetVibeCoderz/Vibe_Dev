# DotCode — Progress / Progres

*Last updated: 2026-09-28 · v0.1.0* — Legend: ✅ done · 🟡 partial · ⏳ planned

> 🇮🇩 Dokumen ini melacak progres implementasi terhadap requirement ([requirements.md](requirements.md)) dan desain ([solution-design.md](solution-design.md)). Rencana ke depan ada di [PLAN.md](PLAN.md).

## Requirements checklist (requirements.md)

| Requirement | Status | Evidence |
|---|---|---|
| Built with .NET 10 | ✅ | `global.json` (SDK 10.0.401), C# 14, all projects `net10.0` |
| Best practice, performance, memory efficiency | ✅ | NativeAOT single binary (~14 MB), source-generated JSON, `Utf8JsonWriter` request building, pooled `HttpClient`, streaming parsers, bounded diff memory, parallel read-only tools, zero AOT/trim warnings |
| Complete bilingual documentation (EN + ID) | ✅ | `README.md` / `README.id.md`, `docs/en/*` and `docs/id/*` (11 pages each) |
| Screenshots in README and docs | ✅ | `docs/images/*.png`, captured from real sessions with `tools/DotCode.TermCapture` |
| Credit "Gravicode Studios, led by Kang Fadhil" in docs and app | ✅ | Welcome banner, `--version`, `/about`, `/help`, `doctor`, server `initialize`, package metadata, READMEs |
| PLAN.md and Progress.md | ✅ | This file and [PLAN.md](PLAN.md) |
| Tested by building apps, documents, scripts, with MCP, skills, plugins | ✅ | .NET console app, landing page (HTML/CSS/JS), Python & PowerShell scripts, CSV insights Markdown report (skill), notes MCP server, marketplace plugin command — see screenshots |
| Tested with real LLMs | ✅ | Azure OpenAI gpt-5-mini (Responses API), DeepSeek V4 Flash (Chat), DeepSeek via Anthropic Messages endpoint; all four SDK samples |
| UI/UX closely matching Claude Code incl. animations/loading, with selectable themes (colors, font) | ✅ | Spinner with shimmer + blinking tool dots, same layout/dialogs/shortcuts; 15 themes, custom JSON themes, glyph sets for fonts (unicode/ascii/nerd), accent, spinner & input styles, reduced motion |
| Publish to NuGet, PyPI, npm | ✅ | NuGet: `DotCode.Cli` (dotnet tool), `DotCode.Sdk`, `DotCode.Engine`, `DotCode.Providers`, `DotCode.Protocol`, `DotCode.Tui`, `DotCode.Abstractions` 0.1.0 via GitHub Actions (tag `dotcode-v0.1.0`, secret `NUGET_API_KEY`) · npm `dotcode-sdk@0.1.0` · PyPI `dotcode-sdk 0.1.0` · GitHub Release with NativeAOT binaries (win-x64, linux-x64, osx-arm64) |
| `--dangerously-skip-permissions` | ✅ | Plus `--allow-dangerously-skip-permissions`, managed `disableBypassPermissionsMode` |
| Go SDK (added request) | ✅ | `sdk/go` + conformance tests + `samples/sdk/go` |
| Java SDK (added request) | ✅ | `sdk/java` (zero-dependency, JDK 17+) + conformance tests + `samples/sdk/java` |
| Cool NuGet icon (added request) | ✅ | `assets/icon.svg` / `icon.png`, packed into every NuGet package |
| Rotating spinner verb, elapsed time, live tokens (added request) | ✅ | Verb changes every ~15 s and per model call; `(16m 50s · ↓ 66.4k tokens · esc to interrupt)`; `docs/images/spinner.png` |

## Components

| Area | Status | Notes |
|---|---|---|
| Abstractions (messages, events, capabilities) | ✅ | Polymorphic, source-generated |
| Anthropic adapter | ✅ | Cache breakpoints, thinking signatures, count_tokens; verified live via DeepSeek's Anthropic endpoint |
| OpenAI Responses / Chat, Azure | ✅ | Verified live on Azure |
| DeepSeek | ✅ | `reasoning_content` round-trip within tool loops; verified live |
| Gemini | ✅ | Contract-tested (no live key available) |
| Ollama | ✅ | Contract-tested (no local Ollama during testing) |
| OpenAI-compatible quirk profiles | ✅ | 10 built-in profiles |
| Text tool protocol fallback | ✅ | Contract-tested |
| Retry/backoff, fallback chains, cost tracking | ✅ | |
| Agent loop, parallel tools, interrupts | ✅ | |
| Built-in tools (17) | ✅ | Read, Write, Edit, NotebookEdit, Glob, Grep, Bash, PowerShell, BashOutput, KillShell, WebFetch, WebSearch, TodoWrite, Agent, Skill, AskUserQuestion, ExitPlanMode (+ MCP resource tools) |
| Permission engine & modes | ✅ | Claude Code rule syntax, compound command analysis |
| Settings hierarchy (user/project/local/CLI/managed) | ✅ | `.claude/` compat |
| Memory files & `@` imports | ✅ | |
| Sessions, resume, fork, export | ✅ | |
| Checkpoints & rewind | ✅ | |
| Auto/manual compaction | ✅ | |
| Hooks (9 events) | ✅ | Claude Code JSON contract |
| Skills, commands, subagents, output styles | ✅ | |
| Plugins & marketplaces | ✅ | |
| MCP client (stdio, Streamable HTTP, tools/prompts/resources) | ✅ | Legacy SSE transport ⏳ |
| TUI | ✅ | Vim mode, Ctrl+R search, full-screen transcript ⏳ |
| Headless (text/json/stream-json, stream-json input) | ✅ | |
| JSON-RPC server (stdio, Content-Length, WebSocket + token) | ✅ | |
| SDKs: .NET, TypeScript, Python, Go, Java | ✅ | Rust ⏳ |
| OpenRPC schema | ✅ | Code generation from schema ⏳ |
| Tests | ✅ | 57 xUnit + 2 TS + 2 Python + 2 Go + 2 Java conformance, all passing |
| NativeAOT binaries | ✅ | win-x64, linux-x64, osx-arm64 built and smoke-tested in CI |
| Sandboxing, auto mode, worktrees, LSP tool, OpenTelemetry, Desktop, IDE | ⏳ | See PLAN.md |

## Real-LLM test log (2026-09-28)

| Scenario | Model | Result |
|---|---|---|
| Arithmetic (headless json) | azure:gpt-5-mini, deepseek:deepseek-v4-flash | ✅ |
| Write + run `fib.py` (tool loop) | Azure, DeepSeek, DeepSeek-via-Anthropic | ✅ |
| PowerShell script via AOT binary | DeepSeek | ✅ |
| .NET console app TodoCli (new/build/run, todo list) | Azure | ✅ `console-app-*.png` |
| Landing page from Indonesian prompt | DeepSeek | ✅ `web-app-*.png` (page renders) |
| CSV insights report via skill | DeepSeek | ✅ `skill-csv-report.png` |
| Notes MCP server (add/search) | Azure | ✅ `mcp-notes.png` |
| Plugin command from marketplace | Azure | ✅ `plugin-command.png` |
| Permission dialogs, plan mode | Azure | ✅ `permission-bash.png`, `plan-mode.png` |
| SDK samples (.NET in-proc, TS, Python, Go, Java) | Azure, DeepSeek | ✅ |

## Issues found and fixed during testing

- `-p` blocked forever on an idle stdin pipe → stdin is ignored after 1.5 s when a prompt argument exists.
- ConPTY child inherited redirected stdio → `STARTF_USESTDHANDLES` in the capture harness.
- Relative path rules stripped the dot of `.env`.
- Subcommand flags (`mcp add --scope`) were parsed as global options.
- Home directory's `~/.dotcode` was mistaken for a project root.
- Hooks that don't read stdin raised "pipe is being closed".
- Session list compared role case-sensitively ("(no prompt)" in Recent activity).
- ANSI themes leaked 16-color mode into other themes' previews.
- Long plans overflowed the plan dialog → height-capped, scrollable.
