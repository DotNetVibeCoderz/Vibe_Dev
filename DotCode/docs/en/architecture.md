# Architecture

> 🇮🇩 [Bahasa Indonesia](../id/arsitektur.md)

The design is described in detail in [`solution-design.md`](../../solution-design.md) (Indonesian). This page describes what is implemented.

## One engine, many surfaces, one protocol

```
┌──────────────┐  ┌─────────────┐  ┌──────────────────────────────┐
│ TUI (dotcode)│  │ Headless -p │  │ SDKs: .NET · TS · Python · Go│
└──────┬───────┘  └──────┬──────┘  └──────────────┬───────────────┘
       │ in-process       │ in-process             │ JSON-RPC 2.0 (dotcode serve)
       └──────────────────┴────────────┬───────────┘
                                ┌──────▼──────┐
                                │   Engine    │  agent loop · tools · permissions · hooks
                                │             │  context/compaction · sessions · checkpoints
                                │             │  skills · commands · subagents · plugins · MCP
                                └──────┬──────┘
                                ┌──────▼──────┐
                                │  Providers  │  Anthropic · OpenAI (Responses/Chat) · Azure
                                │             │  Gemini · DeepSeek · Ollama · OpenAI-compatible
                                └─────────────┘
```

Every surface consumes the same `AgentEvent` stream, so a feature cannot exist in only one of them.

## Projects

| Project | Responsibility |
|---|---|
| `DotCode.Abstractions` | Normalized message model (`Message`, `ContentPart`: text, image, document, tool_use, tool_result, thinking + `ProviderOpaque`), `ModelRequest`/`ModelEvent`, `ModelCapabilities`, `IModelProvider`, `AgentEvent`s, permission/question contracts. No dependencies. |
| `DotCode.Providers` | Adapters over `HttpClient` + SSE/NDJSON + `Utf8JsonWriter` (no vendor SDKs), model catalog, schema sanitizer, JSON repair, text-tool-protocol fallback, scripted/recording providers. |
| `DotCode.Engine` | `AgentRuntime` (shared services), `AgentSession` (the loop), `ToolExecutor`, `PromptBuilder`, `Compactor`, `ModelRouter`, built-in tools, `PermissionEngine`, `HookRunner`, `SessionStore`, `CheckpointManager`, extension registry, plugin manager, MCP client. |
| `DotCode.Protocol` | JSON-RPC peer (stdio/WebSocket), `AgentServer` (`dotcode serve`), host-tool proxy. |
| `DotCode.Tui` | Renderer (static scrollback + live region), input editor, markdown/highlighter, themes, dialogs, slash commands. |
| `DotCode.Cli` | Entry point: argument parsing, TUI, headless runner, subcommands. NativeAOT. |
| `DotCode.Sdk` | .NET SDK (Spawn / Connect / InProcess). |
| `sdk/typescript`, `sdk/python`, `sdk/go` | Language SDKs. |
| `tools/DotCode.TermCapture` | ConPTY harness: scripted TUI sessions → VT emulation → HTML/PNG screenshots. |

## The agent loop

1. `UserPromptSubmit` hooks → user message (with `@file` attachments) appended and persisted.
2. Loop: ensure context budget (auto-compact) → build request (system prompt with environment, git snapshot, memory files, MCP instructions; history window since the last compaction with tool pairing repaired; tool schemas; transient reminders such as plan mode) → stream the model call with retry/backoff and fallback → append the assistant message.
3. If the model called tools: `ToolExecutor` validates → `PreToolUse` hooks → permissions (dialog/SDK callback) → executes (parallel for read-only batches) → `PostToolUse` hooks → truncation → tool results appended. Repeat.
4. No tool calls: `Stop` hooks may send the agent back to work; otherwise emit `TurnCompleted` with usage and cost.
5. Interrupts leave a valid history (every `tool_use` gets a result).

## Design choices

- **Own message model instead of `IChatClient`**: keeps cache breakpoints, signed thinking and provider-opaque data. Opaque blocks are only replayed to the provider that produced them, so switching models mid-session (`/model`) is safe.
- **NativeAOT**: all JSON goes through source-generated contexts or `Utf8JsonWriter`/`JsonDocument`; trimming/AOT warnings are errors in the core projects. The result is a ~14 MB single binary that starts instantly.
- **Hand-written HTTP adapters** give full control over streaming, retries and AOT.
- **Terminal UI**: an Ink-like model — committed blocks go to real scrollback (native search/copy), the live region is redrawn with synchronized updates. Pre-wrapping with display-width measurement (CJK, emoji, combining marks) keeps the cursor math exact.
- **Compatibility** with Claude Code's files (`CLAUDE.md`, `.claude/` settings, skills, commands, agents, plugins, hooks JSON contract, permission rule syntax) eases migration.

## Differences from the design document

The design proposed one project per concern (Tools, Permissions, Mcp, Extensibility, Host) and one per provider; they are merged into `DotCode.Engine`, `DotCode.Protocol` and `DotCode.Providers` for faster builds — namespaces keep the same boundaries. The Desktop app, IDE extensions, sandboxing, auto mode, Java/Rust SDKs and cloud features are on the [roadmap](../../PLAN.md).
