---
name: csv-insights
description: Analyze a CSV file and produce a Markdown insights report (summary statistics, top categories, trends). Use whenever the user asks to analyze, summarize or report on CSV/tabular data.
allowed-tools: Bash(python *), Read, Write
---

# CSV insights

Produce a clear, decision-ready Markdown report from a CSV file.

## Steps

1. Run the bundled analyzer to get reliable numbers (never compute statistics by eye):

   ```bash
   python "{baseDir}/analyze.py" <path-to.csv>
   ```

   It prints JSON with row count, per-column type, numeric stats (min/max/mean/sum) and top values for text columns.

2. Read the JSON and write `<csv-name>-report.md` next to the CSV with these sections:
   - **Overview** — rows, columns, time span if a date column exists
   - **Key numbers** — a table of the most important numeric columns (sum, mean, min, max)
   - **Top categories** — the leading values of the most informative text columns
   - **Insights** — 3–5 concrete observations backed by the numbers
   - **Recommendations** — 2–3 actionable next steps

3. Reply with a 3-line summary and the report path.

Keep numbers formatted with thousands separators and at most 2 decimals.
