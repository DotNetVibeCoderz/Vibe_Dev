"""Profile a CSV file and print JSON statistics (used by the csv-insights skill)."""
import csv
import json
import sys
from collections import Counter


def to_number(value: str):
    try:
        return float(value.replace(",", ""))
    except ValueError:
        return None


def main(path: str) -> None:
    with open(path, newline="", encoding="utf-8-sig") as f:
        rows = list(csv.DictReader(f))
    columns = rows[0].keys() if rows else []
    report = {"file": path, "rows": len(rows), "columns": {}}
    for col in columns:
        values = [r[col] for r in rows if r.get(col) not in (None, "")]
        numbers = [n for n in (to_number(v) for v in values) if n is not None]
        if values and len(numbers) == len(values):
            report["columns"][col] = {
                "type": "number",
                "min": min(numbers),
                "max": max(numbers),
                "mean": round(sum(numbers) / len(numbers), 2),
                "sum": round(sum(numbers), 2),
            }
        else:
            counts = Counter(values)
            report["columns"][col] = {
                "type": "text",
                "distinct": len(counts),
                "top": counts.most_common(5),
                "first": values[0] if values else None,
                "last": values[-1] if values else None,
            }
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    if len(sys.argv) != 2:
        sys.exit("usage: analyze.py <file.csv>")
    main(sys.argv[1])
