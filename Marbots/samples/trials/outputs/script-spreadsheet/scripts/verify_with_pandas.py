import pandas as pd
from openpyxl import load_workbook
from pathlib import Path

CSV_PATH = Path('data/expenses.csv')
WB_PATH = Path('reports/monthly-summary.xlsx')
REPORT_PATH = Path('reports/verification.txt')
MONTHS = ['2026-07','2026-08','2026-09']

def compute_from_csv():
    # Read CSV robustly: handle lowercase headers (e.g., 'date') and different capitalizations
    df = pd.read_csv(CSV_PATH)
    # normalize column names to lowercase
    df.columns = [c.strip() for c in df.columns]
    lc = {c: c.lower() for c in df.columns}
    df.rename(columns=lc, inplace=True)
    # detect date column
    date_col = None
    for col in df.columns:
        if 'date' == col or col.endswith('date') or 'date' in col:
            date_col = col
            break
    if date_col is None:
        raise ValueError("No date column found in CSV")
    # parse dates
    df[date_col] = pd.to_datetime(df[date_col])
    df['ym'] = df[date_col].dt.to_period('M').astype(str)
    # ensure Amount column exists (case-insensitive)
    amount_col = None
    for col in df.columns:
        if col.lower() == 'amount' or 'amount' in col:
            amount_col = col
            break
    if amount_col is None:
        raise ValueError("No Amount column found in CSV")
    # normalize Amount to numeric
    df[amount_col] = pd.to_numeric(df[amount_col], errors='coerce').fillna(0).astype(int)
    # standardize column name
    df.rename(columns={amount_col: 'Amount'}, inplace=True)
    results = {}
    # detect category column case-insensitively
    cat_col = None
    for col in df.columns:
        if col.lower() == 'category' or 'category' in col.lower():
            cat_col = col
            break
    if cat_col is None:
        raise ValueError('No Category column found in CSV')
    categories = sorted(df[cat_col].unique())
    # standardize category column name
    df.rename(columns={cat_col: 'Category'}, inplace=True)
    for m in MONTHS:
        sub = df[df['ym']==m]
        total = int(sub['Amount'].sum()) if not sub.empty else 0
        count = int(len(sub))
        cat_totals = sub.groupby('Category')['Amount'].sum().to_dict()
        # ensure all categories present with 0 if missing
        for c in categories:
            cat_totals.setdefault(c, 0)
        results[m] = {
            'total_amount': total,
            'num_transactions': count,
            'category_totals': {c:int(cat_totals[c]) for c in sorted(cat_totals)}
        }
    return results, categories


def find_in_sheet(sheet, value):
    for row in sheet.iter_rows(values_only=False):
        for cell in row:
            if cell.value == value:
                return cell.row, cell.column
    return None, None


def load_workbook_objects():
    wb_values = load_workbook(WB_PATH, data_only=True)
    wb_formulas = load_workbook(WB_PATH, data_only=False)
    return wb_values, wb_formulas


def read_summary_sheet(wb_values, wb_formulas):
    summary = wb_formulas['Summary']
    summary_values = wb_values['Summary']
    return summary, summary_values


