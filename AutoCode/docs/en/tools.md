# Tools

> Auto Code — Gravicode Studios, led by Kang Fadhil

Tools are what let the agent do anything beyond talk. Each declares a capability, which is what the
permission engine gates on.

| Tool | Capability | Purpose |
| --- | --- | --- |
| `Read` | reads files | Read a file slice with line numbers |
| `Write` | writes files | Create or replace a file |
| `Edit` | writes files | Exact string replacement |
| `MultiEdit` | writes files | Several replacements in one transaction |
| `Glob` | reads files | Find files by pattern, newest first |
| `Grep` | reads files | Regex search across contents |
| `List` | reads files | Explore a directory tree |
| `Bash` | runs commands | Execute a shell command |
| `TodoWrite` | none | Maintain the visible task list |
| `WebFetch` | network | Fetch a URL as text |
| `Task` | reads + writes | Dispatch a subagent |
| `CodeSearch` | reads files | Semantic search (when the index is enabled) |
| `Verify` | runs commands | Run the project's verification commands |
| `mcp__*` | all | Tools contributed by MCP servers |

## File safety

Three rules that exist because their absence is how agents corrupt code:

1. **Read before edit.** `Edit` and `MultiEdit` refuse to touch a file that has not been read in
   this session. The agent has to look before it patches.
2. **Unique match required.** `Edit` fails when `old_string` appears more than once, unless
   `replace_all` is set. Guessing which occurrence was meant is not a recoverable mistake.
3. **Staleness detection.** If a file changed on disk since it was read, the edit is refused. The
   agent re-reads and tries again with current content.

`Write` additionally refuses to overwrite an existing non-empty file that has not been read.

`MultiEdit` is transactional: if any edit in the batch fails, nothing is written.

## Search

`Glob` and `Grep` skip build and VCS directories automatically — `.git`, `node_modules`, `bin`,
`obj`, `dist`, `target`, `__pycache__`, `.venv` and roughly twenty more.

```jsonc
// Grep supports three output modes
{ "pattern": "class \\w+Service", "output_mode": "content", "-n": true, "-C": 2 }
{ "pattern": "TODO", "output_mode": "files_with_matches" }
{ "pattern": "TODO", "output_mode": "count", "glob": "**/*.cs" }
```

## Bash

The working directory persists between calls within a session; shell variables do not, because each
call is a fresh process.

- Default timeout `bashTimeoutMs` (120 s), capped at 600 s per call.
- Output truncated at 30 000 characters.
- stdin is closed immediately, so a command that waits for input fails fast instead of hanging.
- The shell is `pwsh`/`powershell` on Windows and `$SHELL` elsewhere. Override with `"shell"`.

## Verify

`Verify` appears only when `verifyCommands` is configured:

```jsonc
{ "verifyCommands": ["dotnet build", "dotnet test", "dotnet format --verify-no-changes"] }
```

The system prompt then instructs the agent to run it after making changes. This is the difference
between an agent that thinks it is done and one that knows.

## CodeSearch

Semantic search finds code by what it does rather than what it is called — "where do we validate
refresh tokens" works even when none of those words appear.

```jsonc
// Local and free — needs Ollama running
{
  "enableSemanticIndex": true,
  "embeddings": { "kind": "Ollama", "model": "nomic-embed-text" }
}
```

```jsonc
// Fully offline — no server, no network, no key
{
  "enableSemanticIndex": true,
  "embeddings": {
    "kind": "Onnx",
    "modelPath": "models/model.onnx",
    "vocabPath": "models/vocab.txt"
  }
}
```

Embeddings are configured independently of the chat provider, so the index can run on a local model
while you reason with a hosted one. See [providers](providers.md#embeddings) for every backend.

Build the index with `/index`. It lives in memory for the session; rebuilding is cheap and always
current, which for a codebase under active edit is the right trade.

Use `Grep` when you know the exact symbol. `CodeSearch` is for when you do not.

## Restricting the toolset

```jsonc
{ "disabledTools": ["WebFetch", "mcp__*"] }
```

```bash
autocode --disallowed-tools WebFetch,Bash
autocode --allowed-tools Read,Grep,Glob     # adds allow rules, not a whitelist
```

Subagents get their own allow-list in their definition — see [subagents](subagents.md).

## Writing your own

Auto Code has no plugin API for compiled tools by design: an MCP server is the supported extension
point, works across every MCP client, and does not have to be written in C#. See [MCP](mcp.md).

If you are embedding `AutoCode.Core` in your own application, implement `IAgentTool` and register it
on the `ToolRegistry` — that is the same interface the built-in tools use.
