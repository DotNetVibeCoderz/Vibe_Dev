---
name: report-writing
description: Write clear, well-structured business reports, briefs and executive summaries in Markdown or HTML.
version: 1.0.0
requires:
  tools: [write_file]
permissions:
  network: false
  shell: false
---
# Report writing

Structure (pyramid principle — conclusion first):
1. **Title + metadata** (date, author bot, audience).
2. **Executive summary** – 3-5 bullets: the answer, why it matters, recommended action.
3. **Context** – the question and scope.
4. **Findings** – one section per finding; each starts with a one-sentence claim, then evidence (tables, numbers, quotes with sources).
5. **Recommendations** – specific, owner + timeline when possible.
6. **Appendix** – methodology, data, sources.

Style: short paragraphs, active voice, numbers with units, tables for comparisons, no filler.
For HTML output, produce a single self-contained file with print-friendly CSS.
Save to `reports/<slug>.md` (or `.html`) and mention the path.
