---
name: financial-analysis
description: Financial modelling and analysis: budgets, forecasts, unit economics, ratios and scenarios.
version: 1.0.0
requires:
  tools: [run_shell, write_file]
permissions:
  network: false
  shell: true
---
# Financial analysis

1. List assumptions explicitly in an Assumptions table (growth, prices, costs, FX, tax).
2. Build the model as a Python script producing CSV/XLSX (see spreadsheet-builder) so it is reproducible.
3. Common outputs: P&L projection, cash flow, break-even, unit economics (CAC, LTV, payback), ratios (gross margin,
   EBITDA margin, current ratio, DSO).
4. Scenarios: base / optimistic / pessimistic with sensitivity on the 2-3 most influential drivers.
5. Check: totals reconcile, signs correct, units and currency labelled (IDR/USD), no circular references.
6. Narrative: what the numbers say, key risks, recommendation.

Disclaimer: analysis for planning purposes, not investment, tax or accounting advice.
