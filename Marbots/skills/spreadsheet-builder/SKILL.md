---
name: spreadsheet-builder
description: Create spreadsheets (CSV and .xlsx with formulas/formatting via openpyxl) for plans, budgets and trackers.
version: 1.0.0
requires:
  tools: [run_shell, write_file]
permissions:
  network: false
  shell: true
---
# Spreadsheet builder

- Simple tables → CSV (`csv` module, UTF-8 with BOM `utf-8-sig` so Excel opens it correctly).
- Formatted workbooks → `.xlsx` via openpyxl (`python -m pip install --quiet openpyxl`).

## openpyxl recipe
```python
from openpyxl import Workbook
from openpyxl.styles import Font, PatternFill, Alignment
wb = Workbook(); ws = wb.active; ws.title = "Budget"
ws.append(["Item", "Qty", "Unit price", "Total"])
for c in ws[1]: c.font = Font(bold=True); c.fill = PatternFill("solid", fgColor="DDE3F0")
ws.append(["Laptop", 2, 15000000, "=B2*C2"])
ws["D10"] = "=SUM(D2:D9)"   # use formulas, not hard-coded totals
ws.column_dimensions["A"].width = 28
ws.freeze_panes = "A2"
wb.save("sheets/budget.xlsx")
```
- Use formulas for totals so the sheet stays live; number formats like `'#,##0'`.
- Verify by re-opening with openpyxl and printing the sheet's values.
