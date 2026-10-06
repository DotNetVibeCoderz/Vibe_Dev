---
name: meeting-notes
description: Turn meetings or rough notes into structured minutes with decisions, action items and owners.
version: 1.0.0
requires:
  tools: [write_file]
permissions:
  network: false
  shell: false
---
# Meeting notes

```
# <Meeting> — <date>
Attendees · Facilitator · Note taker
## Summary (3 bullets)
## Decisions
- D1: … (owner, rationale)
## Action items
| # | Action | Owner | Due | Status |
## Discussion notes (by agenda item)
## Parking lot / open questions
## Next meeting
```
Rules: actions start with a verb and have one owner; decisions are explicit; keep neutral tone.
