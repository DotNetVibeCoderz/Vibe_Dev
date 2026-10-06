---
name: product-requirements
description: Write PRDs and user stories with acceptance criteria, scope and prioritization.
version: 1.0.0
requires:
  tools: [write_file]
permissions:
  network: false
  shell: false
---
# Product requirements (PRD)

```
# PRD: <feature>
Status · Owner · Date
## Problem
## Goals & non-goals
## Users & use cases
## Success metrics (with targets)
## Requirements
| ID | Requirement | Priority (MoSCoW) | Notes |
## User stories
- As a <user>, I want <capability>, so that <benefit>.
  - Acceptance: Given … When … Then …
## UX notes / flows
## Risks, assumptions, dependencies
## Release plan / milestones
## Open questions
```
Rules: every requirement testable; prioritize ruthlessly; separate what from how.
