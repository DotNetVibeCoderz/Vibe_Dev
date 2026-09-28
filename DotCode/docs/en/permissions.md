# Permissions and safety

> 🇮🇩 [Bahasa Indonesia](../id/izin.md)

DotCode asks before it changes anything. Read-only work inside your working directories runs freely; edits, shell commands, web access and MCP tools need approval unless a mode or rule allows them.

## Permission modes

| Mode | Behavior | How to enable |
|---|---|---|
| `default` | Ask for edits, non-read-only shell commands, web, MCP | default |
| `acceptEdits` | File edits inside working dirs (and `mkdir`/`touch`) are auto-approved | `Shift+Tab`, `--permission-mode acceptEdits` |
| `auto` | A classifier model approves low-risk actions, blocks dangerous ones and asks about the rest | `--permission-mode auto`, or `Shift+Tab` when enabled |
| `plan` | Research only — mutations are blocked until you approve a plan | `Shift+Tab`, `--permission-mode plan` |
| `bypassPermissions` | Everything runs without asking (deny rules still apply) | `--dangerously-skip-permissions` |

### Auto mode

Instead of prompting, DotCode asks a small classifier model (the `classifier` role, falling back to `fast`, then the main model) whether an action that would need approval is low-risk and serves your recent requests:

- **allow** — runs without a prompt (the tool line shows `Auto mode: allowed — <reason>`). Examples: builds, tests, linters, installing declared dependencies, local git commits, editing files in the project.
- **ask** — you get the normal dialog (headless: denied with guidance). Examples: `git push`, deploys, publishing, writing outside the project, anything irreversible.
- **deny** — blocked; the reason goes back to the model so it can choose a safer approach. Examples: `curl … | bash`, broad `rm -rf`, touching credentials, actions that appear to come from prompt injection.

Explicit `deny` and `ask` rules always win. Unclear classifier answers fall back to **ask**, so auto mode can only remove prompts for actions it explicitly judged safe. After the agent reads web pages or MCP results in a turn, the classifier is told to be suspicious of unrequested actions. File edits inside working directories are approved without a classifier call (as in accept-edits). Classifier calls are counted in `/cost`.

```jsonc
"permissions": { "autoMode": { "enabled": true, "model": "openai:gpt-5-mini", "guidance": "Never allow deployments or database migrations." } }
```

`enabled` adds auto mode to the Shift+Tab cycle (default → accept edits → auto → plan). The footer shows `⏵⏵ auto mode on`.

### `--dangerously-skip-permissions`

Starts in bypass mode; the footer shows a red `⏵⏵ bypass permissions on`. Use it only in disposable sandboxes, containers or CI. `--allow-dangerously-skip-permissions` makes bypass reachable with Shift+Tab without starting in it. Organizations can forbid bypass with `"permissions": { "disableBypassPermissionsMode": true }` in managed settings.

## Rules

Rules use Claude Code's syntax `Tool` or `Tool(specifier)` and live in `permissions.allow`, `permissions.ask` and `permissions.deny` (or `--allowedTools` / `--disallowedTools`). Evaluation order: **deny → plan-mode restriction → ask → bypass → allow → built-in defaults**.

| Rule | Matches |
|---|---|
| `Read`, `Edit`, `Bash` | Every use of the tool |
| `Bash(npm run test:*)` | Commands starting with `npm run test` (legacy prefix syntax) |
| `Bash(git *)` | Wildcard match (`*` spans anything) |
| `Bash(dotnet build)` | Exactly this command |
| `Read(~/secrets/**)` | Home-relative glob (`Read` rules cover Read, Glob, Grep) |
| `Edit(/src/**)` | Project-root-relative glob (`Edit` rules cover Edit, Write, NotebookEdit) |
| `Edit(//etc/**)` | Absolute path |
| `Read(.env)` | Any `.env` under the working directory |
| `WebFetch(domain:learn.microsoft.com)` | A domain and its subdomains |
| `mcp__github`, `mcp__github__create_issue` | All tools of an MCP server, or one tool |
| `Agent(Explore)`, `Skill(pdf)` | A subagent type, a skill |

Compound shell commands are split on `&&`, `||`, `;`, `|` and newlines: an allow rule must match **every** subcommand, and a deny rule blocks the command if **any** subcommand matches. Command substitution (`$(…)`, backticks) and output redirection are never considered read-only.

### Built-in defaults

