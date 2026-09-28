# Interactive mode

> 🇮🇩 [Bahasa Indonesia](../id/mode-interaktif.md)

`dotcode` opens a full-screen-free, scrollback-friendly terminal UI modeled closely on Claude Code: finished output is written once into your terminal's scrollback; only the bottom "live" area (streaming text, running tools, spinner, input box, dialogs) is redrawn in place, inside synchronized-update frames so there is no flicker.

![A session building a .NET console app](../images/console-app-done.png)

## Anatomy

| Element | Meaning |
|---|---|
| `> prompt` (shaded) | Your message |
| `●` + text | Assistant response (markdown rendered: headings, lists, tables, syntax-highlighted code) |
| `● Tool(args)` | A tool call — gray/blinking while running, green on success, red on error |
| `⎿  summary` | Tool result summary; edits show a colored diff with line numbers |
| `☐ / ◼ / ☒` | Todo list items (pending / in progress / done) |
| `✻ Verb… (12s · ↓ 1.2k tokens · esc to interrupt)` | Working indicator with a shimmering verb |
| Footer | Mode indicator (`⏵⏵ accept edits on`, `⏵⏵ auto mode on`, `⏸ plan mode on`, `⏵⏵ bypass permissions on`), model, context warnings |

The working indicator cycles its glyph (`·✢✳✶✻✽`), sweeps a shimmer across a whimsical verb that changes as the turn progresses (Pondering → Mulling → Whirring…), and shows elapsed time (`16m 50s`), live output tokens (`↓ 66.4k tokens`) and whether the model is thinking:

![Working indicator](../images/spinner.png)

## Keyboard shortcuts

| Key | Action |
|---|---|
| `Enter` | Send (while busy: queue the message) |
| `Shift+Enter`, `Alt+Enter`, `Ctrl+J`, `\` + `Enter` | New line |
| `Shift+Tab` | Cycle permission mode: default → accept edits → (auto) → plan (→ bypass if enabled) |
| `Esc` | Interrupt the running turn · close menus · twice: clear input |
| `Esc Esc` (empty input) | Rewind to an earlier message (conversation and/or code) |
| `Ctrl+C` | Clear input · interrupt · press twice to exit |
| `Ctrl+D` | Exit (empty input) |
| `↑ / ↓` | Move in multi-line input, otherwise prompt history · navigate menus |
| `Tab` | Accept completion |
| `Ctrl+O` | Open the full-screen transcript viewer (thinking, complete tool inputs and outputs) |
| `Ctrl+R` | Search prompt history (type to filter, `Ctrl+R` again for older matches, `Enter` to use, `Esc` to cancel) |
| `Ctrl+T` | Show the todo list |
| `Ctrl+L` | Clear the screen |
| `Ctrl+A/E`, `Ctrl+B/F`, `Alt+B/F` | Line start/end, char/word movement |
| `Ctrl+U/K`, `Ctrl+W`, `Alt+D`, `Ctrl+Y` | Kill to start/end, delete word back/forward, yank |
| `Ctrl+_`, `Ctrl+Z` | Undo |
| `?` (empty input) | Show shortcuts |

## Input prefixes

| Prefix | Mode |
|---|---|
| `/` | Slash command (with autocomplete of built-ins, custom commands, skills, plugin commands, MCP prompts) |
| `!` | Bash mode — run a shell command yourself; its output is added to the conversation |
| `#` | Memorize — append the text to `DOTCODE.md` |
| `@path` | Attach a file (contents are included and marked as read) or a directory listing; completes file paths |

Large pastes collapse into `[Pasted text #1 +42 lines]`, like Claude Code.

## Slash commands

| Command | Description |
|---|---|
| `/help` | Help and all commands |
| `/clear` (`/reset`, `/new`) | Clear the conversation |
| `/compact [instructions]` | Summarize the conversation to free context |
| `/context` | Colored grid of context usage by category |
| `/cost` (`/usage`) | Cost, durations, code changes and tokens per model |
| `/model [provider:model]` | Pick or set the model (lists every configured provider) |
| `/effort [level]` | Reasoning effort: off, low, medium, high, xhigh |
| `/theme` | Theme picker with live preview |
| `/config` | Settings: theme, glyph set (font), spinner, input style, reduced motion, thinking, tips, auto-compact… |
| `/output-style [name]` | default, explanatory, learning or custom |
| `/permissions` | View rules; `/permissions allow|ask|deny <Rule>`; `/permissions remove <Rule>` |
| `/add-dir <path>` | Add a working directory |
| `/init` | Create `DOTCODE.md` for the repository |
| `/memory` | Show memory files; `/memory add <text>` |
| `/resume [id]` | Resume a previous session (picker) |
| `/rewind` | Restore code and/or conversation to an earlier point |
| `/export [file]` | Export the conversation to Markdown |
| `/mcp` | MCP server status and tools; `/mcp reconnect <name>` |
| `/agents`, `/skills`, `/hooks` | List subagents, skills, hooks |
| `/plugin` | List plugins; `install`, `remove`, `enable`, `disable`, `marketplace add` |
| `/todos`, `/bashes` | Todo list; background shells (`/bashes kill <id>`) |
| `/review`, `/security-review` | Review the current changes |
| `/status`, `/doctor` | Session status; installation diagnostics |
| `/about` | Version and credits |
| `/exit` | Quit |

