# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

```bash
dotnet build Marbots.slnx                                   # whole solution (.NET 10)
dotnet test tests/Marbots.Tests                             # ~140 tests, no network (mock LLM)
dotnet test tests/Marbots.Tests --filter "FullyQualifiedName~EngineTests.Boss_man_and_starter_team_are_seeded"
dotnet run --project src/Marbots.Server                     # UI + API on http://localhost:5170
dotnet run --project src/Marbots.Cli -- status              # CLI (MARBOTS_URL, MARBOTS_API_KEY)
python tools/gen_templates.py src/Marbots.Runtime/builtin-templates.json   # regenerate template catalogue (embedded resource)
cd sdk/typescript && npm install && npx tsc -p .             # TS SDK build
cd sdk/go && go vet ./...                                   # Go SDK
python samples/trials/run_trials.py                         # real-LLM trials against a running server
node tools/screenshots/shoot.mjs http://localhost:5170 docs/images   # README/docs screenshots (npm install in tools/screenshots first)
dotnet run --project src/Marbots.Desktop                    # Avalonia desktop app with the 3D office
dotnet build src/Marbots.Mobile -f net10.0-android          # MAUI mobile app (not in Marbots.slnx)
dotnet publish src/Marbots.AgentHost -c Release -r win-x64 -o src/Marbots.Server/data/host-packages   # then rename to marbots-host-win-x64.exe for SSH bootstrap
```

A running `Marbots.Server.exe` locks its binaries; stop it before rebuilding the server. Data lives in
`src/Marbots.Server/data` (SQLite, workspaces, skills, encrypted secrets); deleting it resets everything.

## Architecture

Modular monolith (see `docs/en/architecture.md`, full target design in `solution-design.md`):

- `Marbots.Abstractions` – all contracts + `MarbotsJsonContext` (source-generated JSON). New persisted/transported types must be registered there.
- `Marbots.Storage` – SQLite: one generic `documents(kind,id,json)` table behind `IDocumentStore<T>` (kinds registered in `ServiceCollectionExtensions.AddDocs`), plus `messages`, `events`, `memories` + FTS5.
- `Marbots.Runtime` – the core. `MarbotsEngine` (threads, root tasks, delegation DAG, cancel, recovery) → `AgentRuntime` (agent loop: tools via `ToolAssembler`, context via `ContextManager`, policy via `PolicyEngine` + `ApprovalService`) → `IModelRouter`. Boss Man tools (`delegate_tasks`, `create_bot`, …) in `AgentTools.cs` resolve the engine lazily from `FunctionExecutionContext.Services` to avoid DI cycles.
- Tools are `IKernelFunction`s grouped by `Descriptor.Pack`; a bot enables packs via `KernelFunctions`. MCP tools are wrapped as `McpToolFunction`, skills via `load_skill`. Every call passes the policy engine (category → Allow/Ask/Deny per permission profile).
- Root tasks write to the chat thread; delegated tasks write to `transcript_<taskId>` but share the root thread's workspace (`data/workspaces/<thread-slug>`) and emit events with the root `ThreadId`.
- Everything observable is an `AgentEvent` through `IEventBus` (persisted + fanned out). Blazor pages derive from `LiveComponent` (throttled refresh); SSE endpoints and the CLI read the same stream.
- `Marbots.Server` – minimal APIs in `Api/ApiEndpoints.cs`, A2A in `Api/A2aEndpoints.cs`, Blazor Server UI in `Components/`. UI strings EN/ID live in `Services/UiServices.cs` (`UiText`).

## Conventions & requirements

- Bilingual: every doc page exists in `docs/en` and `docs/id` with the same structure; README.md + README.id.md.
- Credits must appear in docs and app: "Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil" (`WellKnown.Credits`).
- Keep `PLAN.md` (roadmap) and `Progress.md` (status/log) updated with notable changes.
- UI design system: "glass marbles" — tokens in `wwwroot/app.css` (`:root` + dark mode), Bricolage Grotesque + Plus Jakarta Sans, the `Marble` component is the signature element. Follow the frontend-design skill; avoid generic card-kit styling.
- Async + `CancellationToken` everywhere, bounded channels, no reflection JSON on hot paths. Rust only if profiling justifies it (ADR-008).
- Real-LLM test key: `C:\Users\mifma\Documents\CodeSandbox\testkey.txt`; publishing credentials: `C:\Users\mifma\Documents\CodeSandbox\PackageCredentials.txt`. Load at runtime only; never copy values into the repo, docs or logs. Publishing packages is outward-facing — confirm with the user first.

## Distributed hosts (Phase 2)

- `Marbots.AgentHost` (`marbots-host`) executes the `RemotePacks` (files, search, shell, desktop) for bots whose `HostRef` is a registered host; the agent loop, policy, approvals, memory and web tools stay on the server (ADR-010).
- Protocol: `HostFrame` JSON over one WebSocket (`/api/v1/hosts/connect`), see `Abstractions/Hosts.cs`, `Runtime/Hosts.cs` (`HostConnectionManager`, `RemoteFunction`, `PlacementService`), `Runtime/HostBootstrapper.cs` (SSH.NET).
- `ToolAssembler.ForBotAsync` swaps remote-pack tools for `RemoteFunction` and `load_skill` for `RemoteSkillLoader`.
- Remote workspace folder = `Ids.Slug(threadId)` (e.g. `thr-…`) under `%LOCALAPPDATA%\Marbots\Host\workspaces`.
