<div align="center">

<img src="assets/icon.png" width="120" alt="DotCode logo">

# DotCode

**Agentic coding in your terminal — with any LLM.**
A .NET 10 port of the Claude Code experience, plus a harness SDK for .NET, TypeScript, Python, Go, Java and Rust.

*Built by **Gravicode Studios**, led by **Kang Fadhil**.*

🇮🇩 [Baca dalam Bahasa Indonesia](README.id.md)

</div>

![DotCode building a .NET console app](docs/images/console-app-working.png)

DotCode reads your codebase, edits files, runs commands and tests, and explains what it did — from a terminal UI that looks and feels like Claude Code (same layout, dialogs, diff view, spinner, shortcuts, slash commands and permission modes), but with your choice of model: **Anthropic, OpenAI, Azure OpenAI, Google Gemini, DeepSeek, Ollama (fully local) or any OpenAI-compatible server**. The same engine is exposed over JSON-RPC so your own applications can embed the agent.

## Highlights

- **Claude Code–style terminal UI** — streaming markdown, `● Tool(args)` / `⎿ result` blocks, colored diffs, animated spinner with shimmering verbs, todo list, permission and plan dialogs, `/` command menu, `@` file mentions, `!` bash mode, `#` memory, Esc-Esc rewind, prompt history with Ctrl+R search, Ctrl+O transcript viewer, vim mode, English / Indonesian UI, terminal notifications, 15 themes, glyph sets for any font (unicode / ascii / Nerd Font), custom themes, status line.
- **Any model, one normalized engine** — adapters for Anthropic Messages, OpenAI Responses & Chat, Azure, Gemini, DeepSeek, Ollama and OpenAI-compatible servers (quirk profiles), with prompt caching, reasoning round-trip, schema sanitizing, retries, fallback chains, cost tracking, and a text tool protocol for models without function calling. Switch models mid-session with `/model`.
- **Full agent toolbox** — Read, Write, Edit, Glob, Grep, LSP (definitions, references, hover, diagnostics via language servers), Bash (Git Bash on Windows), PowerShell, background shells, WebFetch, WebSearch, TodoWrite, subagents (Agent), Skills, AskUserQuestion, plan mode, notebooks, MCP tools & resources.
- **Safe by default** — permission modes (default, acceptEdits, plan, bypass via `--dangerously-skip-permissions`), Claude Code–compatible rules (`Bash(npm test:*)`, `Edit(/src/**)`, `WebFetch(domain:…)`), compound-command analysis, checkpoints and `/rewind`, hooks.
- **Extensible and compatible** — skills (`SKILL.md`), custom commands, subagents, hooks, output styles, plugins & marketplaces, MCP servers (stdio / HTTP). Reads `CLAUDE.md`, `AGENTS.md` and `.claude/` so existing setups keep working.
- **Headless & CI** — `dotcode -p` with text / json / stream-json output.
- **Harness SDK** — `dotcode serve` (JSON-RPC 2.0 over stdio or WebSocket) with SDKs for **.NET** (also in-process), **TypeScript**, **Python**, **Go**, **Java** and **Rust**: custom tools, permission handlers, streaming events, BYOK.
- **Fast and small** — NativeAOT single binary (~14 MB, instant start), source-generated JSON, no vendor SDK dependencies.

## Screenshots

All screenshots were captured from real sessions (Azure OpenAI gpt-5-mini and DeepSeek V4 Flash) using the scripted terminal harness in [`tools/DotCode.TermCapture`](tools/DotCode.TermCapture).

| | |
|---|---|
| **Welcome screen** ![](docs/images/welcome.png) | **Slash commands** ![](docs/images/slash-commands.png) |
| **Permission dialog** ![](docs/images/permission-bash.png) | **Plan mode** ![](docs/images/plan-mode.png) |
| **MCP server tools** ![](docs/images/mcp-notes.png) | **Model picker (multi-LLM)** ![](docs/images/model-picker.png) |
| **Context usage & cost** ![](docs/images/cost-context.png) | **Themes** ![](docs/images/theme-dracula.png) |

**Built with DotCode** — a landing page generated from a prompt in Bahasa Indonesia (DeepSeek), a .NET console app (Azure OpenAI), a CSV insights report via a skill, and a plugin command:

| Session | Result |
|---|---|
| ![](docs/images/web-app-done.png) | ![](docs/images/web-app-result.png) |
| ![](docs/images/skill-csv-report.png) | ![](docs/images/plugin-command.png) |

## Quick start

```bash
# 1. Install: download the dotcode binary for your OS (or: dotnet tool install -g DotCode.Cli)
# 2. Configure a provider (any one of these works)
export ANTHROPIC_API_KEY=...        # or OPENAI_API_KEY, GEMINI_API_KEY, DEEPSEEK_API_KEY,
                                    # AZURE_OPENAI_API_KEY + AZURE_OPENAI_ENDPOINT, OLLAMA_HOST
# 3. Go
cd your-project
dotcode
```

