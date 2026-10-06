<p align="center"><img src="src/Marbots.Server/wwwroot/favicon.svg" width="72" alt="Marbots marble logo"></p>

<h1 align="center">Marbots — Marvelous Bots</h1>

<p align="center"><b>Build, hire, teach and orchestrate a team of AI coworkers from one platform.</b><br>
Multi-agent collaboration on .NET 10 · Boss Man orchestration · Skills · MCP · A2A · SDKs</p>

<p align="center"><a href="README.id.md">Bahasa Indonesia</a> · <a href="docs/en/index.md">Documentation</a> · <a href="docs/id/index.md">Dokumentasi</a> · <a href="PLAN.md">Roadmap</a> · <a href="Progress.md">Progress</a></p>

<p align="center"><i>Created by Gravicode Studios, led by Kang Fadhil · Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil</i></p>

---

![Marbots chat with Boss Man delegating to the team](docs/images/chat-webapp.png)

## What is Marbots?

Marbots treats AI agents as **durable teammates**. Each has a name, a persona, memory, skills, tools and clear
permission boundaries. You give a goal to **Boss Man**. He plans it, splits it into sub-tasks, hands them to the right
teammates in parallel, waits for dependencies, reviews the results and reports back. You can also talk to any bot directly.

- **Boss Man orchestration**: delegation DAG with parallel fan-out, dependencies, cancellation and depth limits.
- **Per-bot models**: each bot can run on its own model (`provider/model` or a profile); bots without one use the workspace default, and unusable models fall back to it.
- **Durable bots**: persona, short- and long-term memory (BM25 with provenance), automatic context compaction, and optional Auto-Learn.
- **Template gallery**: 58 ready-made roles across 11 categories (engineering, design, product, finance, HR, legal,
  marketing, support, education and more). Create your own templates with name, instructions, MCP and skills.
- **Skills**: `SKILL.md` packages (Claude/Agent Skills and OpenClaw compatible) with progressive disclosure; 20 built in; install from git.
- **MCP**: stdio and HTTP client, a curated gallery, workspace-scoped servers, custom servers, secret references.
- **Kernel functions**: files, grep, shell (PowerShell/bash), web search/fetch, memory, todo.
- **Safety by design**: a deterministic policy engine with permission profiles, human approvals (once or per thread),
  encrypted secrets, path sandboxing, and prompt-injection boundaries. A dangerous *skip approvals* mode exists for
  sandboxes (Settings, `marbots approvals skip on`, or `--dangerously-skip-approvals`).
- **Scheduler**: cron and one-off jobs with time zones.
- **Observability**: a single event stream feeds the live chat activity, the **Office** floor plan, the dashboard, SSE and the CLI.
- **Interop**: REST + SSE API, OpenAPI, **A2A** agent cards and JSON-RPC, `.marbot` export/import without secrets.
- **SDKs**: .NET, Python, TypeScript and Go, plus the `marbots` CLI with themes.
- **Bilingual**: UI and docs in English and Bahasa Indonesia.

## Quick start

```bash
git clone https://github.com/DotNetVibeCoderz/Vibe_Dev.git && cd Vibe_Dev/Marbots
dotnet run --project src/Marbots.Server        # http://localhost:5170
```

Then open **Settings** and add a model provider (Azure OpenAI or any OpenAI-compatible endpoint), or configure it with
environment variables:

```bash
export Marbots__Providers__0__Name=azure Marbots__Providers__0__Kind=azure-openai \
       Marbots__Providers__0__Endpoint=https://<resource>.openai.azure.com/ Marbots__Providers__0__ApiKey=<key> \
       Marbots__ModelProfiles__0__Name=default Marbots__ModelProfiles__0__Provider=azure Marbots__ModelProfiles__0__Model=gpt-5-mini
```

Full walkthrough: [Getting started](docs/en/getting-started.md).

## Screenshots

