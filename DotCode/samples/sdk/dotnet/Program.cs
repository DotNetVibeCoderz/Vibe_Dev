// DotCode .NET SDK sample: a custom tool, a permission handler and streaming events.
// Run: dotnet run --project samples/sdk/dotnet -- "azure:gpt-5-mini"
using DotCode.Abstractions;
using DotCode.Sdk;

var model = args.Length > 0 ? args[0] : null;
var mode = Environment.GetEnvironmentVariable("DOTCODE_SDK_MODE") == "spawn" ? ClientMode.Spawn : ClientMode.InProcess;

await using var client = new DotCodeClient(new DotCodeClientOptions { Mode = mode });
await using var session = await client.CreateSessionAsync(new SessionOptions
{
    Model = model,
    PersistSession = false,
    Tools =
    [
        DotCodeTool.Create("get_exchange_rate", "Get the exchange rate between two currencies",
            """{"type":"object","properties":{"from":{"type":"string"},"to":{"type":"string"}},"required":["from","to"]}""",
            input => $"1 {input.GetString("from")} = {(input.GetString("to") == "IDR" ? "16,250" : "0.92")} {input.GetString("to")} (demo data)",
            readOnly: true),
    ],
    OnPermissionRequest = (request, _) =>
    {
        Console.WriteLine($"  [permission] {request.DisplayName} → allowed");
        return Task.FromResult(PermissionDecision.AllowOnce);
    },
});

Console.WriteLine($"DotCode SDK ({mode}) · model {session.Model}\n");
await foreach (var e in session.StreamAsync("How many Indonesian Rupiah is 250 US dollars? Use the tool, then answer in one sentence."))
{
    switch (e)
    {
        case AssistantTextDeltaEvent d: Console.Write(d.Text); break;
        case ToolStartedEvent t: Console.WriteLine($"● {t.DisplayName}"); break;
        case ToolCompletedEvent c: Console.WriteLine($"  ⎿  {c.Output}"); break;
        case TurnCompletedEvent done: Console.WriteLine($"\n\n✔ {done.NumModelCalls} model calls · ${done.CostUsd:0.0000} · {done.DurationMs} ms"); break;
    }
}
