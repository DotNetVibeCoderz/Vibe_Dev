# Permissions

> Auto Code — Gravicode Studios, led by Kang Fadhil

## The principle

An agent that can edit files and run commands needs a boundary. Auto Code's is: **read freely, ask
before changing anything**.

Read-only tools — `Read`, `Grep`, `Glob`, `List` — never prompt, in any mode. Everything that
writes, executes or reaches the network goes through the permission engine.

## Modes

| Mode | Writes | Commands | Network |
| --- | --- | --- | --- |
| `ask` *(default)* | prompt | prompt | prompt |
| `acceptEdits` | allowed | prompt | prompt |
| `plan` | **refused** | **refused** | prompt |
| `bypassPermissions` | allowed | allowed | allowed |

```bash
autocode --permission-mode plan "how would you add rate limiting?"
```

```
› /permissions acceptEdits
```

`plan` is worth knowing well. It is not "ask more" — mutations are hard-refused, so the agent cannot
try one to see whether it is allowed. You get research and a plan, and nothing has changed.

`bypassPermissions` is for sandboxes and CI. Deny rules still apply to it; nothing else does.

## Rules

A rule is `Tool` or `Tool(pattern)`:

```jsonc
{
  "permissions": {
    "allow": [
      "Read",                      // every call to Read
      "Bash(git status)",          // exactly this command
      "Bash(npm run:*)",           // this prefix and anything after it
      "Bash(dotnet *)",            // glob
      "Write(src/**)",             // path glob
      "mcp__github__*"             // every tool from one MCP server
    ],
    "ask":  ["Bash(git push:*)"],  // force a prompt even in acceptEdits
    "deny": ["Read(**/.env)", "Bash(rm -rf:*)", "Write(**/*.key)"]
  }
}
```

Pattern syntax:

| Form | Meaning |
| --- | --- |
| `Tool` | Any call to that tool |
| `Tool(exact)` | Exact match on the subject |
| `Tool(prefix:*)` | The subject starts with `prefix` |
| `Tool(glob)` | Glob match, with `*`, `**`, `?`, `{a,b}` |

The "subject" is the command for shell tools, the path for file tools, and the URL for network
tools.

## Evaluation order

```
deny  →  mode  →  allow  →  ask
```

**Deny always wins.** A deny rule cannot be undone by a broader allow rule, and not by
`bypassPermissions` either. This is what makes a deny list worth writing.

## Answering a prompt

```
╭─ Run command ─────────────────────────────────╮
│ Bash(dotnet test --filter OrderFlow)          │
╰───────────────────────────────────────────────╯
Allow this?
❯ Yes
  Yes, and don't ask again for Bash(dotnet test:*)
  No, tell Auto Code what to do differently
  No, and stop this turn
```

- **Yes** — this call only.
- **Yes, and don't ask again** — adds an allow rule for the rest of the session. Commands generalise
  to a prefix; paths stay exact.
- **No, tell it what to do differently** — you type a reason, the agent receives it as the tool
  result, and adapts. This is usually more useful than a bare refusal.
- **No, and stop** — ends the turn so you can redirect.

Session rules are not written to disk. Move the ones you want to keep into `settings.json` yourself
— an agent silently accumulating permissions across sessions is not a property you want.

## A starting deny list

```jsonc
{
  "permissions": {
    "deny": [
      "Read(**/.env)", "Read(**/.env.*)", "Read(**/*.pem)", "Read(**/id_rsa*)",
      "Write(**/.env)", "Write(**/*.key)",
      "Bash(rm -rf:*)",
      "Bash(git push --force:*)",
      "Bash(curl:* | sh)",
      "Bash(sudo:*)"
    ]
  }
}
```

## Non-interactive runs

In `--print` mode nothing can prompt, so anything that would ask is refused with an explanation the
model can act on. To let a headless run make changes:

```bash
autocode -p "fix the failing test" --permission-mode acceptEdits
```

Or pre-authorise exactly what it needs:

```bash
autocode -p "run the tests" --allowed-tools "Bash(dotnet test:*)"
```

## Hooks as policy

A `PreToolUse` hook can veto a call before the permission engine ever sees it — useful for rules that
depend on state rather than on the arguments alone, such as refusing writes while a release branch is
checked out. See [hooks](hooks.md).

## Subagents

Subagents route every tool call through the same engine as the main loop. Dispatching a subagent is
not a way around the prompt — you will still be asked. They also start with an empty file-read
tracker, so a subagent must read a file itself before it may edit it.
