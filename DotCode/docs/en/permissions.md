# Permissions and safety

> 🇮🇩 [Bahasa Indonesia](../id/izin.md)

DotCode asks before it changes anything. Read-only work inside your working directories runs freely; edits, shell commands, web access and MCP tools need approval unless a mode or rule allows them.

## Permission modes

| Mode | Behavior | How to enable |
|---|---|---|
| `default` | Ask for edits, non-read-only shell commands, web, MCP | default |
| `acceptEdits` | File edits inside working dirs (and `mkdir`/`touch`) are auto-approved | `Shift+Tab`, `--permission-mode acceptEdits` |
| `plan` | Research only — mutations are blocked until you approve a plan | `Shift+Tab` ×2, `--permission-mode plan` |
| `bypassPermissions` | Everything runs without asking (deny rules still apply) | `--dangerously-skip-permissions` |

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

## Checkpoints

Before the first change to a file in each turn, DotCode snapshots it. `Esc Esc` or `/rewind` restores code and/or conversation to any earlier prompt.

## Other safeguards

- Tool results are treated as data; the system prompt tells the model not to follow instructions found in files or web pages.
- WebFetch blocks private, loopback, link-local and cloud-metadata addresses (override with `DOTCODE_ALLOW_PRIVATE_FETCH=1`) and reports cross-host redirects instead of following them.
- Edits require a prior Read and fail if the file changed on disk since.
- SDK sessions are **deny-by-default** unless the host registers a permission handler.
- The WebSocket server binds to `127.0.0.1` and supports a bearer token.
