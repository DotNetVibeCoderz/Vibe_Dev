# Subagents and agent teams

> Auto Code — Gravicode Studios, led by Kang Fadhil

## Why subagents exist

Some steps produce far more output than conclusion. Sweeping forty files to find three call sites
fills your main context with thirty-seven files you did not need.

A subagent runs that work in its own context and returns only its report. The main conversation
receives the answer, not the search.

Use one when:

- Answering means reading many files and you only want the conclusion.
- You want an independent opinion — a reviewer that has not seen you write the code.
- The work is self-contained and its intermediate steps do not matter.

Do not use one when the task is small, or when it needs the conversation's context to make sense —
a subagent cannot see it.

## Built-in agents

Available when a workspace defines none of its own:

| Agent | Tools | Purpose |
| --- | --- | --- |
| `explorer` | read-only | Search and read; report findings with file paths and line numbers |
| `reviewer` | read-only + Bash | Review changes for defects with concrete failure scenarios |
| `tester` | read + write + Bash | Write and run tests, matching the existing suite's conventions |

## Defining your own

```
.autocode/agents/migration-auditor.md
```

```markdown
---
name: migration-auditor
description: Audits EF Core migrations for destructive or unreviewed changes
tools: [Read, Grep, Glob, Bash]
max-iterations: 30
model: gpt-4.1-mini
---

You audit Entity Framework migrations before they reach production.

Read every migration under `Migrations/` that is newer than the last release tag. For each one,
determine whether it drops a column, drops a table, changes a column type in a narrowing way, or
adds a non-nullable column without a default — those are the changes that lose data.

Report each finding as: the migration file, the specific operation, and what data would be lost.
If a migration is safe, do not mention it. If there are no findings, say so in one line.
```

| Field | Purpose |
| --- | --- |
| `name` | Dispatch name |
| `description` | **When to dispatch this agent.** The main agent reads this to choose. |
| `tools` | Allow-list. Empty means everything except `Task`. |
| `model` / `provider` | Run on a different, often cheaper, model |
| `max-iterations` | Cap on the subagent's own tool-call rounds |

Loaded from `~/.autocode/agents/`, `<workspace>/.autocode/agents/` and `<workspace>/.claude/agents/`,
in increasing precedence, plus anything plugins contribute.

## Dispatching

The agent does it itself through the `Task` tool when a task matches a description. You can also ask
directly:

```
› use the explorer agent to find every place we construct an HttpClient
```

List what is available:

```
› /agents
```

## Isolation, precisely

A subagent gets:

- Its own system prompt, from the definition.
- Its own conversation. It cannot see the parent's, so its prompt must stand alone.
- Its own file-read tracker — it must read a file itself before it may edit it, regardless of what
  the parent had open.
- A filtered toolset, without `Task`. **Nesting is disabled**: a subagent that can spawn subagents
  turns a bounded run into an unbounded one, at a depth nobody approved.

It shares: the workspace, the terminal, and — importantly — **the permission engine**. Dispatching a
subagent is not a way around the approval prompt.

Subagents run on Microsoft Agent Framework (`ChatClientAgent`), with tool calls routed through the
same gating pipeline the main loop uses.

## Agent teams

A team puts several subagents on one brief.

```jsonc
{
  "teams": {
    "review": {
      "description": "Full review pass over the current change",
      "mode": "Parallel",
      "members": ["reviewer", "tester", "migration-auditor"],
      "synthesizer": "reviewer"
    },
    "feature": {
      "description": "Research, then implement, then test",
      "mode": "Sequential",
      "members": ["explorer", "implementer", "tester"]
    }
  }
}
```

**Parallel** sends the same brief to every member at once and merges the reports. Name a
`synthesizer` and it reconciles them — keeping agreements, surfacing conflicts, dropping unsupported
claims. Use this when independent perspectives are the point.

**Sequential** pipes each member's output into the next, along with the original brief. Use this when
the work has stages.

```
› /teams review the changes in src/Api/Orders since the last commit
```

Members run concurrently in parallel mode, so a three-member team costs roughly one member's
wall-clock time — and three members' tokens.

## Cost

Every subagent is a separate conversation with its own system prompt. A parallel team of three
triples the token cost of that step. That is usually worth it for review, where independence is the
value; it is rarely worth it for a lookup a single `Grep` would have answered.

Set `model` on definitions that do not need your best model — an explorer running on a small model
returns the same file paths for a fraction of the cost.
