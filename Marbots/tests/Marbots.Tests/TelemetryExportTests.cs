using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text;
using Marbots.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Marbots.Tests;

/// <summary>Spans, metrics and logs reach an OTLP/HTTP collector (a fake one that records what it receives).</summary>
public sealed class TelemetryExportTests : IAsyncLifetime
{
    private WebApplication _collector = default!;
    private readonly ConcurrentDictionary<string, ConcurrentBag<byte[]>> _received = new();
    private string _endpoint = "";

    public async Task InitializeAsync()
    {
        var b = WebApplication.CreateSlimBuilder();
        b.WebHost.UseUrls("http://127.0.0.1:0");
        _collector = b.Build();
        _collector.MapPost("/v1/{signal}", async (string signal, HttpRequest req) =>
        {
            using var ms = new MemoryStream();
            await req.Body.CopyToAsync(ms);
            _received.GetOrAdd(signal, _ => []).Add(ms.ToArray());
            return Results.Ok();
        });
        await _collector.StartAsync();
        _endpoint = _collector.Urls.First();
    }

    public async Task DisposeAsync() => await _collector.DisposeAsync();

    private sealed class Server(string endpoint) : WebApplicationFactory<Program>
    {
        public string DataDir { get; } = Path.Combine(Path.GetTempPath(), "mb-otel-" + Guid.NewGuid().ToString("N")[..8]);

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Marbots:DataDirectory", DataDir);
            builder.UseSetting("Marbots:Telemetry:OtlpEndpoint", endpoint);
            builder.UseSetting("Marbots:Telemetry:Protocol", "http/protobuf");
            builder.UseSetting("OTEL_BSP_SCHEDULE_DELAY", "200");
            builder.UseSetting("OTEL_METRIC_EXPORT_INTERVAL", "500");
            builder.UseSetting("OTEL_BLRP_SCHEDULE_DELAY", "200");
        }
    }

    private bool Has(string signal, string text) =>
        _received.TryGetValue(signal, out var bodies) && bodies.Any(b => Encoding.UTF8.GetString(b).Contains(text, StringComparison.Ordinal));

    [Fact]
    public async Task Chat_is_exported_as_otlp_traces_and_metrics()
    {
        await using var server = new Server(_endpoint);
        var http = server.CreateClient();
        var thread = await (await http.PostAsJsonAsync("/api/v1/threads", new { botId = "alice" })).Content.ReadFromJsonAsync<ChatThread>();
        var sent = await http.PostAsJsonAsync($"/api/v1/threads/{thread!.Id}/messages", new { text = "hello", wait = true });
        sent.EnsureSuccessStatusCode();

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline && !(Has("traces", "invoke_agent") && Has("metrics", "gen_ai.client.token.usage") && Has("traces", "marbots.tenant")))
            await Task.Delay(200);
        Assert.True(Has("traces", "invoke_agent"), "no invoke_agent span exported");
        Assert.True(Has("traces", "marbots.tenant"));
        Assert.True(Has("traces", "service.name") || Has("traces", "marbots"), "resource attributes missing");
        Assert.True(Has("metrics", "gen_ai.client.token.usage"), "no token usage metric exported");
        Assert.True(Has("metrics", "marbots.tasks"));
    }
}
