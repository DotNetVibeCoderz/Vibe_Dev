# Skills

[English](../en/skills.md) · [Bahasa Indonesia](../id/skills.md)

A **skill** is a folder with a `SKILL.md` file (plus optional `scripts/`, `references/`, `templates/`, `assets/`).
The format is compatible with Claude/Agent Skills and OpenClaw-style skills.

```markdown
---
name: weekly-report
description: Produce the weekly KPI report in our house style
version: 1.0.0
requires:
  tools: [read_file, write_file]
permissions:
  network: false
  shell: false
---

# Weekly report
1. Read data/kpi.csv ...
```

## Progressive disclosure

Only each skill's **name and description** go into a bot's system prompt. When a bot decides to use one, it calls
`load_skill` to read the full instructions, and `read_skill_file` for bundled templates or references. Bots can know
about many skills without filling their context window.

## Built-in skills

`api-design`, `bilingual-docs`, `code-review`, `data-analysis`, `docker-devops`, `dotnet-engineering`,
`financial-analysis`, `legal-review`, `market-research`, `marketing-copy`, `meeting-notes`, `product-requirements`,
`python-scripting`, `recruiting`, `report-writing` (with an HTML report template), `security-review`,
`spreadsheet-builder`, `test-automation`, `ux-design-brief`, `web-app-builder`.

## Installing and creating skills

![Skills gallery](../images/skills.png)

- **From git or a folder**: Skills → *Install*, or `marbots skills install https://github.com/org/skills.git`. Every
  `SKILL.md` found is copied into `data/skills/`.
- **Create in the UI**: Skills → *New skill*.
- **Enable per bot**: Team → Edit → Skills. Boss Man has `*` (all skills).

Trust labels: `Marbots Verified` (built-in), `Local` (installed), `Unverified` (auto-learn drafts). The table shows
whether a skill needs shell or network and whether it bundles scripts. Review scripts before giving a skill to a bot
with shell access.

## Auto-learned skills

Bots in `SuggestSkills` mode can draft new skills from successful multi-step tasks. Drafts land in **Waiting for
review** on the Skills page. **Publish skill** moves a draft into `data/skills`; **Discard** deletes it.
A learned skill never grants new permissions: it runs inside the bot's existing permission profile.

---
*Marbots — Created by Gravicode Studios, led by Kang Fadhil.*
