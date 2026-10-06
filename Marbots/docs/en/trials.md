# Trials with a real LLM

[English](../en/trials.md) · [Bahasa Indonesia](../id/trials.md)

We ran Marbots against a real model (**Azure OpenAI `gpt-5-mini`**) on a fresh install: Boss Man plus the starter team
(Atlas, Alice, Quinn, Wren), default permission profiles, Tavily web search, and the Filesystem, Sequential Thinking and
Time MCP servers installed. Seven jobs ran **concurrently**, plus an A2A call, driven by
[`samples/trials/run_trials.py`](../../samples/trials/run_trials.py). An auto-approver played the human and approved
shell commands after 60 seconds. Every artifact the bots produced is in
[`samples/trials/outputs`](../../samples/trials/outputs) and the raw results are in
[`samples/trials/trial-results.json`](../../samples/trials/trial-results.json).

## Results

| Job | Who worked | Result | Time |
|---|---|---|---|
| **Web app**: "Kas Warung" bookkeeping app for small shops (Indonesian prompt) | Boss Man → Alice (build) → Quinn (test, depends on Alice) | ✅ Static app (HTML/CSS/JS, localStorage, Rupiah formatting, daily/monthly balance); Quinn wrote and ran 4 pytest checks: 4 passed | 5.0 min |
| **Bilingual document**: onboarding guide EN + ID + print-ready HTML | Boss Man → Wren | ✅ `onboarding-en.md`, `onboarding-id.md`, `onboarding.html` (from the report-writing skill template) and a bilingual term map | 2.7 min |
| **Script + spreadsheet**: expense CSV → monthly XLSX with formulas, verified independently | Boss Man → Alice → Quinn | ✅ Sample data, `monthly_summary.py`, `monthly-summary.xlsx` (sheet per month, SUM/SUMIF formulas, summary sheet); Quinn re-ran it and cross-checked totals with pandas | 6.8 min |
| **Web research** with citations | Atlas (direct chat) | ✅ `research/pos-indonesia.md`: Kasir Pintar, Pawoon and iREAP compared on pricing, features, strengths and weaknesses, with primary-source links | 2.0 min |
| **MCP + conversational bot creation** | Boss Man creates **Mira** (data-engineer template + `filesystem` + `time` MCP) → Mira | ✅ `mcp__time__get_current_time` ×2 and `mcp__filesystem__write_file` → `reports/world-clock.md`. Mira hit a missing-folder error and recovered on her own with `mcp__filesystem__create_directory` | 1.0 min |
| **Schedule + memory** (Indonesian) | Boss Man | ✅ `remember` stored the company name and language preference; `schedule_task` created *every Monday 08:00 WIB → Atlas, AI news brief* (cron `0 8 * * 1`, next run computed) | 0.6 min |
| **Research → landing page** with a dependency | Boss Man → Atlas (research) → Alice (page, depends on Atlas) | ✅ `research/kopi-competitors.md` and `apps/kopi-kilat/index.html` (hero, 3 advantages vs competitors, 3-step ordering, testimonials, WhatsApp CTA) | 4.7 min |
| **A2A** `message/send` to Wren | Wren via JSON-RPC | ✅ `completed`, answer returned in the A2A task | < 1 min |

Totals for the run: **16 tasks** (root and delegated), **16 completed, 0 failed**, ~1.02 M tokens, **≈ $0.41** estimated
model cost.

## What the bots built

**Kas Warung**, built by Alice and tested by Quinn. We added three transactions with Playwright: the balance updates correctly (185,000 − 64,000 + 72,000 = Rp 193,000).

![Kas Warung](../images/trial-kas-warung.png)

**Kopi Kilat landing page**, built by Alice from Atlas's competitor research:

![Kopi Kilat](../images/trial-kopi-kilat.png)

**Bilingual onboarding guide** (HTML version), written by Wren using the report-writing skill template:

![Onboarding](../images/trial-onboarding-html.png)

## What the platform did

Delegation, with the live activity of three bots in one thread:

![Web app thread](../images/chat-webapp.png)

Web research with `web_search` and `web_fetch` steps visible in the activity panel:

![Research thread](../images/chat-research.png)

The MCP showcase: Boss Man creates Mira and delegates the MCP work to her:

![MCP thread](../images/chat-mcp.png)

A shell approval while Alice runs the spreadsheet script, and the Office view at the same moment:

![Approval](../images/chat-approval.png)
![Office live](../images/office-live.png)

Auto-Learn (Boss Man, `MemoryOnly`) saved preferences from the run, for example *"User prefers to communicate and
receive deliverables in Indonesian"*. They appear on the Memory page with provenance `autolearn:<task>`.

![Memory](../images/memory.png)

The fleet dashboard after the run:

![Dashboard](../images/dashboard.png)

## Honest notes

- The spreadsheet job asked for 60 sample rows; Alice generated 93 (about 30 per month). Totals and formulas were
  correct and Quinn's independent check matched.
- Quinn's Kas Warung tests check HTML/JS structure statically. Quinn pointed out that a browser E2E test would be the
  next step; the screenshots above were produced by such a run.
- The community `mcp-server-sqlite` package failed to start against the current MCP Python library during testing.
  Marbots reported the error cleanly, and that entry was removed from the default gallery.

## Reproduce

```bash
dotnet run --project src/Marbots.Server              # with a provider configured
python samples/trials/run_trials.py --approve-delay 30
node tools/screenshots/shoot.mjs http://localhost:5170 docs/images
```

---
*Marbots — Created by Gravicode Studios, led by Kang Fadhil.*
