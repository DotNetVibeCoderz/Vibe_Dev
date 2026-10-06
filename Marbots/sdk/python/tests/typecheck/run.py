"""Static typing check: mypy must accept the SDK (--strict) and report exactly the lines marked `# E:` in
check_types.py (each is a deliberate typo). Run from sdk/python: `python tests/typecheck/run.py`."""

import os
import pathlib
import re
import subprocess
import sys

here = pathlib.Path(__file__).resolve().parent
root = here.parents[1]
env = {**os.environ, "MYPYPATH": str(root / "src")}


def mypy(*args: str) -> str:
    r = subprocess.run([sys.executable, "-m", "mypy", "--no-error-summary", "--no-color-output", *args],
                       cwd=root, env=env, capture_output=True, text=True)
    return r.stdout


sdk = mypy("--strict", "src/marbots_sdk")
if sdk.strip():
    sys.exit(f"mypy --strict reported problems in the SDK:\n{sdk}")

source = (here / "check_types.py").read_text(encoding="utf-8").splitlines()
expected = {i + 1 for i, line in enumerate(source) if re.search(r"\s# E:", line)}
reported = {int(m.group(1)) for m in re.finditer(r"check_types\.py:(\d+): error", mypy(str(here / "check_types.py")))}
if reported != expected:
    sys.exit(f"typecheck mismatch: expected errors on lines {sorted(expected)}, mypy reported {sorted(reported)}")
print(f"OK: SDK is mypy --strict clean; {len(expected)} deliberate typos rejected")