def main():
    report_lines = []
    if not CSV_PATH.exists():
        report_lines.append(f"ERROR: Missing CSV file at {CSV_PATH}\n")
        Path('reports').mkdir(parents=True, exist_ok=True)
        REPORT_PATH.write_text('\n'.join(report_lines))
        return
    if not WB_PATH.exists():
        report_lines.append(f"ERROR: Missing workbook at {WB_PATH}\n")
        REPORT_PATH.write_text('\n'.join(report_lines))
        return

    computed, categories = compute_from_csv()

    # header
    report_lines.append('Verification report for reports/monthly-summary.xlsx')
    report_lines.append('Using data/expenses.csv computed with pandas')
    report_lines.append('')

    # list computed totals
    report_lines.append('Pandas-computed totals per month:')
    for m in MONTHS:
        r = computed[m]
        report_lines.append(f"- {m}: total_amount={r['total_amount']}  num_transactions={r['num_transactions']}")
    report_lines.append('')

    report_lines.append('Pandas-computed category totals (Category x Month):')
    header = 'Category,' + ','.join(MONTHS)
    report_lines.append(header)
    for c in categories:
        row = [c] + [str(computed[m]['category_totals'].get(c,0)) for m in MONTHS]
        report_lines.append(','.join(row))
    report_lines.append('')

    # load workbook
    wb_values, wb_formulas = load_workbook_objects()
    summary, summary_values = read_summary_sheet(wb_values, wb_formulas)

    report_lines.append("Summary sheet checks:\n")

    # For each month, find the cell in Summary that equals the month string
    month_checks = {}
    for m in MONTHS:
        r,c = find_in_sheet(summary, m)
        if r is None:
            report_lines.append(f"- Month row for {m}: NOT FOUND in Summary sheet -> FAIL")
            month_checks[m] = {'found':False}
            continue
        # assume Total is in a cell to the right; we find numeric/formula cells in same row
        # Heuristic: find first numeric/formula cell after month cell (max 6 cols)
        total_cell_formula = None
        total_cell_value = None
        count_cell_formula = None
        count_cell_value = None
        for offset in range(1,6):
            cell_formula = summary.cell(row=r, column=c+offset)
            cell_value = summary_values.cell(row=r, column=c+offset)
            if cell_formula.value and isinstance(cell_formula.value, str) and cell_formula.value.strip().startswith('='):
                # use first formula as total, next formula as count
                if total_cell_formula is None:
                    total_cell_formula = cell_formula
                    total_cell_value = cell_value
                    continue
                if count_cell_formula is None:
                    count_cell_formula = cell_formula
                    count_cell_value = cell_value
                    break
        month_checks[m] = {
            'found':True,
            'row':r,
            'col':c,
            'total_formula': total_cell_formula.value if total_cell_formula else None,
            'total_cached_value': total_cell_value.value if total_cell_value else None,
            'count_formula': count_cell_formula.value if count_cell_formula else None,
            'count_cached_value': count_cell_value.value if count_cell_value else None,
        }

        # write details
        tf = month_checks[m]['total_formula']
        cv = month_checks[m]['total_cached_value']
        cf = month_checks[m]['count_formula']
        cc = month_checks[m]['count_cached_value']

        report_lines.append(f"- {m} row found at Summary!{r},{c}")
        report_lines.append(f"  Total formula cell: {tf}" if tf else "  Total formula cell: NOT FOUND")
        report_lines.append(f"  Total cached value: {cv}" if cv is not None else "  Total cached value: <no cached numeric value>")
        report_lines.append(f"  Count formula cell: {cf}" if cf else "  Count formula cell: NOT FOUND")
        report_lines.append(f"  Count cached value: {cc}" if cc is not None else "  Count cached value: <no cached numeric value>")

        # compare cached values if present
        expected_total = computed[m]['total_amount']
        expected_count = computed[m]['num_transactions']
        total_match = None
        count_match = None
        if cv is not None:
            try:
                total_match = (int(cv) == expected_total)
            except Exception:
                total_match = False
        if cc is not None:
            try:
                count_match = (int(cc) == expected_count)
            except Exception:
                count_match = False

        report_lines.append(f"  Expected total (pandas): {expected_total}")
        report_lines.append(f"  Expected count (pandas): {expected_count}")
        if total_match is True:
            report_lines.append("  Month total numeric match: PASS")
        elif total_match is False:
            report_lines.append(f"  Month total numeric match: FAIL (workbook has {cv})")
        else:
            report_lines.append("  Month total numeric match: N/A (no cached numeric value to compare)")
        if count_match is True:
            report_lines.append("  Transaction count numeric match: PASS")
        elif count_match is False:
            report_lines.append(f"  Transaction count numeric match: FAIL (workbook has {cc})")
        else:
            report_lines.append("  Transaction count numeric match: N/A (no cached numeric value to compare)")

    report_lines.append('\nCategory x Month checks (Summary sheet):')

    # find category header location by searching for first category name
    # We'll iterate categories and months and look for formula cells that match
    # For each category, find a cell in summary with that category text. Then for each month, find the cell in same row offset matching month column.

    # Build a quick map of month header columns by finding the month header cells in the top of the category area.
    # Simpler approach: locate any cell that equals a category name; then for each month look to the right for a formula cell that is numeric or formula.

    category_checks = {}
    for c_name in categories:
        # find category cell in Summary (text match)
        r,c = find_in_sheet(summary, c_name)
        if r is None:
            # maybe categories are in a column as header above; skip
            category_checks[c_name] = {'found':False}
            report_lines.append(f"- Category '{c_name}': NOT FOUND in Summary sheet")
            continue
        category_checks[c_name] = {'found':True, 'row':r, 'col':c, 'months':{}}
        # For each month, find a cell in same row with formula (search right up to 20 cols)
        for m in MONTHS:
            found_formula = None
            cached_value = None
            target_col = None
            for offset in range(1,25):
                cell_formula = summary.cell(row=r, column=c+offset)
                cell_value = summary_values.cell(row=r, column=c+offset)
                if cell_formula.value is None:
                    continue
                # Accept cells that are formulas (start with =) or numeric cached cells
                if isinstance(cell_formula.value, str) and cell_formula.value.strip().startswith('='):
                    # Check if formula mentions the month sheet name or SUMIF
                    if m in cell_formula.value or 'SUMIF' in cell_formula.value.upper() or 'SUM' in cell_formula.value.upper():
                        found_formula = cell_formula.value
                        cached_value = cell_value.value
                        target_col = c+offset
                        break
                # if not formula but cell_value has numeric maybe it's cached already
                if cell_value.value is not None and (isinstance(cell_value.value, (int,float))):
                    # assume this corresponds to some month but we cannot be sure which; we skip
                    pass
            category_checks[c_name]['months'][m] = {
                'formula': found_formula,
                'cached_value': cached_value,
                'col': target_col
            }
            # report
            if found_formula:
                report_lines.append(f"- Category '{c_name}' {m}: formula found at col {target_col}: {found_formula}")
                if cached_value is not None:
                    report_lines.append(f"  Cached numeric value: {cached_value}")
                else:
                    report_lines.append(f"  Cached numeric value: <no cached numeric value>")
                expected = computed[m]['category_totals'].get(c_name, 0)
                report_lines.append(f"  Expected (pandas): {expected}")
                if cached_value is not None:
                    try:
                        match = int(cached_value) == expected
                    except Exception:
                        match = False
                    report_lines.append("  Numeric match: PASS" if match else f"  Numeric match: FAIL (workbook has {cached_value})")
                else:
                    report_lines.append("  Numeric match: N/A (no cached numeric value to compare)")
            else:
                report_lines.append(f"- Category '{c_name}' {m}: NO formula found in nearby cells -> FAIL")

    # Summary conclusion
    report_lines.append('\nOverall conclusions:')
    # For months
    for m in MONTHS:
        info = month_checks.get(m)
        if not info or not info.get('found'):
            report_lines.append(f"- {m}: FAIL - Month row not found in Summary sheet")
            continue
        tf = info.get('total_formula')
        cf = info.get('count_formula')
        total_cached = info.get('total_cached_value')
        count_cached = info.get('count_cached_value')
        expected_total = computed[m]['total_amount']
        expected_count = computed[m]['num_transactions']
        # determine pass/fail
        pf = True
        reasons = []
        if not tf or not (isinstance(tf,str) and tf.strip().startswith('=')):
            pf = False
            reasons.append('Total formula missing')
        if not cf or not (isinstance(cf,str) and cf.strip().startswith('=')):
            pf = False
            reasons.append('Count formula missing')
        # if cached values exist, compare
        if total_cached is not None and int(total_cached) != expected_total:
            pf = False
            reasons.append(f"Total numeric mismatch (workbook {total_cached} != expected {expected_total})")
        if count_cached is not None and int(count_cached) != expected_count:
            pf = False
            reasons.append(f"Count numeric mismatch (workbook {count_cached} != expected {expected_count})")
        if pf:
            report_lines.append(f"- {m}: PASS")
        else:
            report_lines.append(f"- {m}: FAIL - {'; '.join(reasons)}")

    # For categories-months
    any_fail = False
    for c_name in categories:
        rec = category_checks.get(c_name)
        if not rec or not rec.get('found'):
            any_fail = True
            report_lines.append(f"- Category '{c_name}': FAIL - category not found in Summary sheet")
            continue
        for m in MONTHS:
            cellinfo = rec['months'].get(m)
            if not cellinfo or not cellinfo.get('formula'):
                any_fail = True
                report_lines.append(f"- Category '{c_name}' {m}: FAIL - SUMIF formula not found in Summary sheet")
            else:
                # if cached value exists and mismatches
                cv = cellinfo.get('cached_value')
                expected = computed[m]['category_totals'].get(c_name, 0)
                if cv is not None and int(cv) != expected:
                    any_fail = True
                    report_lines.append(f"- Category '{c_name}' {m}: FAIL - numeric mismatch (workbook {cv} != expected {expected})")

    if not any_fail:
        report_lines.append('\nFINAL: All checks passed. Workbook contains formulas and pandas-computed totals match the CSV (where numeric cached values available in workbook they also match).')
    else:
        report_lines.append('\nFINAL: Some checks failed (see details above).')

    REPORT_PATH.write_text('\n'.join(report_lines))
    print(f"Wrote verification report to {REPORT_PATH}")

if __name__ == '__main__':
    main()