```bash
dotcode --model deepseek:deepseek-v4-flash         # pick a model
dotcode -p "summarize recent changes" --output-format json
dotcode mcp add notes node samples/mcp-server-notes/server.mjs
dotcode plugin marketplace add ./samples/marketplace && dotcode plugin install gravicode-toolkit@gravicode
dotcode --dangerously-skip-permissions             # sandboxes / CI only
```

Configure several providers at once in `~/.dotcode/settings.json`:

```jsonc
{
  "model": "anthropic:claude-sonnet-4-5",
  "models": { "fast": "openai:gpt-5-mini", "subagent": "ollama:qwen3-coder" },
  "providers": {
    "anthropic": { "type": "anthropic", "apiKey": "${env:ANTHROPIC_API_KEY}" },
    "openai":    { "type": "openai",    "apiKey": "${env:OPENAI_API_KEY}" },
    "ollama":    { "type": "ollama",    "baseUrl": "http://localhost:11434", "numCtx": 32768 }
  }
}
```

## SDK in 10 lines

```python
from dotcode_sdk import DotCodeClient, tool

@tool("get_exchange_rate", "Exchange rate", {"type": "object", "properties": {"from": {"type": "string"}, "to": {"type": "string"}}})
def rate(args): return f"1 {args['from']} = 16,250 {args['to']}"

async with DotCodeClient() as client:
    session = await client.create_session(model="azure:gpt-5-mini", tools=[rate],
                                          on_permission_request=lambda r: {"decision": "allow"})
    print((await session.send("How many IDR is 250 USD?"))["result"])
```

Same API in [.NET](docs/en/sdk.md#net), [TypeScript](docs/en/sdk.md#typescript), [Go](docs/en/sdk.md#go), [Java](docs/en/sdk.md#java) and [Rust](docs/en/sdk.md#rust).

## Documentation

| English | Bahasa Indonesia |
|---|---|
| [Getting started](docs/en/getting-started.md) | [Memulai](docs/id/memulai.md) |
| [Configuration](docs/en/configuration.md) | [Konfigurasi](docs/id/konfigurasi.md) |
| [LLM providers](docs/en/providers.md) | [Provider LLM](docs/id/provider.md) |
| [Interactive mode](docs/en/interactive-mode.md) | [Mode interaktif](docs/id/mode-interaktif.md) |
| [Permissions & safety](docs/en/permissions.md) | [Izin & keamanan](docs/id/izin.md) |
| [Tools](docs/en/tools.md) | [Tools](docs/id/tools.md) |
| [Extensions: skills, commands, agents, hooks, plugins, MCP](docs/en/extensions.md) | [Ekstensi](docs/id/ekstensi.md) |
| [Headless mode](docs/en/headless.md) | [Mode headless](docs/id/headless.md) |
| [SDK & protocol](docs/en/sdk.md) | [SDK & protokol](docs/id/sdk.md) |
| [Git worktrees](docs/en/worktrees.md) | [Git worktree](docs/id/worktree.md) |
| [Observability: audit log & OpenTelemetry](docs/en/observability.md) | [Observabilitas](docs/id/observabilitas.md) |
| [Architecture](docs/en/architecture.md) | [Arsitektur](docs/id/arsitektur.md) |
| [Development](docs/en/development.md) | [Pengembangan](docs/id/pengembangan.md) |

Roadmap: [PLAN.md](PLAN.md) · Progress: [Progress.md](Progress.md) · Design: [solution-design.md](solution-design.md)

## Repository layout

```
src/        DotCode.Abstractions · Providers · Engine · Protocol · Tui · Cli · Sdk
sdk/        typescript · python · go · java
schema/     protocol.schema.json (OpenRPC) · protocol-version.json
samples/    mcp-server-notes · skills/csv-insights · plugins/gravicode-toolkit · marketplace · sdk/{dotnet,typescript,python,go,java}
tests/      DotCode.Tests (xUnit: provider contracts, engine, TUI, SDK conformance)
tools/      DotCode.TermCapture (ConPTY screenshot harness + scenarios)
docs/       en · id · images
```

## Build from source

```bash
dotnet build DotCode.slnx
dotnet test tests/DotCode.Tests
dotnet publish src/DotCode.Cli -c Release -r win-x64 -o artifacts/win-x64    # NativeAOT
```

## Credits and legal

DotCode is an independent, clean-room project **built by Gravicode Studios, led by Kang Fadhil**. It is inspired by the publicly documented behavior of Anthropic's Claude Code and the architecture of the GitHub Copilot SDK; it contains no code from either. "Claude" and "Claude Code" are trademarks of Anthropic; other product names belong to their owners. Released under the [MIT License](LICENSE).
