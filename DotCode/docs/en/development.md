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
cd sdk/python && python tests/typecheck/run.py   # needs mypy (+ pydantic)
cd sdk/go && go test ./...
cd sdk/rust && cargo test
```

| Suite | What it covers |
|---|---|
| `ProviderContractTests` | Every adapter against recorded-style SSE/NDJSON streams via a mock `HttpMessageHandler`: parts, stop reasons, usage, request shape (cache control, schema sanitizing, auth headers), error mapping, text tool protocol |
| `PermissionTests`, `UtilTests` | Rule parsing, shell analysis, path globs, diff, JSON repair, frontmatter, settings merge, schema profiles, catalog |
| `AgentLoopTests` | The loop with the scripted provider: tools, permissions, plan mode, edits with CRLF, subagents, todos, commands/skills, rewind |
| `TuiTests` | Display width, wrapping, markdown, themes × glyph sets, diffs |
| `SdkConformanceTests` + `sdk/*` tests | The same scenarios (typed host tool + streamed events, permission handler with `send` + `turn.completed`, invalid tool arguments reported to the model) in-process and over JSON-RPC, in every language; compile-time typo checks (`sdk/typescript/test/typecheck.ts`, `sdk/python/tests/typecheck/run.py` with mypy) |

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

## Releases

Releases are cut by pushing a tag `dotcode-vX.Y.Z` (the repository hosts other projects, so every DotCode tag has this prefix). The version comes from the tag (`-p:Version`), so bump `<Version>` in `Directory.Build.props` to match for local and NuGet builds. The `DotCode` workflow then:

1. runs all tests (plus the sandbox, LSP and installer tests);
2. builds NativeAOT binaries on native runners for `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64` (Ubuntu 22.04 → glibc 2.35+), `osx-arm64` and `osx-x64` (cross-compiled, smoke-tested under Rosetta), and smoke-tests each (`--version`, a mock-model prompt);
3. packages them as `dotcode-<rid>.zip` (Windows) / `.tar.gz` (Unix, keeps the executable bit), writes `SHA256SUMS`, an SPDX SBOM and GitHub build-provenance attestations;
4. packs and pushes the NuGet packages, then creates the GitHub release with the archives, checksums, SBOM, installers, `.nupkg`s and the VS Code `.vsix`;
5. publishes the VS Code extension (stamped with the release version) to the Marketplace as `GravicodeStudios.dotcode-vscode`, using the repository secret `VSCE_PAT`. A manual run with **publish_vscode** publishes the version in `ide/vscode/package.json`.

To try the whole pipeline without publishing anything, run the workflow manually with **build_binaries** (`gh workflow run dotcode.yml -f build_binaries=true`); the results are the `release-assets` artifact. SDK packages are released separately (npm, PyPI, crates.io by hand; Go and Java by their own tags, see CLAUDE.md). After a release, update `packaging/scoop/dotcode.json` (version, URL, hash from `SHA256SUMS`).

The installers (`install/install.sh`, `install/install.ps1`) and `dotcode update` read releases from the GitHub API. `DOTCODE_RELEASES_API` / `DOTCODE_DOWNLOAD_BASE` point them at a mirror or a test server. `install/test-install.sh` runs `install.sh` against a local fake release: it checks the verified install and that a tampered archive is refused.

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
