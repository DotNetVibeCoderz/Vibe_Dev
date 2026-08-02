---
name: add-tool
description: Add a new built-in tool to Auto Code — use when asked to give the agent a new capability that belongs in-process rather than in an MCP server
allowed-tools: [Read, Write, Edit, Glob, Grep, Bash]
---

Add a built-in tool. Work through this in order.

## 1. Decide it belongs here

A built-in tool is the right answer only when the capability needs the workspace, the file tracker,
or the subagent dispatcher. Anything that talks to an external service should be an MCP server
instead — it works in every MCP client and does not have to be C#. Say so and stop if that is the
case here.

## 2. Read a comparable tool first

Read `src/AutoCode.Tools/FileTools.cs` for a tool that touches the filesystem, or
`src/AutoCode.Tools/WorkflowTools.cs` for one that does not. Match what you find: the schema style,
the description voice, the failure handling.

## 3. Write it

Derive from `ToolBase`, which gives you schema caching and uniform failure handling. Supply:

- `Name` — the name the model calls.
- `Description` — this is the tool's entire user manual as far as the model is concerned. Write it
  as instructions, including when *not* to use it.
- `SchemaJson` — JSON Schema with a `description` on every property.
- `Capability` — **the security-critical field.** Declare exactly what the tool can do:
  `ReadsFiles`, `WritesFiles`, `ExecutesCommands`, `AccessesNetwork`. The permission engine gates on
  this, so an understated flag is a security bug, not a cosmetic one.
- `Summarize` — the one-line form shown in the terminal and in the approval prompt.
- `ExecuteAsync` — the work. Return `ToolResult.Fail` with an actionable message rather than
  throwing; the model reads it and adapts.

If the tool touches files, resolve paths with `WorkspacePath.Resolve` and record reads and writes on
`invocation.Services.Files`.

## 4. Register it

Add it to `ToolRegistry.CreateDefault`. If it depends on a feature that can be switched off, gate the
registration on that setting the way `CodeSearchTool` and `VerifyTool` are gated.

## 5. Test it

Add tests to `tests/AutoCode.Tests/ToolTests.cs` using `TempWorkspace` and `ToolTestExtensions.CallAsync`.
Cover the success path, the invalid-argument path, and every refusal the tool can produce.

If the tool mutates anything, add a `PermissionEngineTests` case proving it prompts in `ask` mode and
is refused in `plan` mode.

## 6. Document it

Add a row to the tool table in **both** `docs/en/tools.md` and `docs/id/tools.md`. The Indonesian
docs are a deliverable, not an afterthought.

## 7. Verify

Run `dotnet build` and `dotnet test`. Report the actual result — if anything fails, show the output
rather than describing it.
