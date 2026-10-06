#!/usr/bin/env python3
"""Create monthly summary workbook from data/expenses.csv using openpyxl.

Produces reports/monthly-summary.xlsx and prints a text summary to reports/summary_print.txt
"""
import csv
import os
from collections import defaultdict, OrderedDict
from datetime import datetime

import pandas as pd
from openpyxl import Workbook, load_workbook
from openpyxl.utils import get_column_letter
from openpyxl.styles import Font

DATA_CSV = os.path.join('data', 'expenses.csv')
REPORT_XLSX = os.path.join('reports', 'monthly-summary.xlsx')
SUMMARY_TXT = os.path.join('reports', 'summary_print.txt')

MONTHS = ['2026-07', '2026-08', '2026-09']


def read_expenses(path):
    df = pd.read_csv(path, parse_dates=['date'])
    df['month'] = df['date'].dt.strftime('%Y-%m')
    return df


def autofit_columns(ws):
    for col in ws.columns:
        max_len = 0
        col_letter = get_column_letter(col[0].column)
        for cell in col:
            try:
                val = str(cell.value)
            except Exception:
                val = ''
            if val:
                max_len = max(max_len, len(val))
        ws.column_dimensions[col_letter].width = min(50, max_len + 2)


def create_workbook(df):
    wb = Workbook()
    # remove default sheet
    default = wb.active
    wb.remove(default)

    category_set = sorted(df['category'].unique())

    # Keep map of month -> category subtotals start cell for referencing
    month_subtotal_cells = {}

    for month in MONTHS:
        ws = wb.create_sheet(title=month)
        month_df = df[df['month'] == month].copy()
        month_df.sort_values('date', inplace=True)

        # Header
        headers = ['Date', 'Description', 'Category', 'Amount']
        ws.append(headers)
        for cell in ws[1]:
            cell.font = Font(bold=True)

        # Write rows
        start_row = 2
        for _, row in month_df.iterrows():
            ws.append([row['date'].strftime('%Y-%m-%d'), row['description'], row['category'], int(row['amount'])])
        end_row = start_row + len(month_df) - 1 if len(month_df) > 0 else start_row

        # Category subtotal table header
        subtotal_header_row = end_row + 2
        ws.cell(row=subtotal_header_row, column=1, value='Category')
        ws.cell(row=subtotal_header_row, column=2, value='Subtotal')
        ws.cell(row=subtotal_header_row, column=1).font = Font(bold=True)
        ws.cell(row=subtotal_header_row, column=2).font = Font(bold=True)

        # Write category rows with SUMIF formulas referencing the Amount column (D)
        cat_row_start = subtotal_header_row + 1
        for i, cat in enumerate(category_set):
            r = cat_row_start + i
            ws.cell(row=r, column=1, value=cat)
            # SUMIF over category column (C) and amount column (D)
            criteria_range = f"C{start_row}:C{end_row}"
            sum_range = f"D{start_row}:D{end_row}"
            formula = f"=SUMIF({criteria_range}, \"{cat}\", {sum_range})"
            ws.cell(row=r, column=2, value=formula)

        month_subtotal_cells[month] = (ws.title, cat_row_start, cat_row_start + len(category_set) - 1)

        autofit_columns(ws)

    # Summary sheet
    s = wb.create_sheet(title='Summary')
    s.append(['Month', 'Total Amount', 'Number of Transactions'])
    for cell in s[1]:
        cell.font = Font(bold=True)

    for i, month in enumerate(MONTHS):
        row = 2 + i
        s.cell(row=row, column=1, value=month)
        # Total Amount formula referencing month sheet D column
        total_formula = f"=SUM('{month}'!D2:D1000)"
        s.cell(row=row, column=2, value=total_formula)
        # Number of transactions formula: COUNTA of Amount or COUNT
        count_formula = f"=COUNTA('{month}'!D2:D1000)"
        s.cell(row=row, column=3, value=count_formula)

    # Add an empty row, then Category x Month cross-table
    table_start_row = 2 + len(MONTHS) + 2
    s.cell(row=table_start_row, column=1, value='Category')
    for j, month in enumerate(MONTHS):
        s.cell(row=table_start_row, column=2 + j, value=month)
    for cell in s[table_start_row]:
        cell.font = Font(bold=True)

    for i, cat in enumerate(category_set):
        r = table_start_row + 1 + i
        s.cell(row=r, column=1, value=cat)
        for j, month in enumerate(MONTHS):
            # Reference the subtotal cell from the month sheet
            ws_name = month
            # Find the row of the category on the month sheet
            # We will construct a SUMIF that searches that month sheet to be robust
            # Build formula that sums on the month sheet categories
            # Example: =SUMIF('2026-07'!C2:C50, "Groceries", '2026-07'!D2:D50)
            # Use 1000 as safe end row
            criteria_range = f"'{ws_name}'!C2:C1000"
            sum_range = f"'{ws_name}'!D2:D1000"
            formula = f"=SUMIF({criteria_range}, \"{cat}\", {sum_range})"
            s.cell(row=r, column=2 + j, value=formula)

    autofit_columns(s)

    return wb


