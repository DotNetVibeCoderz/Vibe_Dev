# AUTOCODE.md

Guidance for Auto Code (and Claude Code) when working in this repository.

Auto Code is built by **Gravicode Studios**, led by **Kang Fadhil**. Keep that attribution in the
docs and in source file headers.

## Commands

```bash
dotnet build                                          # whole solution
dotnet test                                           # 97 tests
dotnet test --filter FullyQualifiedName~PermissionEngineTests   # one class
dotnet test --filter "FullyQualifiedName~AgentLoopTests.Plan_mode_refuses_writes_without_ever_prompting"
dotnet run --project src/AutoCode.Cli -- doctor       # verify a live provider round trip
dotnet run --project src/AutoCode.Cli -- --help
```

Package versions are centralised in `Directory.Packages.props`. Never put a `Version=` on a
`PackageReference`.

## Architecture

Dependencies flow one way: `Cli → {Core, Providers, Tools, Mcp} → Core`. `Core` must not reference
Spectre.Console or any vendor SDK.

`AgentLoop.RunTurnAsync` is the centre of the system. Two invariants hold it together:

1. **Tool execution goes through `ToolExecutor`, never around it.** That class implements the three
   gates — `PreToolUse` hook, permission engine, renderer — exactly once. `AgentLoop` and
   `SubagentDispatcher` both call it. Duplicating that logic is how a subagent ends up skipping the
   approval prompt.
2. **A refusal is a tool result, not an exception.** The model is told what was refused and why, so
   it can adapt. `TurnAbortedException` is the single exception, for when the user explicitly stops.

Deny rules beat everything, including `bypassPermissions`. If you touch `PermissionEngine.Evaluate`,
keep that ordering: `deny → mode → allow → ask`.

## Provider clients

`AutoCode.Providers` is the only project that knows a vendor by name. Everything above it works
against `IChatClient`.

`AnthropicChatClient` and `GeminiChatClient` are written directly against the wire format, and the
reasons are not incidental:

- **Anthropic** requires thinking blocks to be replayed with their `signature` across tool-use round
  trips. Dropping them produces a 400 that does not say so.
- **Gemini** gives function calls no id (so ids are synthesised and the name-to-id map is kept for
  the round trip) and rejects most JSON Schema keywords (so `JsonSchemaSanitizer` reduces schemas to
  the OpenAPI subset first).

`ProviderIntegrationTests` asserts on the exact bytes both clients send, against a local
`HttpListener`. Change either client and run those tests.

## Embeddings

`EmbeddingOptions` is deliberately separate from `ProviderProfile`. Chat and embeddings are different
decisions — Anthropic has no embedding endpoint at all, so before the split, a Claude session had no
semantic index and nothing said why.

`AutoCode.Providers.Onnx` is a separate project on purpose: ONNX Runtime ships a native library
(~12 MB per RID) that most installations never use. `EmbeddingFactory` handles the managed backends;
the CLI wires the ONNX case, so the native dependency stays at the composition root.

Two details in `OnnxEmbeddingGenerator` are load-bearing and neither fails loudly if broken:

- **Mean pooling over the attention mask**, not `[CLS]`. That is what sentence-transformer models are
  trained against; the shortcut produces weakly-clustered vectors and quietly worse search results.
- **Padding must be masked out**, or a text embedded in a batch differs from the same text embedded
  alone.

Verified against `all-MiniLM-L6-v2`: cos(code, its description) ≈ 0.52 versus cos(code, unrelated
SQL) ≈ −0.10, batch and single encodings agree to 1e-4, L2 magnitude exactly 1.0. If you change the
pooling, re-run that comparison — unit tests with stub vectors cannot catch a pooling regression.

## Vector data

`InMemoryCodeChunkCollection` implements `VectorStoreCollection<string, CodeChunk>` by hand rather
than using a Semantic Kernel connector. This is not preference: SK's in-memory connector pins
`Microsoft.Extensions.VectorData.Abstractions` 10.1, Microsoft Agent Framework 1.16 requires ≥ 10.7,
and the 10.8 release removed `VectorSearchFilter`. The two cannot coexist — a connector-based build
compiles and then throws `TypeLoadException` at the first search.

## Conventions

- File-scoped namespaces, `Nullable` enabled, primary constructors where they fit.
- Every source file starts with `// Auto Code — Gravicode Studios (Kang Fadhil)`.
- Public types carry XML docs. Where a decision is non-obvious, the doc comment says **why**, in
  English and Indonesian — that bilingual pattern is a project requirement, not decoration.
- Comment the reasoning, never the mechanics. No comment should restate what the line does.
- Catch specific exception types. A bare `catch (Exception)` needs a `when` clause or a comment
  justifying it.

## Adding things

**A tool** — implement `IAgentTool` (or derive from `ToolBase` for the shared plumbing), register it
in `ToolRegistry.CreateDefault`, and declare an accurate `ToolCapability`: that flag is what the
permission engine gates on. Getting it wrong is a security bug, not a cosmetic one.

**A provider** — if it speaks OpenAI chat completions, add a preset to `ProviderPresets`; no code.
Only a genuinely different wire format justifies a new `IChatClient`.

**A slash command** — add a case to `SlashCommandRouter.HandleAsync` and a row to `ShowHelp`.

## Documentation

`docs/en/` and `docs/id/` mirror each other. A change to one needs the same change to the other —
the Indonesian docs are a first-class deliverable, not a translation afterthought.
