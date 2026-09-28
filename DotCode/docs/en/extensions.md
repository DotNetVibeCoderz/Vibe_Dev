# Extensions: skills, commands, subagents, hooks, plugins, MCP

> 🇮🇩 [Bahasa Indonesia](../id/ekstensi.md)

DotCode reads both its own layout (`.dotcode/`, `~/.dotcode/`) and Claude Code's (`.claude/`, `~/.claude/`), so existing skills, commands and agents keep working. Project definitions override user ones.

## Skills

A skill is a folder with a `SKILL.md`. The model sees each skill's name and description in the `Skill` tool and loads the full instructions only when a task matches. Users can invoke a skill directly with `/skill-name [args]`.

```
.dotcode/skills/csv-insights/
├── SKILL.md
└── analyze.py
```

```markdown
---
name: csv-insights
description: Analyze a CSV file and produce a Markdown insights report. Use whenever the user asks to analyze CSV data.
---
1. Run `python "{baseDir}/analyze.py" <file.csv>` …
2. Write `<name>-report.md` with Overview, Key numbers, Insights, Recommendations.
```

`{baseDir}` (or `${DOTCODE_PLUGIN_ROOT}` / `${CLAUDE_PLUGIN_ROOT}`) expands to the skill folder. Optional frontmatter: `allowed-tools`, `model`.

![Skill in action](../images/skill-csv-report.png)

## Custom slash commands

Markdown files in `.dotcode/commands/` (project) or `~/.dotcode/commands/` (user). Subfolders namespace them (`commands/frontend/lint.md` → `/frontend:lint`).

```markdown
---
description: Draft a CHANGELOG entry
argument-hint: [since-ref]
allowed-tools: Bash(git log:*)
model: fast
---
Recent commits:
!`git log --oneline -n 20`

Draft a CHANGELOG entry for changes since $ARGUMENTS.
```

`$ARGUMENTS` and `$1…$9` are substituted. `` !`cmd` `` runs a shell command (only when allowed by `allowed-tools`) and inlines its output. `allowed-tools` rules are granted for the session when the command runs.

## Subagents

`.dotcode/agents/<name>.md`:

```markdown
---
name: code-reviewer
description: Expert code reviewer. Use after writing or modifying code.
tools: Read, Grep, Glob, Bash
model: inherit          # inherit | fast | planner | subagent | provider:model
isolation: worktree     # optional: work in its own git worktree
---
You are a senior code reviewer. …
```

The model delegates through the `Agent` tool (`subagent_type`). Subagents have their own context window; only their final report returns. `/agents` lists them. With `isolation: worktree` (or the tool input `"isolation": "worktree"`) the subagent gets a fresh git worktree on its own branch — see [Git worktrees](worktrees.md).

## Hooks

