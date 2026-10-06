---
name: market-research
description: Research markets and competitors on the web with citations, comparisons and takeaways.
version: 1.0.0
requires:
  tools: [web_search, web_fetch, write_file]
permissions:
  network: true
  shell: false
---
# Market research

1. Restate the question and define scope (region, segment, time frame).
2. `web_search` with 3-5 varied queries (English and Bahasa Indonesia when the market is Indonesia).
3. `web_fetch` the 4-8 most authoritative sources (official sites, reports, reputable media). Note publication dates.
4. Extract facts into a table; mark estimates and conflicting numbers.
5. Competitor comparison: positioning, pricing, key features, strengths, weaknesses.
6. Synthesize: 3-5 key takeaways and implications for the user.

## Output (`research/<topic>.md`)
```
# <Topic> — research brief
_Date, scope_
## Key takeaways
## Market overview
## Competitors (table)
## Opportunities & risks
## Sources
1. Title — URL (accessed YYYY-MM-DD)
```
Never invent sources or numbers. If something can't be verified, say so.
