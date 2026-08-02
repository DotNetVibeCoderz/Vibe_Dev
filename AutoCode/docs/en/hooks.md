# Hooks

> Auto Code — Gravicode Studios, led by Kang Fadhil

A hook is a shell command bound to a lifecycle event. Hooks let you enforce policy and automate
around the agent deterministically — the harness runs them, so they happen whether or not the model
cooperates.

## Events

| Event | Fires | Can block |
| --- | --- | --- |
| `SessionStart` | Session begins | no |
| `UserPromptSubmit` | Before a prompt reaches the model | yes |
| `PreToolUse` | Before a tool runs, before the permission prompt | **yes** |
| `PostToolUse` | After a tool completes | no |
| `PreCompact` | Before context compaction | no |
| `SubagentStop` | A subagent finished | no |
| `Stop` | A turn ended | no |

## Configuration

```jsonc
{
  "hooks": {
    "PostToolUse": [
      {
        "matcher": "Edit|Write|MultiEdit",
        "command": "dotnet format --include $(git diff --name-only --diff-filter=ACM)",
        "timeoutMs": 60000,
        "blocking": false
      }
    ],
    "PreToolUse": [
      {
        "matcher": "Bash",
        "command": "pwsh -File .autocode/guard-commands.ps1",
        "blocking": true
      }
    ]
  }
}
```

| Field | Meaning |
| --- | --- |
| `matcher` | Glob against the tool name. Omit to match everything. |
| `command` | Shell command. Receives the event payload as JSON on stdin. |
| `timeoutMs` | Default 30 000. On timeout the hook is ignored, never fatal. |
| `blocking` | For `Pre*` events: a non-zero exit refuses the action. Default `true`. |

## The contract

**Input** — a JSON object on stdin:

```json
{
  "hook_event_name": "PreToolUse",
  "session_id": "a1b2c3d4",
  "tool_name": "Bash",
  "tool_input": { "command": "git push --force origin main" }
}
```

Also available as environment variables: `AUTOCODE_HOOK_EVENT`, `AUTOCODE_WORKSPACE`.

**Output**

- **Exit code 0** — allow. Anything on stdout is passed to the model as extra context.
- **Non-zero** on a blocking `Pre*` event — refuse. stderr (or stdout) becomes the reason the model
  is told, so it can adapt rather than retry blindly.

That stdout-as-context behaviour is what makes a hook able to explain itself:

```bash
#!/usr/bin/env bash
# PostToolUse on Edit — tell the agent when it has broken the build
if ! dotnet build --nologo -v q > /tmp/build.log 2>&1; then
  echo "The build is now failing:"
  tail -20 /tmp/build.log
fi
exit 0
```

## Useful hooks

**Format after every edit**

```jsonc
{ "hooks": { "PostToolUse": [
  { "matcher": "Edit|Write|MultiEdit", "command": "dotnet format --no-restore", "blocking": false }
]}}
```

**Block force pushes regardless of permission mode**

```powershell
# .autocode/guard-commands.ps1
$payload = [Console]::In.ReadToEnd() | ConvertFrom-Json
$command = $payload.tool_input.command

if ($command -match 'push\s+.*--force' -and -not ($command -match '--force-with-lease')) {
    Write-Error "Force pushes are not allowed here. Use --force-with-lease."
    exit 1
}
exit 0
```

**Protect a release branch**

```bash
#!/usr/bin/env bash
branch=$(git rev-parse --abbrev-ref HEAD)
if [[ "$branch" == release/* ]]; then
  echo "Direct edits to $branch are not allowed. Work on a feature branch." >&2
  exit 1
fi
exit 0
```

**Notify when a long run finishes**

```jsonc
{ "hooks": { "Stop": [
  { "command": "notify-send 'Auto Code' 'Turn finished'", "blocking": false }
]}}
```

## Hooks versus permission rules

Both refuse actions. They answer different questions.

- A **permission rule** is a static fact about the arguments: *never read `.env`*. Cheap, declarative,
  no process spawned.
- A **hook** is a decision that depends on state the arguments do not carry: the current branch, a
  clean working tree, the time of day, an external policy service.

Reach for a rule first. Use a hook when the rule cannot express the condition.

## Notes

- Hooks run in the workspace root.
- A hook that fails to start is reported and ignored — a broken hook must not cost you the session.
- A hook that times out is ignored, never treated as a block.
- `PreToolUse` runs **before** the permission prompt, so a hook veto means the user is never asked.
- Plugins can contribute hooks; they are merged with yours rather than replacing them.
