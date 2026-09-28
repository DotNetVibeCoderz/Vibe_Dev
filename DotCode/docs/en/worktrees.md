# Git worktrees

> 🇮🇩 [Bahasa Indonesia](../id/worktree.md)

A [git worktree](https://git-scm.com/docs/git-worktree) is a second checkout of the same repository on its own branch. DotCode can run a whole session, or a single subagent, inside one. The agent can then edit, build and commit freely without touching your main working tree, and several sessions can work on the same repository in parallel.

DotCode keeps its worktrees in `<repo>/.dotcode/worktrees/<name>` on the branch `dotcode/<name>`. It writes a `.gitignore` there, so they never show up in your main checkout's `git status`.

## A session in a worktree

```bash
dotcode --worktree                  # generated name, e.g. wt-0928-181243-71fd
dotcode --worktree auth-refactor    # named (reused if it already exists)
dotcode -w auth-refactor -c         # continue the last session in that worktree
dotcode -w -p "fix the flaky test"  # works in headless mode too
```

- DotCode creates the worktree from the current `HEAD`, prints its path and branch, and starts the session there. If you start in a subdirectory (`repo/src`), you land in the same subdirectory of the worktree. The welcome panel shows `worktree <name> · dotcode/<name>`.
- **On exit**, a worktree without changes (no uncommitted files and no new commits) is removed together with its branch. If there are changes, it is kept and DotCode prints how to continue, merge or remove it:

```text
Worktree kept: …/.dotcode/worktrees/auth-refactor (branch dotcode/auth-refactor: 2 commit(s)).
  Continue:  dotcode --worktree auth-refactor -c
  Merge:     git merge dotcode/auth-refactor   ·   Remove: dotcode worktree remove auth-refactor
```

A name may contain letters, digits, `.`, `_` and `-`. A quoted prompt such as `dotcode -w "fix the bug"` is never taken as a name. Use `--worktree=name` when you want to be explicit.

## Managing worktrees

```bash
dotcode worktree list                         # name, branch, changed files, commits ahead of HEAD
dotcode worktree remove <name> [--keep-branch]
dotcode worktree prune                        # remove clean worktrees whose branch is already merged
```

## Isolated subagents

Add `isolation: worktree` to an agent definition, or have the model pass `"isolation": "worktree"` to the `Agent` tool:

```markdown
---
name: experimenter
description: Tries a risky refactor on a separate branch.
isolation: worktree
---
Make the change, run the tests, commit on your branch and report the result.
```

The subagent works in its own worktree: file paths, `Bash`/`PowerShell` commands and the prompt's working directory all point there. Its system prompt tells it to commit its work to its branch. Parallel isolated subagents never step on each other. When it finishes:

- **no changes** → the worktree and branch are removed;
- **changes** → the worktree is kept, and the tool result tells the main agent the path, branch, commits and how to review (`git diff base...branch`) and merge it.

## SDKs

All SDKs accept a `worktree` session option: `true` for a generated name, or a string name.

```ts
const session = await client.createSession({ worktree: "sdk-task", permissionMode: "acceptEdits" });
```

`session.create` returns `worktree: { name, path, branch }`. `session.close` removes the worktree when nothing changed. In .NET use `Worktree = true` / `WorktreeName = "…"`, in Python `worktree=True`/`"name"`, in Go `Worktree`/`WorktreeName`, in Java `.worktree(true)`/`.worktree("name")`.

## Requirements and notes

- `git` must be on `PATH`, and the repository needs at least one commit.
- The worktree shares the repository's objects and branches. Committing there creates commits on `dotcode/<name>`, which you can merge, rebase or push like any branch.
- Untracked files that the build needs (for example `.env`, local config) are not copied into the worktree.
- Sessions and checkpoints are stored per worktree path, so `-c`/`--resume` inside a worktree continue that worktree's conversations.
