# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

DotCode: a clean-room .NET 10 / C# 14 re-implementation of the Claude Code experience (terminal agent) that works with any LLM, plus a harness SDK (.NET, TypeScript, Python, Go, Java) over JSON-RPC. Built by Gravicode Studios (led by Kang Fadhil) — keep that credit in UI/docs. Never copy Claude Code source; behavior is derived from public docs. `solution-design.md` (Indonesian) is the design; `PLAN.md`/`Progress.md` track roadmap/status (update Progress.md when finishing features). Docs are bilingual: every page in `docs/en/` has a counterpart in `docs/id/` — keep them in sync.

## Commands

```bash
dotnet build DotCode.slnx
dotnet test tests/DotCode.Tests                                            # xUnit (69 tests)
dotnet test tests/DotCode.Tests --filter "FullyQualifiedName~AgentLoopTests.Runs_tools_until_the_model_stops"
dotnet run --project src/DotCode.Cli -- --model mock:echo                  # offline, no keys
dotnet src/DotCode.Cli/bin/Debug/net10.0/dotcode.dll -p "hi" --model mock:echo
# NativeAOT (Windows needs vswhere on PATH):
PATH="$PATH:/c/Program Files (x86)/Microsoft Visual Studio/Installer" dotnet publish src/DotCode.Cli -c Release -r win-x64 -o artifacts/win-x64
cd sdk/typescript && npm run build && npm test     # SDK tests spawn src/DotCode.Cli/bin/Debug/net10.0/dotcode.dll
cd sdk/python/tests && PYTHONPATH=../src python -m unittest -v
cd sdk/go && go test ./...
# Java (JDK 17+, no deps): javac -d target/classes $(find src/main -name "*.java") … then run com.gravicode.dotcode.ConformanceTest (see .github/workflows/dotcode.yml)
```

Release: tag `dotcode-v*` → CI builds AOT binaries + pushes NuGet; `DotCode/sdk/go/v*` tags publish the Go module; `dotcode-java-v*` tags are built by JitPack (`../jitpack.yml`); Maven Central via `sdk/java/publish-central.sh` once the namespace is verified. npm/PyPI are published manually (`npm publish`, `twine upload`).

Use `DOTCODE_CONFIG_DIR=<tmp>` when running the CLI in tests/demos so `~/.dotcode` (the user's real config) isn't touched. In this Bash environment `dotcode -p` should get `< /dev/null`.

## Architecture (big picture)

- **One engine, many surfaces**: TUI (`DotCode.Tui`) and headless (`DotCode.Cli/HeadlessRunner`) run `DotCode.Engine` in-process; SDKs go through `dotcode serve` (`DotCode.Protocol/AgentServer`). All surfaces consume the same `AgentEvent` stream (`DotCode.Abstractions/AgentEvents.cs`) — a feature must not exist in only one.
- **Engine core**: `AgentRuntime` (per-cwd services: settings, `ModelRouter`, `ExtensionRegistry`, MCP, hooks, memory, built-in tools) → `AgentSession.RunTurnAsync` (the loop: hooks → `PromptBuilder` → provider stream with retry/fallback → `ToolExecutor` → repeat). `Compactor` handles context; `SessionStore` (JSONL) + `CheckpointManager` handle resume/rewind. Subagents are child `AgentSession`s sharing permissions/checkpoints/sink.
- **Tools** derive from `Engine/Tools/Tool.cs`, declare `IsReadOnly`/`IsConcurrencySafe`/`GetPermissionTarget`, register in `Tools/Builtin/BuiltinToolset.cs`. Consecutive concurrency-safe calls run in parallel.
- **Permissions**: `PermissionEngine.Evaluate` order deny → plan restriction → ask → bypass → allow → defaults; Claude Code rule syntax; shell commands analyzed by `ShellCommand`.
- **Providers** (`DotCode.Providers`): one adapter per wire protocol, hand-written with `Utf8JsonWriter`/SSE. Contract: emit `ContentBlockCompleted` for every finished block and exactly one `MessageStopped`; throw `ModelProviderException` (code/retryable). Provider-specific round-trip data lives in `ProviderOpaque` and is only replayed to the same provider id. OpenAI-compatible variation is data (`OpenAIQuirks` profiles), not code. `mock` provider = `ScriptedProvider` (JSON script) for tests.
- **TUI**: `Rendering/Screen.cs` is Ink-like (committed lines go to scrollback; live region redrawn in sync frames). `TuiApp.cs` is a single UI loop over a channel of key/agent/tick/modal events; agent turns run on the thread pool and interact only via that channel (`IInteractionHandler` posts modals).
- **Settings** deep-merge user → project (`.claude` compat, `.dotcode`) → local → CLI → managed (`Configuration/SettingsLoader.cs`).

## Constraints

- **NativeAOT**: IL2026/IL3050 etc. are errors in `src/*`. No reflection JSON — add types to a `JsonSerializerContext` or write with `Utf8JsonWriter`/`JsonDocument`. `JsonArray.Add(x)` must be `Add((JsonNode?)JsonValue.Create(x))`.
- Tests use the scripted provider — no network. New providers need a test in `ProviderContractTests` (mock `HttpMessageHandler` via `ProviderHttp.OverrideHandler`).
- Interpolated raw strings containing JSON need `$$$"""` with `{{{expr}}}`.
- Screenshots: `tools/DotCode.TermCapture` scenarios (`tools/DotCode.TermCapture/scenarios/*.json`) drive the real TUI in ConPTY; output goes to `docs/images`.