def reopen_and_print_summary(xlsx_path, txt_out):
    # Re-open workbook to ensure formulas are present, but compute the numeric results
    # from the source CSV so we can print concrete computed values (openpyxl does not evaluate formulas).
    wb = load_workbook(xlsx_path, data_only=False)
    s = wb['Summary']

    # Compute values from source data
    df = pd.read_csv(DATA_CSV, parse_dates=['date'])
    df['month'] = df['date'].dt.strftime('%Y-%m')

    month_stats = df.groupby('month')['amount'].agg(['sum', 'count']).reindex(MONTHS).fillna(0).astype(int)
    categories = sorted(df['category'].unique())
    cat_month = df.pivot_table(index='category', columns='month', values='amount', aggfunc='sum').reindex(index=categories, columns=MONTHS).fillna(0).astype(int)

    # Verify that subtotal cells in month sheets are formulas
    formula_checks = []
    for month in MONTHS:
        ws = wb[month]
        # scan column B for cells that start with '=' and have a category in column A
        month_formulas = []
        for row in ws.iter_rows(min_row=1, max_col=2):
            a, b = row
            if a.value in categories and isinstance(b.value, str) and b.value.startswith('='):
                month_formulas.append((a.value, b.value))
        formula_checks.append((month, len(month_formulas), month_formulas[:5]))  # include up to 5 examples

    # Verify summary formulas
    summary_formula_examples = []
    for r in range(2, 2 + len(MONTHS)):
        total_cell = s.cell(row=r, column=2).value
        count_cell = s.cell(row=r, column=3).value
        summary_formula_examples.append((r, total_cell, count_cell))

    # Build output
    lines = []
    lines.append('Summary sheet values (computed from source CSV):')
    lines.append('')
    lines.append('Month totals and transaction counts:')
    lines.append('')
    for month in MONTHS:
        total = int(month_stats.loc[month, 'sum'])
        count = int(month_stats.loc[month, 'count'])
        lines.append(f"{month}: Total Amount = {total}  |  Number of Transactions = {count}")
    lines.append('')
    lines.append('Category x Month table:')
    header = ['Category'] + MONTHS
    lines.append(','.join(header))
    for cat in categories:
        row_vals = [str(cat)]
        for month in MONTHS:
            row_vals.append(str(int(cat_month.loc[cat, month])))
        lines.append(','.join(row_vals))

    lines.append('')
    lines.append('Formula presence checks (examples):')
    for month, count, examples in formula_checks:
        lines.append(f"{month}: found {count} subtotal formula cells. Examples:")
        for ex in examples:
            lines.append(f"  Category: {ex[0]}  Formula: {ex[1]}")
    lines.append('')
    lines.append('Summary sheet formula examples:')
    for r, total_f, count_f in summary_formula_examples:
        lines.append(f"Row {r}: Total formula: {total_f}  Count formula: {count_f}")

    output = '\n'.join(lines)
    print(output)

    os.makedirs(os.path.dirname(txt_out), exist_ok=True)
    with open(txt_out, 'w', encoding='utf-8') as f:
        f.write(output)
        f.write('\n\nRuntime notes:\n')
        f.write('Ran scripts/monthly_summary.py which created reports/monthly-summary.xlsx and reports/summary_print.txt.\n')
        f.write('Computed printed values from the source CSV because openpyxl does not evaluate formulas.\n')


def main():
    df = read_expenses(DATA_CSV)
    os.makedirs('reports', exist_ok=True)
    wb = create_workbook(df)
    wb.save(REPORT_XLSX)
    # Re-open and print computed summary
    reopen_and_print_summary(REPORT_XLSX, SUMMARY_TXT)


if __name__ == '__main__':
    main()
