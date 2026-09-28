# Built-in tools

> 🇮🇩 [Bahasa Indonesia](../id/tools.md)

| Tool | Purpose | Read-only | Needs permission (default mode) |
|---|---|---|---|
| `Read` | Read files with line numbers (offset/limit), images, PDFs, notebooks | ✔ | outside working dirs / secret-looking files |
| `Write` | Create or overwrite a file (must Read existing files first) | | ✔ |
| `Edit` | Exact string replacement; `replace_all`; preserves CRLF and BOM | | ✔ |
| `NotebookEdit` | Replace/insert/delete Jupyter cells | | ✔ |
| `Glob` | Find files by pattern (`**/*.cs`, `{a,b}`), newest first | ✔ | |
| `Grep` | Regex search via ripgrep (managed fallback); content/files/count modes, context lines | ✔ | |
| `LSP` | Code intelligence from language servers: definition, implementation, references, hover, document/workspace symbols, diagnostics | ✔ | outside working dirs |
| `Bash` | Run bash (Git Bash on Windows); persistent working directory; timeouts; background runs | read-only commands only | ✔ |
| `PowerShell` | Run PowerShell (Windows PowerShell or pwsh) | read-only commands only | ✔ |
| `BashOutput`, `KillShell` | Read output of / stop background shells | ✔ | |
| `WebFetch` | Fetch a URL, convert HTML to markdown, answer a prompt with the fast model | ✔ | ✔ (per domain) |
| `WebSearch` | Web search (Tavily with `TAVILY_API_KEY`, DuckDuckGo fallback) | ✔ | ✔ |
| `TodoWrite` | Maintain the visible task list | ✔ | |
| `Agent` | Launch a subagent (general-purpose, Explore, Plan, custom) with its own context | ✔ | |
| `Skill` | Load a skill's instructions | ✔ | |
| `AskUserQuestion` | Ask 1–4 multiple-choice questions | ✔ | |
| `ExitPlanMode` | Present a plan for approval (plan mode only) | ✔ | |
| `ListMcpResourcesTool`, `ReadMcpResourceTool` | MCP resources | ✔ | ✔ |
| `mcp__<server>__<tool>` | Tools from MCP servers | per server hint | ✔ |

## Execution model

- All tool calls in one model response are validated (JSON schema `required`, tool-specific checks), then run: **consecutive read-only, concurrency-safe calls run in parallel** (up to 10), others serially, results kept in order.
- Every call passes through `PreToolUse` hooks → the permission engine (and a dialog if needed) → execution → `PostToolUse` hooks.
- Errors are returned to the model as error tool results; the loop continues so the model can recover.
- Results over the tool's limit (30k chars for shells) are truncated head+tail and the full output is saved to a temp file the model can Read or Grep.
- The shell working directory persists across calls (and is shared with subagents). Environment: `DOTCODE=1`, `GIT_TERMINAL_PROMPT=0`, pagers disabled. Timeout default 2 min, max 10 min; the whole process tree is killed on timeout or `Esc`.
- `run_in_background` starts servers/watchers; read their output with `BashOutput`, stop with `KillShell` or `/bashes kill`.

## LSP (language servers)

The `LSP` tool gives the model precise, semantic answers instead of text search:

| Operation | Input | Answer |
|---|---|---|
| `goToDefinition`, `goToImplementation` | `file_path`, `line`, `character` (1-based) | `path:line:col` plus the source line |
| `findReferences` | same | every usage across the workspace, grouped count by file |
| `hover` | same | type signature and documentation |
| `documentSymbol` | `file_path` | outline (classes, methods, fields…) with line numbers |
| `workspaceSymbol` | `file_path` (any file of the language), `query` | matching symbols in the project |
| `diagnostics` | `file_path` | compiler / type-checker errors and warnings |

**Servers.** DotCode picks the server by file extension and starts it on first use (one per server and workspace root; the root is the nearest folder with a marker such as `tsconfig.json`, `pyproject.toml`, `go.mod`, `Cargo.toml` or `*.csproj`). Built-in defaults are used when their executable is on `PATH` or in the project's `node_modules/.bin`:

| Name | Command | Files | Install |
|---|---|---|---|
| typescript | `typescript-language-server --stdio` | .ts .tsx .js .jsx .mjs .cjs | `npm i -g typescript typescript-language-server` (TypeScript 5.x: TypeScript 7's native compiler no longer ships `tsserver`) |
| python | `pyright-langserver --stdio` | .py .pyi | `npm i -g pyright` |
| go | `gopls` | .go | `go install golang.org/x/tools/gopls@latest` |
| rust | `rust-analyzer` | .rs | `rustup component add rust-analyzer` |
| csharp | `csharp-ls` | .cs | `dotnet tool install -g csharp-ls` |
| cpp | `clangd` | .c .h .cc .cpp .hpp | LLVM clangd |
| java | `jdtls` | .java | Eclipse JDT LS |

`dotcode doctor` lists which are found. Add or override servers in settings:

```jsonc
"lsp": {
  "enabled": true,                 // false removes the tool
  "diagnosticsAfterEdit": true,    // report new errors after Edit/Write
  "servers": {
    "python": { "command": "pylsp", "args": [] },                         // replace a built-in
    "cpp": { "disabled": true },
    "zig": { "command": "zls", "extensions": [".zig"], "rootMarkers": ["build.zig"] }
  }
}
```

**Errors after edits.** When a language server is already running for a file, `Edit` and `Write` send it the new content and append any errors it now reports (`<new-diagnostics>`), so the model fixes type errors immediately without running a build. A server is never started just for this.

Notes: the first request after start-up waits (up to 10 s) until the server has analyzed the file, because some servers answer with partial results while loading the project. A server that crashes is restarted on the next request. A command on `PATH` that cannot actually run (for example a rustup proxy without the component installed) shows as found in `doctor`, and the tool then reports the start-up error.

## Subagents

The `Agent` tool starts a child session with its own context window, system prompt, tool allowlist and model (role or explicit). It shares permissions, checkpoints and the UI; its progress is shown nested under the call, and only its final report returns to the main conversation. Built-in types: `general-purpose` (all tools), `Explore` (read-only, fast model), `Plan` (read-only, planner model). Define your own in `.dotcode/agents/*.md` — see [Extensions](extensions.md#subagents).

## Context management

DotCode tracks the tokens used by each request. When the conversation approaches the auto-compact threshold (85% of the usable window by default) it summarizes the history into a structured continuation message (requests, files, errors, pending tasks, next step, plus the todo list) and continues. `/compact [instructions]` does it on demand; `/context` shows the breakdown.
