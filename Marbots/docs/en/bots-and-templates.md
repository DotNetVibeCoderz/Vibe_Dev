# Bots and the template gallery

[English](../en/bots-and-templates.md) · [Bahasa Indonesia](../id/bots-and-templates.md)

## Five ways to create a bot

1. **Template gallery** (Templates) — click **Hire** for a ready-made role, or **Customise** to adjust it first.
2. **Create bot** (Team → Create bot) — fill in the form yourself.
3. **Ask Boss Man** — *"Create a bot named Sari, a UX designer, with web and files, auto-learn memory only."*
4. **CLI** — `marbots bot hire ux-designer --name Sari`
5. **API / SDK** — `POST /api/v1/bots` or `client.Bots.HireAsync("ux-designer", "Sari")`

![Template gallery](../images/templates.png)

## The template gallery

58 built-in templates in 11 categories:

| Category | Examples |
|---|---|
| Software Development | Software Engineer, Frontend/Backend Developer, .NET Architect, Mobile Developer, DevOps, QA, Security, Code Reviewer, Data/ML Engineer, Tech Lead, Automation Scripter |
| Design & Creative | UX Designer, UI Designer, Brand Designer, Graphic Designer, Copywriter, Video Script Producer, Game Designer |
| Product & Management | Product Manager, Project Manager, Scrum Master, Business Analyst, Chief of Staff |
| Business & Operations | Strategy Advisor, Financial Analyst, Accountant, Operations Manager, Procurement, Supply Chain |
| Marketing & Sales | Marketing Strategist, SEO, Social Media, SDR, Account Executive, Market Researcher |
| People & HR | HR Generalist, Recruiter, Learning & Development |
| Legal & Compliance | Legal Assistant, Compliance Officer |
| Customer & Support | Customer Support Agent, Customer Success Manager |
| Content & Documentation | Technical Writer, Translator EN-ID, Editor, Report Writer |
| Data & Research | Researcher, Data Analyst, Scientific Research Assistant |
| Education & Professions | Teacher/Tutor, Healthcare Admin, Building Architect Assistant, Property Consultant, Event Planner, Personal Assistant, Content Creator |

Built-in templates are read-only; **Duplicate** creates an editable copy. Create your own with **Create template**:
name, category, role, tags, marble colour, instructions/persona, permission profile, auto-learn mode, kernel
functions, skills and MCP servers. The catalogue source lives in `tools/gen_templates.py`.

## The bot form

![Bot editor](../images/bot-editor.png)

| Section | Fields |
|---|---|
| Identity | Name, marble colour, role, description, persona and instructions |
| Brain | Model profile, max steps per task, auto-learn mode, compaction threshold, short-/long-term memory |
| Capabilities | Kernel function packs, skills, MCP servers |
| Boundaries | Permission profile, host |

The right panel shows the **effective capabilities** and **security notes** before you save, for example
"Shell commands ask for your approval" or "Autonomous + shell can run commands without asking".

## Choosing a model per bot

Every bot has a **Model** setting (Team → Edit → Brain, the template editor, `create_bot`, the API, CLI and SDKs):

| Setting | Meaning |
|---|---|
| `default` | Follow the workspace default model (Settings → **Default model**). New bots and templates start here. |
| `provider/model` | A specific model, e.g. `azure/gpt-5.6-luna` or `deepseek/deepseek-v4-flash`. |
| profile name | A named profile from Settings (model + fallbacks), shared by many bots. |

If a bot's model is unknown or its provider is unavailable, the bot uses the default model instead, and a failing
model falls back to the default at run time. Each task records the model that actually served it (Tasks page, API
`task.model`). Providers list their models in Settings → *Models / deployments* (or `Providers[].Models` in config),
and those appear in every model picker.

![Model picker](../images/bot-model.png)

```bash
marbots bot model atlas azure/gpt-5.6-luna     # set
marbots bot model atlas default                # back to the workspace default
marbots models default azure/gpt-5-mini        # change the default for all "default" bots
```

## Sub-agents (optional)

Switch on the **subagents** tool pack to let a bot split independent work across temporary copies of itself:

- web: the bot editor's tool packs;
- CLI: `marbots bot packs nova files,shell,…,subagents`;
- Boss Man: `create_bot` with `kernel_functions` including `subagents`;
- SDK: add `"subagents"` to `KernelFunctions`.

The bot then has `spawn_subagents`. It takes up to 6 self-contained sub-tasks and runs them in parallel, then returns
every report together.

Each sub-agent:

- inherits the persona, skills, model, computer (host), container and tools;
- shares the thread's workspace;
- cannot spawn or delegate further, and does not auto-learn.

The work counts toward the parent bot: status, cost, and its robot in the office. Use it for parts that don't depend
on each other, such as researching several topics or writing several files. Use Boss Man's `delegate_tasks` when
different roles are needed.

## Permission profiles

| Profile | Read | Write workspace | Web | Shell | Delete | External messages |
|---|---|---|---|---|---|---|
| `read-only` | ✅ | ❌ | ✅ | ❌ | ❌ | ❌ |
| `workspace-write` | ✅ | ✅ | ✅ | ❌ | ask | ask |
| `developer-safe` (default) | ✅ | ✅ | ✅ | ask | ask | ask |
| `autonomous` | ✅ | ✅ | ✅ | ✅ | ✅ | ask |
| `manager` (Boss Man) | ✅ | ✅ | ✅ | ask | ask | ask |

## Managing bots

From **Team**: chat, edit, pause/resume (paused bots refuse new work), export, delete. Boss Man can be tuned (model,
skills, extra packs) but not deleted, and its manager tools are protected.

![Team](../images/team.png)

## Export and import (`.marbot`)

A `.marbot` file is a ZIP package:

```
researcher.marbot
├── manifest.json        # schema version + bot definition
├── persona.md
├── skills.lock.json     # skill names and versions
├── exported-skills/     # locally installed skills, bundled
├── mcp.lock.json        # MCP server configs with secret *references* only
├── schedules.json       # exported disabled
├── memory.json          # optional, sanitised
└── checksums.json       # SHA-256 of every file
```

- Secrets are **never** exported. MCP environment values become `secret:NAME` references.
- Import verifies checksums and rejects tampered packages, rejects unsafe paths, gives the bot a new id, installs
  bundled skills, and adds MCP servers **disabled** with trust `Unverified` until you review them.

```bash
marbots bot export atlas -o atlas.marbot --memory
marbots bot import atlas.marbot
```

---
*Marbots — Created by Gravicode Studios, led by Kang Fadhil.*