Shell commands that run on agent events, configured in settings (or in a plugin's `hooks/hooks.json`) using Claude Code's format:

```json
{
  "hooks": {
    "PostToolUse": [ { "matcher": "Edit|Write", "hooks": [ { "type": "command", "command": "dotnet format --include \"$DOTCODE_PROJECT_DIR\"", "timeout": 60 } ] } ],
    "PreToolUse":  [ { "matcher": "Bash", "hooks": [ { "type": "command", "command": "python .dotcode/hooks/guard.py" } ] } ],
    "UserPromptSubmit": [ { "hooks": [ { "type": "command", "command": "echo \"Current branch: $(git branch --show-current)\"" } ] } ]
  }
}
```

| Event | When | Can |
|---|---|---|
| `PreToolUse` | Before a tool runs (matcher = tool name regex) | block, allow/deny/ask, rewrite input |
| `PostToolUse` | After a tool runs | add feedback/context for the model |
| `UserPromptSubmit` | Before a prompt is sent | block, add context (stdout) |
| `Stop` / `SubagentStop` | When the agent wants to stop | block with a reason → the agent continues |
| `SessionStart` / `SessionEnd` | Start/resume, exit | add context |
| `Notification` | A permission prompt is shown | notify (desktop toast, sound…) |
| `PreCompact` | Before compaction | — |

**Contract:** the hook receives JSON on stdin (`session_id`, `transcript_path`, `cwd`, `hook_event_name`, `tool_name`, `tool_input`, `tool_response`, `prompt` …). Exit `0` = success (stdout may be JSON: `{"decision":"block","reason":…}`, `{"continue":false,"stopReason":…}`, `{"systemMessage":…}`, `{"hookSpecificOutput":{"permissionDecision":"allow|deny|ask","updatedInput":{…},"additionalContext":"…"}}`); exit `2` = block with stderr as the reason; other codes = non-blocking error. Hooks run with Git Bash on Windows (cmd as fallback); `DOTCODE_PROJECT_DIR` / `CLAUDE_PROJECT_DIR` are set. Disable all hooks with `"disableAllHooks": true`.

## Output styles

`default`, `explanatory` (adds "★ Insight" notes), `learning` (asks you to write small pieces, `TODO(human)`), or custom `.dotcode/output-styles/<name>.md` (frontmatter `name`, `description`, `keep-coding-instructions`).

## Plugins

A plugin bundles commands, agents, skills, hooks and MCP servers. Layout (compatible with Claude Code plugins):

```
my-plugin/
├── .dotcode-plugin/plugin.json     # or .claude-plugin/plugin.json
├── commands/*.md                   # → /my-plugin:command
├── agents/*.md
├── skills/<name>/SKILL.md
├── hooks/hooks.json
└── .mcp.json
```

```bash
dotcode plugin install ./my-plugin                 # local folder
dotcode plugin install https://github.com/org/plugin.git
dotcode plugin marketplace add ./samples/marketplace
dotcode plugin install gravicode-toolkit@gravicode # from a marketplace
dotcode plugin list | enable <name> | disable <name> | remove <name>
```

A marketplace is a folder or git repo with `.dotcode-plugin/marketplace.json` (or `.claude-plugin/marketplace.json`):

```json
{ "name": "gravicode", "owner": { "name": "Gravicode Studios" },
  "plugins": [ { "name": "gravicode-toolkit", "source": "../plugins/gravicode-toolkit", "description": "…" } ] }
```

The sample plugin in [`samples/plugins/gravicode-toolkit`](../../samples/plugins/gravicode-toolkit) has `/gravicode-toolkit:changelog`, `/gravicode-toolkit:explain`, a `code-reviewer` subagent, a `readme-writer` skill and an edit-log hook.

![Plugin command](../images/plugin-command.png)

## MCP servers

DotCode is a Model Context Protocol client (stdio and Streamable HTTP). Tools appear as `mcp__<server>__<tool>`, prompts as `/mcp__<server>__<prompt>`, and resources through `ListMcpResourcesTool` / `ReadMcpResourceTool`.

```bash
dotcode mcp add notes node samples/mcp-server-notes/server.mjs            # user scope (~/.dotcode/mcp.json)
dotcode mcp add --scope project github --transport http https://api.githubcopilot.com/mcp/ -H "Authorization: Bearer $GH_TOKEN"
dotcode mcp add-json fs '{"command":"npx","args":["-y","@modelcontextprotocol/server-filesystem","."]}'
dotcode mcp list        # health check
dotcode mcp get notes   # tools
dotcode mcp remove notes
dotcode --mcp-config servers.json --strict-mcp-config
```

Sources, lowest to highest: `~/.dotcode/mcp.json`, enabled plugins, settings `mcpServers`, project `.mcp.json`, `--mcp-config`. Values support `${env:VAR}`. On Windows, `.cmd` shims such as `npx` are handled automatically. Servers connect in parallel at startup (timeout `MCP_TIMEOUT` ms); `/mcp` shows status and `/mcp reconnect <name>` retries.

![MCP notes server](../images/mcp-notes.png)