Custom commands (`.dotcode/commands/*.md`), skills (`/skill-name`), plugin commands (`/plugin:command`) and MCP prompts (`/mcp__server__prompt`) appear in the same menu.

![Slash command menu](../images/slash-commands.png)

## Permission dialogs

When a tool needs approval, the input box is replaced by a dialog with the exact command or a diff preview:

![Permission dialog for a shell command](../images/permission-bash.png)

Options: **Yes** · **Yes, and don't ask again for `<prefix>` commands** (saved to `.dotcode/settings.local.json`) or, for edits, **Yes, allow all edits during this session** (switches to accept-edits) · **No, and tell DotCode what to do differently** (`Esc`). Keys `1`–`3`, `y`/`n`, arrows and Enter work.

## Plan mode

`Shift+Tab` twice enters plan mode: the model may only research, then presents a plan with `ExitPlanMode`:

![Plan approval](../images/plan-mode.png)

## Themes and fonts

Pick with `/theme` (live preview) or `--theme <name>`; persist with `dotcode theme set <name>`.

Built-in themes: `dark` (default), `light`, `dark-daltonized`, `light-daltonized`, `dark-ansi`, `light-ansi` (the Claude Code palettes) plus DotCode originals `dotnet`, `dracula`, `nord`, `solarized-dark`, `monokai`, `gruvbox`, `catppuccin`, `matrix`, `ocean-light`.

| Dracula | Nord | Catppuccin |
|---|---|---|
| ![](../images/theme-dracula.png) | ![](../images/theme-nord.png) | ![](../images/theme-catppuccin.png) |

A terminal UI cannot change your terminal's font, so DotCode adapts to it instead with **glyph sets**: `unicode` (default, Claude Code look), `ascii` (for fonts without box/symbol glyphs — also switches borders and spinner) and `nerd` (Nerd Font icons). Other style options: input style (`lines` or boxed `rounded`/`single`/`double`/`heavy`), spinner (`claude`, `dots`, `line`, `star`, `bounce`, `arc`, `dotnet`), accent color and reduced motion. All are in `/config`.

Custom theme — `~/.dotcode/themes/sunset.json`:

```json
{ "base": "dark", "displayName": "Sunset", "colors": { "brand": "#FF7A59", "brandShimmer": "#FFC2AE", "userMessageBackground": "#2B1F2A" }, "border": "rounded", "spinner": "dots" }
```

## Transcript viewer, history search and vim mode

![Transcript viewer](../images/transcript-viewer.png)

**Transcript viewer (`Ctrl+O`)** shows the whole conversation in full detail: thinking blocks, every tool call with its complete JSON input and output (up to 500 lines per result), and the text still streaming. Keys: `↑/↓` or `j/k` scroll, `PgUp/PgDn` (`b`/`f`, `Space`) page, `g`/`G` top/bottom, `/` search, `n`/`N` next/previous match, `Esc`, `q` or `Ctrl+O` close. Output produced while the viewer is open is shown when you close it; a permission prompt from the agent closes the viewer so it is never hidden. For more detail in the normal view, turn on *Verbose output* in `/config` (or start with `--verbose`).

**History search (`Ctrl+R`)** — like a shell's reverse search: type part of an earlier prompt, press `Ctrl+R` again to go to older matches, `Enter`/`Tab` puts the match in the prompt (it is not sent), `Esc`/`Ctrl+G` restores what you had typed.

![History search](../images/history-search.png)

**Vim mode** — `/vim` (or `"tui": { "vim": true }`) switches the prompt to vim keys. `Esc` enters NORMAL mode, the footer shows `-- NORMAL --` / `-- INSERT --`. Supported: `h l w b e W B E 0 ^ $ gg G`, counts (`3w`, `2dd`), operators `d c y` with motions or doubled (`dw`, `cw`, `ce`, `d$`, `dd`, `cc`, `yy`), `x X s S D C r ~ p P u`, and `i a I A o O`. `j`/`k` move between lines and walk the prompt history on the first/last line. `Enter` sends in either mode; while a turn runs with an empty prompt, `Esc` still interrupts.

![Vim mode](../images/vim-mode.png)

## Notifications and language

- `"tui": { "notifications": "bell" }` (default) rings the terminal bell when DotCode needs your input (permission, question, plan) or finishes a turn that took 15 seconds or more. `"osc9"` sends a desktop notification (iTerm2, WezTerm, Windows Terminal, kitty…), `"osc777"` uses the urxvt/foot/Ghostty format, `"off"` disables it.
- `"tui": { "language": "id" }` shows the interface in Bahasa Indonesia (welcome panel, footer, shortcuts, tips, permission dialogs, history search, transcript viewer, exit summary). `"auto"` follows `DOTCODE_LANG` or the OS language; the default is `"en"`. The model's replies are not affected — just ask it to answer in your language.

![Indonesian UI](../images/ui-indonesian.png)

## Status line

`"statusLine": { "type": "command", "command": "…" }` runs a command (bash on Windows via Git Bash) with session JSON on stdin (`model`, `cwd`, `workspace`, `cost`, `permission_mode`) and shows its first output line under the footer.

## Sessions

Every conversation is saved as JSONL. `dotcode -c` continues the latest one, `dotcode -r` opens a picker, `dotcode -r <id>` resumes a specific one (`--fork-session` copies it). On exit DotCode prints cost, durations and the resume command.
