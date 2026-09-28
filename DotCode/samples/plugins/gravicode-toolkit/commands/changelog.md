---
description: Draft a CHANGELOG entry from recent git history
argument-hint: [since-ref]
allowed-tools: Bash(git log:*), Bash(git diff:*)
---
Recent commits:
!`git log --oneline -n 20`

Draft a CHANGELOG.md entry (Keep a Changelog format: Added / Changed / Fixed) for the changes since $ARGUMENTS (or the last 20 commits if empty).
Group related commits, write user-facing descriptions, and prepend the entry to CHANGELOG.md (create it if missing).
