# Development

> 🇮🇩 [Bahasa Indonesia](../id/pengembangan.md)

## Prerequisites

.NET 10 SDK (see `global.json`), Git. For NativeAOT on Windows: Visual Studio 2022+ "Desktop development with C++" (the publish step uses `vswhere.exe`; add `C:\Program Files (x86)\Microsoft Visual Studio\Installer` to `PATH` if it is not found). Node 18+ (TypeScript SDK), Python 3.9+ (Python SDK), Go 1.22+ (Go SDK).

## Build and run

```bash
dotnet build DotCode.slnx
dotnet run --project src/DotCode.Cli -- --model mock:echo      # offline, no API key
dotnet src/DotCode.Cli/bin/Debug/net10.0/dotcode.dll -p "hi" --model mock:echo
```

The `mock` provider is always available: `mock:echo` echoes; `{"type":"mock","script":"file.json"}` plays scripted responses (see `ScriptedProvider`).

## Tests

```bash
dotnet test tests/DotCode.Tests                                        # all
dotnet test tests/DotCode.Tests --filter "FullyQualifiedName~ProviderContractTests"
dotnet test tests/DotCode.Tests --filter "FullyQualifiedName~AgentLoopTests.Runs_tools_until_the_model_stops"
cd sdk/typescript && npm install && npm run build && npm test
cd sdk/python/tests && PYTHONPATH=../src python -m unittest -v
cd sdk/go && go test ./...
```

| Suite | What it covers |
|---|---|
| `ProviderContractTests` | Every adapter against recorded-style SSE/NDJSON streams via a mock `HttpMessageHandler`: parts, stop reasons, usage, request shape (cache control, schema sanitizing, auth headers), error mapping, text tool protocol |
| `PermissionTests`, `UtilTests` | Rule parsing, shell analysis, path globs, diff, JSON repair, frontmatter, settings merge, schema profiles, catalog |
| `AgentLoopTests` | The loop with the scripted provider: tools, permissions, plan mode, edits with CRLF, subagents, todos, commands/skills, rewind |
| `TuiTests` | Display width, wrapping, markdown, themes × glyph sets, diffs |
| `SdkConformanceTests` + `sdk/*` tests | The same scenario (host tool + streaming, permission handler) in-process and over JSON-RPC, in every language |

SDK tests find the CLI at `src/DotCode.Cli/bin/Debug/net10.0/dotcode.dll` or `DOTCODE_CLI_PATH`; build the solution first.

## Publishing

```bash
dotnet publish src/DotCode.Cli -c Release -r win-x64   -o artifacts/win-x64     # NativeAOT single file
dotnet publish src/DotCode.Cli -c Release -r linux-x64 -o artifacts/linux-x64   # run on Linux (AOT doesn't cross-OS compile)
dotnet pack src/DotCode.Cli -c Release -p:DotCodeTool=true -o artifacts/nuget   # "dotnet tool" package
dotnet pack src/DotCode.Sdk -c Release -o artifacts/nuget
cd sdk/typescript && npm pack
cd sdk/python && python -m build
```

## Screenshots

`tools/DotCode.TermCapture` runs scripted sessions inside a Windows pseudo console, emulates the terminal and renders HTML/PNG (headless Edge/Chrome):

```bash
export DEMO_ROOT=… DEMO_CONFIG=… DOTCODE_EXE=$PWD/artifacts/win-x64/dotcode.exe   # plus provider keys
dotnet run --project tools/DotCode.TermCapture -- tools/DotCode.TermCapture/scenarios/07-ui-tour.json
```

Steps: `type`, `paste`, `key` (`enter`, `esc`, `shift+tab`, `up`, `ctrl+c` …), `sleep`, `waitFor`/`waitGone` (regex, with optional `respond` auto-answers), `waitIdle`, `snapshot` (`full: true` includes scrollback).

## Conventions

- Keep core projects AOT-clean: no reflection-based JSON; add types to a `JsonSerializerContext` or write with `Utf8JsonWriter`.
- New tools derive from `Tool`, declare read-only/concurrency/permission metadata and register in `BuiltinToolset`.
- New providers implement `IModelProvider`, emit `ContentBlockCompleted` for every finished block and exactly one `MessageStopped`, and get a contract test.
- User-visible behavior should match Claude Code where a concept exists in both.
