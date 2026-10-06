# Schedules

[English](../en/scheduling.md) · [Bahasa Indonesia](../id/scheduling.md)

The scheduler sends a prompt to a bot on a schedule. Each run becomes a normal task in the schedule's own thread
(`⏰ <name>`), so results, files and approvals work exactly like chat.

![Schedules](../images/schedules.png)

## Creating a schedule

- **Ask Boss Man**: *"Every Monday at 08:00 WIB, ask Atlas for a short AI news brief."* Boss Man calls `schedule_task`.
- **Schedules page**: name, bot, cron expression *or* a one-off date/time, time zone, prompt.
- **API**: `POST /api/v1/schedules`.

## Cron syntax

Five fields: `minute hour day-of-month month day-of-week`.

| Expression | Meaning |
|---|---|
| `0 8 * * 1` | Mondays at 08:00 |
| `*/30 9-17 * * 1-5` | Every 30 minutes, 09:00–17:59, Monday–Friday |
| `0 7 1 * *` | 07:00 on the 1st of each month |
| `15 18 * * 0,6` | 18:15 on weekends |

Supported: `*`, lists (`1,15`), ranges (`1-5`), steps (`*/10`, `0-30/5`); day-of-week `0` or `7` is Sunday. When both
day-of-month and day-of-week are restricted, either one matching is enough (classic cron behaviour). The page shows
the next run as you type.

Time zones accept Windows ids (`SE Asia Standard Time`) and IANA ids (`Asia/Jakarta`) where the OS supports them.

## Behaviour

- The scheduler checks every 15 seconds. A job that was due while Marbots was stopped runs once at the next check.
- **Run now** fires a job immediately. Pausing a job keeps its definition.
- One-off jobs disable themselves after running.

---
*Marbots — Created by Gravicode Studios, led by Kang Fadhil.*
