---
name: python-scripting
description: Write and run reliable Python scripts (CLI, file processing, automation) with verification.
version: 1.0.0
requires:
  tools: [write_file, run_shell, read_file]
permissions:
  network: false
  shell: true
---
# Python scripting

1. Check the interpreter: `python --version`. Use only the standard library unless a package is clearly needed;
   if needed, `python -m pip install --quiet <pkg>` and mention it.
2. Structure every script:
   ```python
   """One-line purpose. Usage: python script.py [args]"""
   import argparse
   def main() -> int:
       ...
       return 0
   if __name__ == "__main__":
       raise SystemExit(main())
   ```
3. Use `pathlib`, `argparse`, `csv`/`json`, explicit encodings (`encoding="utf-8"`).
4. Anything destructive gets a `--dry-run` flag (default on for deletes/moves).
5. **Run it** with sample input (create a small sample file if needed) and show the output. Fix errors until it runs.
6. Deliver: script path, usage example, sample output.