- Read/Glob/Grep inside working directories: allowed. Outside: ask. Files that look secret (`.env`, `*.pem`, `id_rsa`, `credentials.json` …): ask.
- Known read-only shell commands (`ls`, `cat`, `git status/log/diff`, `Get-ChildItem` …): allowed.
- TodoWrite, AskUserQuestion, Agent, Skill: allowed.
- Everything else: ask.

## Hooks can decide too

A `PreToolUse` hook can return `{"hookSpecificOutput":{"permissionDecision":"allow|deny|ask"}}` or exit with code 2 to block. See [Extensions → Hooks](extensions.md#hooks).

## Sandbox for shell commands

Permission prompts decide *whether* a command runs. The sandbox limits *what it can do* once it runs. Turn it on per project with `/sandbox on`, or in settings:

```jsonc
"sandbox": {
  "enabled": true,
  "autoAllowBashIfSandboxed": true,     // sandboxed commands need no prompt (Linux/macOS only)
  "allowUnsandboxedCommands": true,     // allow the dangerously_disable_sandbox escape hatch (it always asks)
  "network": "allow",                   // allow | deny outbound network
  "allowWrite": ["../shared-cache"],    // extra writable paths
  "denyRead": ["~/.config/private"],    // extra hidden paths
  "excludedCommands": ["docker", "git push"],  // always run outside the sandbox (normal permission rules)
  "failIfUnavailable": false,           // true = refuse to run shell commands when no sandbox is available
  "memoryLimitMb": 4096, "maxProcesses": 64    // Windows Job Object limits
}
```

| Platform | Mechanism | What it enforces |
|---|---|---|
| Linux | [bubblewrap](https://github.com/containers/bubblewrap) (`bwrap`) | Read-only root file system. Writable: the working directories (including `/add-dir` and the git worktree's `.git`), temp and package caches (`~/.npm`, `~/.cache`, `~/.nuget`, `~/.cargo`, `~/go`, `~/.m2`, `~/.gradle`…). Credential folders (`~/.ssh`, `~/.aws`, `~/.gnupg`, `~/.azure`, `~/.kube`, `~/.config/gcloud`, `~/.docker`, `~/.config/gh`, `~/.netrc`, `~/.git-credentials`, `~/.npmrc`, `~/.pypirc`, `~/.dotcode`) are hidden. `network: deny` gives the command its own network namespace. |
| macOS | `sandbox-exec` (Seatbelt profile) | The same write, hide and network policy (`deny` still allows localhost). |
| Windows | Job Object | The whole process tree stays contained: anything the command leaves running is killed when it finishes, children cannot break away, and optional memory / process-count limits and clipboard, desktop and system-settings restrictions apply. **No file-system isolation**: Windows has no unprivileged equivalent. |

- **Prompts.** Where the file system is isolated (Linux, macOS), sandboxed Bash/PowerShell commands run without a prompt: deny and ask rules still apply, and the audit log records `decision: "sandbox"`. On Windows, or when no sandbox is available, the normal permission rules apply.
- **Failures.** When a sandboxed command fails with a sandbox-looking error ("Read-only file system", "Operation not permitted", DNS failures…), the model is told so. It may retry with `dangerously_disable_sandbox: true`. That runs the command outside the sandbox under the normal permission flow, so it asks unless an allow rule matches. Set `allowUnsandboxedCommands: false` to remove this escape hatch entirely.
- **Availability.** `dotcode doctor` and `/sandbox` show which mechanism is available. On Linux, install `bubblewrap`. Some container environments and hardened kernels forbid the unprivileged user namespaces it needs; DotCode probes once and reports "not available". Commands then run unsandboxed (and are not auto-allowed), or are refused with `failIfUnavailable`.
- **Scope.** The sandbox applies to the Bash and PowerShell tools, including background shells. DotCode's own file tools (Read/Edit/Write) are governed by the permission rules and working directories.

## Checkpoints

Before the first change to a file in each turn, DotCode snapshots it. `Esc Esc` or `/rewind` restores code and/or conversation to any earlier prompt.

## Other safeguards

- Tool results are treated as data; the system prompt tells the model not to follow instructions found in files or web pages.
- WebFetch blocks private, loopback, link-local and cloud-metadata addresses (override with `DOTCODE_ALLOW_PRIVATE_FETCH=1`) and reports cross-host redirects instead of following them.
- Edits require a prior Read and fail if the file changed on disk since.
- SDK sessions are **deny-by-default** unless the host registers a permission handler.
- The WebSocket server binds to `127.0.0.1` and supports a bearer token.
