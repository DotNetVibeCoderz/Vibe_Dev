using Marbots.Abstractions;
using Marbots.Sdk;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Marbots.Tests;

/// <summary>
/// .NET SDK conformance: the real Marbots.Server (in memory, offline mock model) driven through <see cref="MarbotsClient"/>.
/// The Python, TypeScript, Go, Java and Rust SDKs run the same scenario against a real server process.
/// </summary>
public sealed class SdkConformanceTests : IClassFixture<SdkConformanceTests.ServerFixture>, IDisposable
{
    public void Dispose() => _mb.Dispose();

    public sealed class ServerFixture : WebApplicationFactory<Program>
    {
        public string DataDir { get; } = Path.Combine(Path.GetTempPath(), "mb-sdk-" + Guid.NewGuid().ToString("N")[..8]);

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Marbots:DataDirectory", DataDir);
            builder.UseSetting("Marbots:Providers:0:Name", "lab");
            builder.UseSetting("Marbots:Providers:0:Kind", "mock");
            builder.UseSetting("Marbots:Providers:0:Models:0", "lab-small");
            builder.UseSetting("Marbots:Providers:0:Models:1", "lab-large");
        }
    }

    private readonly MarbotsClient _mb;

    public SdkConformanceTests(ServerFixture fx)
    {
        var http = fx.CreateClient();
        _mb = new MarbotsClient(http.BaseAddress!, http: http);
    }

    [Fact]
    public async Task System_and_team()
    {
        var sys = await _mb.SystemAsync();
        Assert.Equal("Marbots", sys.Product);
        Assert.Contains("Gravicode", sys.CreditsEn);
        var bots = await _mb.Bots.ListAsync();
        Assert.Contains(bots, b => b.Id == WellKnown.BossManId && b.IsSystem);
    }

    [Fact]
    public async Task Hire_set_model_chat_export_import()
    {
        var bot = await _mb.Bots.HireAsync("data-analyst", "Dina");
        Assert.Equal(ModelRef.Default, bot.ModelProfile);
        Assert.Contains(KernelPacks.Shell, bot.KernelFunctions);
        Assert.Equal(PermissionProfiles.DeveloperSafe, bot.PermissionProfile);

        var info = await _mb.Bots.SetModelAsync(bot.Id, ModelRef.Of("lab", "lab-large"));
        Assert.Equal("lab/lab-large", info.Effective);
        Assert.False(info.UsesDefault);
        await Assert.ThrowsAsync<MarbotsApiException>(() => _mb.Bots.SetModelAsync(bot.Id, ModelRef.Of("ghost", "x")));

        var thread = await _mb.Threads.CreateAsync(bot.Id);
        var result = await _mb.Threads.SendAsync(thread.Id, "hello", wait: true, timeoutSeconds: 30);
        Assert.Equal(TaskState.Completed, result.Task.State);
        Assert.Equal("lab/lab-large", result.Task.Model);
        Assert.Contains("mock", result.Reply!.Content);

        var package = await _mb.Bots.ExportAsync(bot.Id);
        var imported = await _mb.Bots.ImportAsync(new MemoryStream(package));
        Assert.Equal("lab/lab-large", imported.ModelProfile);
        await _mb.Bots.DeleteAsync(imported.Id);
        await _mb.Bots.DeleteAsync(bot.Id);
    }

    [Fact]
    public async Task Models_catalog_and_default()
    {
        var catalog = await _mb.Models.ListAsync();
        Assert.Contains("lab/lab-small", catalog.Choices);
        Assert.Equal("lab/lab-small", await _mb.Models.SetDefaultAsync(ModelRef.Of("lab", "lab-small")));
        var wren = await _mb.Bots.GetModelAsync("wren");
        Assert.True(wren.UsesDefault);
        Assert.Equal("lab/lab-small", wren.Effective);
    }

    [Fact]
    public async Task Events_stream_reports_completion()
    {
        var thread = await _mb.Threads.CreateAsync("atlas");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var completed = Task.Run(async () =>
        {
            await foreach (var e in _mb.Events.StreamAsync(thread.Id, ct: cts.Token))
                if (e.Type == EventTypes.TaskStateChanged && e.Data == nameof(TaskState.Completed)) return true;
            return false;
        });
        await Task.Delay(300);
        await _mb.Threads.SendAsync(thread.Id, "ping");
        Assert.True(await completed);
        await cts.CancelAsync();
    }

    [Fact]
    public async Task Skip_approvals_toggle()
    {
        Assert.False(await _mb.Approvals.GetSkipApprovalsAsync());
        Assert.True(await _mb.Approvals.SetSkipApprovalsAsync(true));
        Assert.True(await _mb.Approvals.GetSkipApprovalsAsync());
        Assert.False(await _mb.Approvals.SetSkipApprovalsAsync(false));
    }

    [Fact]
    public async Task Templates_approvals_and_schedules()
    {
        Assert.Contains(await _mb.Templates.ListAsync("designer"), t => t.Id == "ux-designer");
        Assert.Empty(await _mb.Approvals.PendingAsync());
        var job = await _mb.Schedules.SaveAsync(new ScheduleJob { Name = "weekly", BotId = "atlas", Prompt = "brief", Cron = "0 8 * * 1" });
        Assert.NotNull(job.NextRunAt);
        await _mb.Schedules.DeleteAsync(job.Id);
        var err = await Assert.ThrowsAsync<MarbotsApiException>(() => _mb.Schedules.SaveAsync(new ScheduleJob { Name = "bad", BotId = "atlas", Prompt = "x", Cron = "nope" }));
        Assert.Equal(400, err.StatusCode);
    }
}