| | |
|---|---|
| ![Team](docs/images/team.png) **Team**: durable bots with roles, tools and status | ![Template gallery](docs/images/templates.png) **Template gallery**: 58 roles, search and categories |
| ![Bot editor](docs/images/bot-editor.png) **Bot editor**: persona, brain, capabilities, boundaries and live security notes | ![Approvals](docs/images/chat-approval.png) **Approvals**: risky actions wait for you, inline in the chat |
| ![Office](docs/images/office-live.png) **Office**: live floor plan; bots walk to the station that matches their work | ![Dashboard](docs/images/dashboard.png) **Dashboard**: tasks, tokens, cost per bot, hosts, MCP health |
| ![Skills](docs/images/skills.png) **Skills gallery**: built-in, installed and auto-learned drafts | ![MCP](docs/images/mcp.png) **MCP gallery**: install, test and list tools |
| ![Research](docs/images/chat-research.png) **Web research** with cited sources | ![Dark mode](docs/images/chat-webapp-dark.png) **Dark mode** |
| ![Indonesian UI](docs/images/chat-id.png) **Bahasa Indonesia UI** (EN/ID switch, bottom left) | ![Schedules](docs/images/schedules.png) **Schedules**: cron jobs created by Boss Man or by hand |

## Built by the bots (real LLM trials)

We ran seven jobs concurrently against Azure OpenAI `gpt-5-mini`: **16/16 tasks completed, ≈ $0.41** in total.
See [Trials](docs/en/trials.md) for details and [`samples/trials/outputs`](samples/trials/outputs) for every artifact.

| Kas Warung bookkeeping app (Alice builds, Quinn tests) | Kopi Kilat landing page (Atlas researches → Alice builds) |
|---|---|
| ![Kas Warung](docs/images/trial-kas-warung.png) | ![Kopi Kilat](docs/images/trial-kopi-kilat.png) |

The run also produced a bilingual onboarding guide (MD + print-ready HTML), an expense-report script with an XLSX built
from SUM/SUMIF formulas and verified independently with pandas, a cited market-research brief, an MCP-driven report
written by a bot that Boss Man created mid-conversation, a recurring cron schedule, and an A2A exchange.

## CLI

```text
$ marbots status
Marbots 0.1.0
Created by Gravicode Studios, led by Kang Fadhil
Model: default → azure/gpt-5-mini
Team:  6 bots, 0 working

$ marbots chat wren "In one sentence, what is Marbots?"
Marbots is a multi-agent workspace by Gravicode Studios (led by Kang Fadhil) for building, coordinating,
and running autonomous agents within a shared project environment.
1 steps · 2,704 tokens · $0.0011
```

## SDKs

```python
from marbots_sdk import MarbotsClient, ModelRef  # pip install marbots-sdk
mb = MarbotsClient()
mb.bots.set_model("atlas", ModelRef.of("azure", "gpt-5.6-luna"))   # every bot can have its own model
print(mb.chat("boss-man", "Plan a 3-step product launch"))
```

```csharp
using var mb = new MarbotsClient(new Uri("http://localhost:5170"));   // dotnet add package Marbots.Sdk
var t = await mb.Threads.CreateAsync("atlas");
var r = await mb.Threads.SendAsync(t.Id, "Top 3 AI news today", wait: true);
```

TypeScript (`@gravicode/marbots`) and Go (`github.com/DotNetVibeCoderz/Vibe_Dev/Marbots/sdk/go`) are also available. See
[API, A2A, SDKs and CLI](docs/en/api-and-sdks.md).

## Repository layout

```
src/
  Marbots.Abstractions   contracts, events, source-generated JSON
  Marbots.Storage        SQLite (WAL): documents, messages, events, FTS5 memory
  Marbots.Providers      Azure OpenAI / OpenAI-compatible client, mock
  Marbots.Kernel         built-in tools + workspace sandboxing
  Marbots.Runtime        engine, agent loop, delegation, memory, compaction, skills, MCP, policy, scheduler
  Marbots.Server         ASP.NET Core: REST/SSE API, A2A, Blazor UI
  Marbots.Sdk            .NET SDK
  Marbots.Cli            marbots CLI
sdk/python · sdk/typescript · sdk/go
skills/                  20 built-in SKILL.md packages
tests/Marbots.Tests      58 unit + end-to-end runtime tests (mock LLM)
samples/trials           real-LLM trial harness, results and bot-made artifacts
docs/en · docs/id        bilingual documentation
tools/                   template catalogue generator, screenshot scripts
```

## Develop

```bash
dotnet build Marbots.slnx
dotnet test tests/Marbots.Tests                         # 58 tests, no network needed
dotnet test tests/Marbots.Tests --filter "FullyQualifiedName~EngineTests"
```

Design: [solution-design.md](solution-design.md) · Roadmap: [PLAN.md](PLAN.md) · Status: [Progress.md](Progress.md)

## License

MIT. Created by **Gravicode Studios**, led by **Kang Fadhil**.
