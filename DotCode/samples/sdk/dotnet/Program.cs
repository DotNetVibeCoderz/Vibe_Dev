// DotCode .NET SDK sample: a typed custom tool, a permission handler and streaming events.
// Run: dotnet run --project samples/sdk/dotnet -- "azure:gpt-5-mini"
using System.ComponentModel;
using System.Text.Json.Serialization;
using DotCode.Abstractions;
using DotCode.Sdk;

var model = args.Length > 0 ? args[0] : null;
var mode = Environment.GetEnvironmentVariable("DOTCODE_SDK_MODE") == "spawn" ? ClientMode.Spawn : ClientMode.InProcess;

// Tool parameters are a C# type: the JSON schema is generated from it and a typo does not compile.
var getExchangeRate = DotCodeTool.DefineTool("get_exchange_rate", "Get the exchange rate between two currencies",
    args => $"1 {args.From} = {(args.To == "IDR" ? "16,250" : "0.92")} {args.To} (demo data)",
    SampleJson.Default.ExchangeRateArgs).AsReadOnly();

await using var client = new DotCodeClient(new DotCodeClientOptions { Mode = mode });
await client.StartAsync();
await using var session = await client.CreateSessionAsync(new SessionConfig
{
    Model = model,
    PersistSession = false,
    Tools = [getExchangeRate],
    OnPermissionRequest = (request, _, _) =>
    {
        Console.WriteLine($"  [permission] {request.DisplayName} → allowed");
        return Task.FromResult(PermissionDecision.ApproveOnce());
    },
});

using var text = session.On<AssistantTextDeltaEvent>(e => Console.Write(e.Text));
using var started = session.On<ToolStartedEvent>(e => Console.WriteLine($"● {e.DisplayName}"));
using var completed = session.On<ToolCompletedEvent>(e => Console.WriteLine($"  ⎿  {e.Output}"));

Console.WriteLine($"DotCode SDK ({mode}) · model {session.Model}\n");
var result = await session.SendAndWaitAsync("How many Indonesian Rupiah is 250 US dollars? Use the tool, then answer in one sentence.");
Console.WriteLine($"\n\n✔ {result.ModelCalls} model calls · ${result.CostUsd:0.0000} · {result.DurationMs} ms");

public sealed record ExchangeRateArgs(
    [property: Description("ISO currency code, e.g. USD")] string From,
    [property: Description("ISO currency code, e.g. IDR")] string To);

[JsonSerializable(typeof(ExchangeRateArgs))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class SampleJson : JsonSerializerContext;
