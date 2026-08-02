# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

The full guidance lives in **[AUTOCODE.md](AUTOCODE.md)** — this project's own context file, which
Auto Code and Claude Code both read. Everything below is a summary; read `AUTOCODE.md` for the
reasoning behind each constraint.

## What this repository is

Auto Code: an agentic coding CLI for the terminal, built on .NET 10 and the official .NET AI
libraries. Built by **Gravicode Studios**, led by **Kang Fadhil** — keep that attribution in docs and
in source file headers.

## Commands

```bash
dotnet build
dotnet test                                                      # 97 tests
dotnet test --filter FullyQualifiedName~PermissionEngineTests    # one class
dotnet run --project src/AutoCode.Cli -- doctor
```

Package versions live in `Directory.Packages.props`. Never put `Version=` on a `PackageReference`.

## Architecture in one page

`Cli → {Core, Providers, Tools, Mcp} → Core`, one direction only. `Core` must not reference
Spectre.Console or any vendor SDK.

Two invariants that are load-bearing:

1. **Tool execution goes through `ToolExecutor`, never around it.** It implements the three gates —
   `PreToolUse` hook, permission engine, renderer — once. `AgentLoop` and `SubagentDispatcher` both
   call it. Duplicating that logic is how a subagent skips an approval prompt.
2. **Deny rules beat everything**, including `bypassPermissions`. If you touch
   `PermissionEngine.Evaluate`, preserve the order `deny → mode → allow → ask`.

`AutoCode.Providers` is the only project that names a vendor. `AnthropicChatClient` and
`GeminiChatClient` are hand-written against their wire formats for reasons documented in
`AUTOCODE.md` — read that before changing either, and run `ProviderIntegrationTests` after.

## Conventions

- File-scoped namespaces, nullable enabled, `// Auto Code — Gravicode Studios (Kang Fadhil)` header.
- XML docs on public types; where a decision is non-obvious, say **why**, in English and Indonesian.
- Comment reasoning, never mechanics.
- `docs/en/` and `docs/id/` mirror each other. A change to one requires the same change to the other.
