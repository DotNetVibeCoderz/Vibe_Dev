---
name: code-review
description: Review source code for bugs, security issues, readability and performance with actionable, prioritized feedback.
version: 1.0.0
requires:
  tools: [read_file, grep, list_files]
permissions:
  network: false
  shell: false
---
# Code review

1. Understand intent: read the README/spec or the request; list the files in scope with `list_files`.
2. Read every changed/target file fully with `read_file`. Use `grep` to find callers of changed functions.
3. Check, in this order:
   - **Correctness** – off-by-one, null/None handling, error paths, concurrency, resource disposal, wrong API use.
   - **Security** – injection (SQL/shell/HTML), path traversal, secrets in code, missing authz, unsafe deserialization.
   - **Data & performance** – N+1 queries, unbounded loops/collections, needless allocations, blocking I/O in async code.
   - **Design** – naming, cohesion, duplication, testability.
   - **Tests** – are the important behaviours covered?
4. Only report issues you can point to (file:line). Quote the code and propose the fix.

## Output format
```
## Summary
<2-3 sentences: overall quality and the most important risk>

## Must fix
- `path:line` – problem → fix

## Should fix
- ...

## Nice to have
- ...
```
Write the review to `reviews/<topic>-review.md` when working in a shared workspace.
