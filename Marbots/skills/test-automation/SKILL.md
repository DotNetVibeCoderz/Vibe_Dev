---
name: test-automation
description: Plan and automate tests (unit, API, UI with Playwright), run them and report results.
version: 1.0.0
requires:
  tools: [read_file, write_file, run_shell]
permissions:
  network: false
  shell: true
---
# Test automation

1. Read the code/spec; list behaviours and risks. Write `tests/TEST-PLAN.md` with cases: id, scenario, steps, expected.
2. Pick the framework that matches the stack:
   - Python: `pytest` (`python -m pytest -q`).
   - .NET: xUnit (`dotnet test`).
   - JS/web UI: Playwright (`npx playwright test`) or a Python Playwright script.
   - Static HTML apps: a Python script that parses the HTML and checks required elements/ids, or Playwright if available.
3. Cover: happy path, boundaries, invalid input, error handling, regression for known bugs.
4. Run the tests. Never report "passing" without running them. Capture the summary line.
5. Report:
```
## Test report
- Total: N, Passed: P, Failed: F
- Failures: test → reason → suspected cause
- Bugs found: severity, steps, expected vs actual
```
