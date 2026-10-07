# Marbots.Sdk

.NET client for the [Marbots](https://github.com/DotNetVibeCoderz/Vibe_Dev/tree/main/Marbots) multi-agent collaboration platform.

```csharp
using Marbots.Sdk;

using var client = new MarbotsClient(new Uri("http://localhost:5170"));
var thread = await client.Threads.CreateAsync("boss-man");
var result = await client.Threads.SendAsync(thread.Id, "Research three competitors and summarise them", wait: true);
Console.WriteLine(result.Reply?.Content);

await foreach (var e in client.Events.StreamAsync(thread.Id))
    Console.WriteLine($"{e.BotId}: {e.Type} {e.Message}");
```

Created by Gravicode Studios, led by Kang Fadhil.

## Computers, placement and learning evaluation (0.2.0)

See [docs/en/computers.md](https://github.com/DotNetVibeCoderz/Vibe_Dev/blob/main/Marbots/docs/en/computers.md) and the Skills guide.

```csharp
// Computers (agent hosts): list, one-time enrollment token, SSH bootstrap (credentials used once)
foreach (var h in await client.Hosts.ListAsync()) Console.WriteLine($"{h.Name} {h.Status} {string.Join(",", h.Capabilities)}");
Console.WriteLine((await client.Hosts.CreateEnrollmentAsync("design-pc")).EnrollCommand);

// A bot that runs on whichever computer fits, with its shell in Docker and parallel sub-agents
await client.Bots.CreateAsync(new BotDefinition
{
    Name = "Nova", HostRef = WellKnown.AutoHost, Container = new ContainerProfile { Image = "python:3.12-slim" },
    KernelFunctions = [KernelPacks.Files, KernelPacks.Shell, KernelPacks.Subagents],
});

// Learning evaluation
foreach (var e in await client.Skills.EvaluationsAsync()) Console.WriteLine($"{e.Name} {e.Version} {e.Verdict} {e.Reason}");
await client.Skills.SetAutoRollbackAsync(true);
```
