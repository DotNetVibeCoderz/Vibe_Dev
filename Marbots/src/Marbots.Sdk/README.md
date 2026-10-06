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
