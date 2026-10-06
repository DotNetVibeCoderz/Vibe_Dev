---
name: data-analysis
description: Analyse CSV/Excel/JSON data with Python (pandas), produce tables/charts and plain-language insights.
version: 1.0.0
requires:
  tools: [run_shell, write_file, read_file]
permissions:
  network: false
  shell: true
---
# Data analysis

1. Inspect: load with pandas (`python -m pip install --quiet pandas matplotlib openpyxl` if missing).
   Print shape, dtypes, head, missing values, duplicates.
2. Clean: fix types, trim strings, handle missing values explicitly (state the rule).
3. Analyse: answer the question with groupbys, pivots, trends; compute summary stats.
4. Visualize (optional): matplotlib PNGs saved to `analysis/figures/` with titles and labelled axes.
5. Validate: cross-check totals against the raw data.
6. Write `analysis/<topic>.py` (re-runnable) and `analysis/<topic>-findings.md`:
   - Question, data description, method, key numbers table, insights (plain language), caveats.

Always run the script and include the actual printed results — never fabricate numbers.
