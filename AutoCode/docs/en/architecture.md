# Architecture

> Auto Code — Gravicode Studios, led by Kang Fadhil

## Projects

| Project | Responsibility |
| --- | --- |
| `AutoCode.Core` | Agent loop, configuration, permissions, hooks, skills, subagents, sessions, context, cost, semantic index |
| `AutoCode.Providers` | The only code that knows a vendor by name |
| `AutoCode.Providers.Onnx` | Offline embeddings. Separate because ONNX Runtime ships a native library most installs never need |
| `AutoCode.Tools` | Built-in tools |
| `AutoCode.Mcp` | MCP client and tool adaptation |
| `AutoCode.Cli` | Terminal interface, slash commands, entry point |
| `AutoCode.Tests` | xUnit suite |

Dependencies flow one way: `Cli → {Core, Providers, Tools, Mcp} → Core`. Nothing in `Core` knows
about Spectre.Console or about any vendor.

## The loop

`AgentLoop.RunTurnAsync` is the centre of the system.

```
user message
     │
     ▼
UserPromptSubmit hook ──── blocked? ──▶ end turn
     │
     ▼
┌──▶ build request (system prompt + transcript + tool declarations)
│    │
│    ▼
│  stream from IChatClient ──▶ emit text / reasoning / usage events
│    │
│    ▼
│  any tool calls?  ── no ──▶ end turn
│    │ yes
│    ▼
│  for each call:
│    PreToolUse hook  ── veto ──▶ result: "blocked by hook"
│    permission check ── deny ──▶ result: "permission denied"
│                     ── ask  ──▶ prompt the user
│    execute tool
│    PostToolUse hook (may inject context)
│    │
│    ▼
└── append results, loop
```

Two decisions shape everything downstream.

**Tool execution is orchestrated manually**, not delegated to the chat client's automatic function
invocation. Every call has to pass three gates first — lifecycle hooks, the permission engine, and
the renderer that shows the user what is about to happen. Automatic invocation would bypass all
three.

**A denial is a tool result, not an exception.** The model is told it was refused and why, and can
adapt. An agent that stalls on refusal is one you have to restart.

### Parallelism

Read-only tools in the same batch run concurrently; anything that mutates state runs in sequence, so
the user sees a coherent order of events. Results are then reordered to match the model's call
order, because several providers validate that.

### Compaction

When `LastContextTokens / contextWindow` exceeds `compactionThreshold` (default `0.82`), the older
half of the transcript is replaced by a written summary produced on `smallModel`. The recent tail is
kept verbatim — that is what the agent is actively working on.

The contract is **compact before the next request**, not compact immediately. Utilization is only
known once a response reports its usage, so the check runs at the top of each loop iteration and acts
on what the previous request cost. In practice that means compaction lands either mid-turn, before
the next tool round, or at the start of the following turn — always before the request that would
have overflowed.

`LastContextTokens` is per-request, not cumulative: it measures how full the window was on the last
call, which is the quantity that matters. A session with a large *total* token spend but a short
conversation is not near its limit.

The split point is never allowed to orphan a tool result from the call that requested it. An
unpaired `FunctionResultContent` is a hard API error on every provider.

**Known limitation.** `ContextCompactor` keeps the last eight messages verbatim, so a conversation of
roughly ten messages or fewer cannot be compacted at all. A short transcript made of a few enormous
messages — one `Read` of a very large file, say — therefore fills the window with nothing safe to
fold away. Auto Code detects this and emits a warning once, rather than letting it become a
context-length error the user cannot explain. The remedies are `/clear` or reading smaller slices.

## Providers

`ProviderFactory` maps a `ProviderProfile` to an `IChatClient`. It is the only place a vendor is
named.

- **OpenAI-compatible** goes through `Microsoft.Extensions.AI.OpenAI`.
- **Anthropic** and **Gemini** are implemented directly against their wire formats.

Those two are hand-written for a reason. Anthropic needs thinking blocks replayed with their
signatures across tool-use round trips; Gemini needs its function calls given synthetic ids and its
JSON Schemas reduced to the OpenAPI subset it accepts. Generic adapters drop both details, and the
resulting failures are opaque.

`ProviderIntegrationTests` exercises both against a real local HTTP server, asserting on the exact
bytes sent.

## Permissions

