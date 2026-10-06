# Core concepts

[English](../en/concepts.md) · [Bahasa Indonesia](../id/concepts.md)

## Bots

A **bot** is a durable AI teammate. It has an identity (name, marble colour, role), a persona (instructions), a model
profile, memory settings, skills, MCP servers, kernel function packs and a permission profile. A bot's identity is
separate from any run: the same bot can work on many tasks, in many threads, at the same time.

## Boss Man

**Boss Man** is the protected manager bot. It always exists, cannot be deleted, and is the default chat entry point.
For small requests it answers directly. For bigger goals it:

1. looks at the team with `list_bots`,
2. splits work into self-contained sub-tasks,
3. hands them out with `delegate_tasks` (independent tasks run in parallel; `depends_on` orders them),
4. reviews the results and replies with one synthesis.

It can also hire new teammates (`create_bot`, optionally from a template) and schedule recurring work (`schedule_task`).
You can always skip Boss Man and chat with any bot directly.

## Threads, tasks and runs

| Term | Meaning |
|---|---|
| **Thread** | A human-visible conversation with one bot. Can be pinned, archived, forked, reset or exported. |
| **Task** | A durable unit of work. Each message you send creates a root task; delegation creates child tasks. |
| **Transcript** | The messages and tool calls of a task. Root tasks write into the thread; child tasks keep their own transcript (see Tasks → task). |
| **Workspace** | A project folder per thread (`data/workspaces/<thread>`). Every bot working on that thread shares it, so the developer's files are visible to QA. |

Task states: `Queued → Preparing → Running ⇄ WaitingForTool / WaitingForAgent / WaitingForHuman → Completed | Failed | Cancelled | TimedOut`.

## The agent loop

```
build context → call model → policy-checked tool calls → observe → repeat → final answer
```

Each step emits events (`AgentThinkingStarted`, `ToolCallStarted`, `ToolCallCompleted`, …). A bot stops when the model
answers without calling tools, or when it hits its **max steps** limit, in which case it is asked to summarise what is done and what remains.

## Memory

- **Short-term memory** — the thread history the model sees. Turn it off for stateless bots.
- **Long-term memory** — facts, preferences and procedures saved with `remember` (or by Auto-Learn) and recalled
  automatically. Search uses SQLite FTS5 (BM25) re-ranked by confidence and recency, filtered by owner. Each memory
  keeps its provenance (source, date, confidence).
- Memory is **private per bot**. The `shared` space is readable by every bot.

## Context compaction

When a thread grows past the bot's threshold (default 24,000 tokens), older turns are summarised into a rolling summary
while the last two user turns stay verbatim. Messages are never deleted, so the full transcript remains for audit.
Type `/compact` in a chat to compact on demand. **Reset context** starts the model fresh without deleting history.

## Auto-Learn

Optional per bot:

| Mode | What happens after a task |
|---|---|
| `Off` | Nothing |
| `MemoryOnly` | Up to three durable facts are extracted into long-term memory (secrets are filtered) |
| `SuggestSkills` | Also drafts a reusable `SKILL.md` into a review queue (Skills page). It is never published automatically and never grants new permissions. |

## Kernel functions

Built-in tools grouped in packs that you enable per bot:

| Pack | Tools |
|---|---|
| `files` | `read_file`, `write_file`, `edit_file`, `list_files`, `delete_file` |
| `search` | `grep` |
| `shell` | `run_shell` (PowerShell on Windows, bash elsewhere) |
| `web` | `web_search`, `web_fetch` |
| `memory` | `remember`, `recall` |
| `todo` | `todo_write` (visible live in the chat) |
| `agents` | `list_bots`, `delegate_tasks` |
| `management` | Boss Man only: `create_bot`, `list_templates`, `schedule_task`, `get_task` |
| `skills` | Added automatically when a bot has skills: `load_skill`, `read_skill_file` |

## Events

Everything observable is an event, stored in SQLite and fanned out live: chat UI, Office view, CLI `logs`, and the SSE
endpoints all read the same stream. See [API and SDKs](api-and-sdks.md).

---
*Marbots — Created by Gravicode Studios, led by Kang Fadhil.*
