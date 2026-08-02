# CLI reference

> Auto Code — Gravicode Studios, led by Kang Fadhil

```
autocode [options] [prompt]
autocode <command> [args]
```

## Commands

| Command | Purpose |
| --- | --- |
| `config show` | Resolved configuration and where it came from |
| `config init` | Scaffold `.autocode/settings.json` |
| `config path` | The settings files being read |
| `mcp` | Configured MCP servers |
| `sessions` | Saved sessions for this workspace |
| `sessions rm <id>` | Delete one |
| `doctor` | Verify installation, configuration and a live model request |

## Options

| Option | Meaning |
| --- | --- |
| `-p`, `--print` | Run once, print the result, exit |
| `--output-format <fmt>` | `text` (default), `json`, `stream-json` |
| `-c`, `--continue` | Resume the most recent session in this workspace |
| `-r`, `--resume <id>` | Resume a specific session |
| `--model <id>` | Override the model for this run |
| `--provider <name>` | Override the provider profile |
| `--permission-mode <m>` | `ask`, `acceptEdits`, `plan`, `bypassPermissions` |
| `--allowed-tools <list>` | Comma-separated allow rules |
| `--disallowed-tools <list>` | Comma-separated tool names to disable |
| `--dangerously-skip-permissions` | Same as `--permission-mode bypassPermissions` |
| `--cwd <path>` | Workspace root to operate in |
| `--lang <en\|id>` | Interface and reply language |
| `--no-context` | Skip `AUTOCODE.md` / `CLAUDE.md` discovery |
| `-h`, `--help` | Show usage |
| `-v`, `--version` | Show the version |

## Slash commands

Available inside an interactive session.

| Command | Purpose |
| --- | --- |
| `/help` | List commands |
| `/clear` | Start a fresh conversation |
| `/compact` | Summarise the conversation to free context |
| `/cost` | Token and cost accounting |
| `/status` | Provider, model, workspace, permissions, session |
| `/model [id]` | Show or switch the model |
| `/provider [name]` | Show or switch the provider profile |
| `/permissions [mode]` | Show or set the permission mode |
| `/tools` | Tools available to the model |
| `/agents` | Subagents that can be dispatched |
| `/teams <name> <brief>` | Run an agent team |
| `/skills` | Installed skills |
| `/mcp` | MCP connection status |
| `/context` | Context files in effect |
| `/sessions` | Saved sessions for this workspace |
| `/index` | Build the semantic code index |
| `/export [path]` | Write the transcript to markdown |
| `/init` | Generate an `AUTOCODE.md` for the project |
| `/language <en\|id>` | Switch interface and reply language |
| `/exit` | Leave |

Every installed skill is also a slash command: `/deploy`, `/release`, and so on.

## Input

- Enter submits.
- End a line with `\` to continue onto the next, for multi-line prompts.
- **Ctrl+C** interrupts the turn in progress; the session survives. Press it again at an idle prompt
  to exit.

## Output formats

**`text`** *(default)* — assistant prose, streamed.

**`json`** — one object, after the run:

```json
{
  "session_id": "a1b2c3d4e5f6",
  "model": "gpt-4.1",
  "provider": "openai",
  "result": "…",
  "denials": [],
  "usage": { "input_tokens": 1420, "output_tokens": 310, "requests": 3, "cost_usd": 0.0053 }
}
```

**`stream-json`** — newline-delimited JSON, one object per event, as it happens:

```json
{"type":"turn_started","session_id":"a1b2c3d4","iteration":1,"timestamp":"…"}
{"type":"tool_call_started","call_id":"c1","tool_name":"Read","summary":"Read(src/a.cs)","timestamp":"…"}
{"type":"tool_call_completed","call_id":"c1","tool_name":"Read","success":true,"display":"Read src/a.cs (120 lines)","elapsed_ms":8,"timestamp":"…"}
{"type":"assistant_text","text":"The file defines…","is_final":false,"timestamp":"…"}
{"type":"turn_completed","iterations":2,"input_tokens":1420,"output_tokens":310,"cost_usd":0.0053,"elapsed_ms":4210,"timestamp":"…"}
```

Event types: `turn_started`, `assistant_text`, `assistant_thinking`, `tool_call_started`,
`tool_call_completed`, `tool_call_denied`, `todo_updated`, `subagent_started`, `subagent_completed`,
`compaction`, `notice`, `error`, `turn_completed`.

## Exit codes

| Code | Meaning |
| --- | --- |
| `0` | Success |
| `1` | Error, or the turn was aborted |
| `2` | Bad usage — unknown option, or `--print` with no prompt |
| `130` | Cancelled |

## Environment variables

| Variable | Purpose |
| --- | --- |
| `AUTOCODE_PROVIDER` | Provider profile, same as `--provider` |
| `AUTOCODE_MODEL` | Model id, same as `--model` |
| `AUTOCODE_<PATH>` | Any setting, `__` as the section separator |
| `AUTOCODE_DEBUG=1` | Print full stack traces on failure |
| `NO_COLOR` | Disable colour output |
| `OPENAI_API_KEY` etc. | Recognised vendor keys — see [providers](providers.md) |

## Examples

```bash
autocode
autocode "why does the build fail on CI?"
autocode -p "list every public endpoint" --output-format json | jq -r .result
autocode --continue
autocode --permission-mode plan "how would you add multi-tenancy?"
autocode --provider ollama --model qwen2.5-coder:14b
git diff --staged | autocode -p "write a commit message for this"
autocode -p "fix the failing test" --permission-mode acceptEdits
autocode doctor
```
