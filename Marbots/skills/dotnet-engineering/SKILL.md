---
name: dotnet-engineering
description: .NET 10 / C# engineering conventions: project layout, async, DI, testing and performance.
version: 1.0.0
requires:
  tools: [read_file, write_file, run_shell]
permissions:
  network: false
  shell: true
---
# .NET engineering

## Setup
- Check SDK: `dotnet --version` (target `net10.0`).
- New solution: `dotnet new sln -n App --format slnx`, projects with `dotnet new classlib|console|webapi|xunit -o src/App.X`.
- Add projects: `dotnet sln add src/**/*.csproj`.

## Conventions
- `<Nullable>enable</Nullable>`, `<ImplicitUsings>enable</ImplicitUsings>`, file-scoped namespaces, primary constructors for DI.
- Async all the way: `async Task`/`ValueTask`, pass `CancellationToken`, never `.Result`/`.Wait()`.
- JSON: `System.Text.Json` with source generation (`JsonSerializerContext`) for hot paths.
- Collections: return `IReadOnlyList<T>`; avoid LINQ in tight loops; use `ArrayPool<T>`/`Span<T>` only on measured hot paths.
- Errors: validate at boundaries, throw specific exceptions, use `ProblemDetails` in Web APIs.
- Minimal APIs: group endpoints with `MapGroup("/api/v1/...")`.

## Verify
- `dotnet build -warnaserror` (or note warnings), `dotnet test`, `dotnet run --project src/App.Api`.
- For APIs, call endpoints with `Invoke-RestMethod`/`curl` and show the response.

## Deliver
Report project layout, commands to run, test results.