```
deny  →  mode  →  allow  →  ask
```

Deny wins over everything, including `bypassPermissions`. Read-only capability short-circuits to
allow before the mode is even consulted.

Rules are `Tool` or `Tool(pattern)`, matched against a subject extracted from the arguments — the
command for shell tools, the path for file tools, the URL for network tools.

`ToolExecutor` implements the three gates once. `AgentLoop` and subagents both use it, which is what
makes "the subagent skipped the approval prompt" structurally impossible rather than merely
untested.

## Subagents

Built on Microsoft Agent Framework's `ChatClientAgent`. Each gets its own session, system prompt and
filtered toolset. Tool calls are wrapped in `GatedToolFunction`, which routes them through the same
`ToolExecutor` as the main loop.

Nesting is disabled — a subagent that can spawn subagents turns a bounded run into an unbounded one
at a depth nobody approved.

## Embeddings

Embeddings resolve independently of chat, through `EmbeddingOptions.Resolve`. This exists because
the two are genuinely different decisions: Anthropic has no embedding endpoint, so an `Auto` setting
on a Claude profile resolves to `None` rather than silently disabling the index with no explanation.

Backends: `Ollama` (OllamaSharp's native `/api/embed` — `OllamaApiClient` already implements
`IEmbeddingGenerator`, so this is direct use rather than an adapter), `OpenAICompatible`, and `Onnx`.

The ONNX case is wired in the CLI rather than in `EmbeddingFactory`, so the native ONNX Runtime
dependency stays confined to the composition root. Anything embedding `AutoCode.Core` can leave that
project out entirely.

`OnnxEmbeddingGenerator` mean-pools token states over the attention mask and L2-normalises. Both
details matter: taking `[CLS]` instead of pooling produces vectors that cluster far more weakly, and
failing to mask padding makes a batched embedding differ from the same text embedded alone — neither
fails loudly, they just quietly return worse matches.

Vector width is learned from the first batch. A stated dimension that disagrees with the model is a
silent failure, since every similarity comparison is then skipped and the index returns nothing; a
configured value is kept as an assertion that fails loudly instead.

## The semantic index

`Microsoft.Extensions.VectorData` provides the contract; Auto Code implements
`VectorStoreCollection<string, CodeChunk>` itself rather than taking a connector dependency.

Two reasons. A code index is disposable — cheaper to rebuild than to manage a database alongside a
CLI. And decisively, the available in-memory connectors pin an older Abstractions version than
Microsoft Agent Framework requires; the two cannot coexist. Implementing the contract keeps the seam
intact, so swapping in Qdrant or Postgres later is a one-line change at the call site.

Files are chunked on blank-line boundaries with overlap, which keeps a function and its signature in
the same chunk far more often than a fixed window would.

## Configuration

Seven layers, lowest to highest: presets, `app.config`, user settings, project settings, local
settings, `AUTOCODE_*` environment variables, command-line flags. Built on
`Microsoft.Extensions.Configuration`, so standard binding rules apply.

`env:NAME` indirection is resolved at read time, not load time, so a rotated key takes effect
without a restart.

## Sessions

JSON transcripts under `~/.autocode/sessions/<workspace>-<hash>/`, scoped per workspace so
`--continue` in one repository never resumes a conversation from another.

Serialisation uses `AIJsonUtilities.DefaultOptions` for its polymorphic `AIContent` converters —
without them a transcript containing tool calls round-trips as empty messages. Writes are
write-then-move, so a crash mid-write cannot leave a truncated transcript.

## The terminal

`IAgentUserInterface` is what lets one loop drive three surfaces: the Spectre.Console REPL, a
headless `--print` run, and a `stream-json` event stream. `Core` has no console dependency.

Assistant prose is written as it streams rather than buffered and re-rendered — a response that
appears word by word reads as responsive, one that appears at once reads as a hang.

## Extension points

| Point | Contract |
| --- | --- |
| Tool | `IAgentTool` |
| Provider | `IChatClient` via `ProviderFactory` |
| UI surface | `IAgentUserInterface` |
| Vector store | `VectorStoreCollection<string, CodeChunk>` |
| Out-of-process capability | MCP server |

There is deliberately no compiled-plugin API. Loading arbitrary assemblies into the agent's process
would let a plugin bypass the permission engine, and that boundary is worth keeping absolute.
