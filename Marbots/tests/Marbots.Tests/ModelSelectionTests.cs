using Marbots.Abstractions;
using Marbots.Runtime;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Marbots.Tests;

/// <summary>Every bot can use its own model; bots without one (or with an unusable one) use the workspace default.</summary>
public sealed class ModelSelectionTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mb-models-" + Guid.NewGuid().ToString("N")[..8]);
    private ServiceProvider _sp = default!;
    private ModelRouter Router => _sp.GetRequiredService<ModelRouter>();
    private MarbotsEngine Engine => _sp.GetRequiredService<MarbotsEngine>();
    private BotRegistry Registry => _sp.GetRequiredService<BotRegistry>();

    public async Task InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        services.AddMarbotsRuntime(new MarbotsOptions
        {
            DataDirectory = _dir,
            Providers = [new ProviderConfig { Name = "lab", Kind = "mock", Models = ["lab-small", "lab-large"] }],
            ModelProfiles = [new ModelProfile { Name = "fast", Provider = "lab", Model = "lab-fast" }],
        });
        _sp = services.BuildServiceProvider();
        foreach (var hosted in _sp.GetServices<IHostedService>().OfType<MarbotsBootstrapper>())
            await hosted.StartAsync(default);
    }

    public async Task DisposeAsync()
    {
        Engine.Shutdown();
        await _sp.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private async Task<TaskRecord> RunAsync(string botId, string text)
    {
        var thread = await Engine.CreateThreadAsync(botId);
        return await Engine.WaitAsync((await Engine.SendAsync(thread.Id, text)).Id, TimeSpan.FromSeconds(20));
    }

    [Theory]
    [InlineData("default", "mock/mock", true)]
    [InlineData("", "mock/mock", true)]
    [InlineData(null, "mock/mock", true)]
    [InlineData("fast", "lab/lab-fast", false)]
    [InlineData("lab/lab-large", "lab/lab-large", false)]
    [InlineData("lab/any-deployment", "lab/any-deployment", false)]
    [InlineData("no-such-profile", "mock/mock", true)]
    [InlineData("ghost/gpt-x", "mock/mock", true)]
    public void Settings_resolve_to_a_model_or_the_default(string? setting, string expected, bool usesDefault)
    {
        var r = Router.Resolve(setting);
        Assert.Equal(expected, r.Label);
        Assert.Equal(usesDefault, r.Fallback);
        if (setting is "no-such-profile" or "ghost/gpt-x") Assert.NotNull(r.Warning);
    }

    [Fact]
    public void Choices_list_provider_models_for_pickers()
    {
        Assert.Contains("lab/lab-small", Router.Choices);
        Assert.Contains("lab/lab-large", Router.Choices);
        Assert.Contains("lab/lab-fast", Router.Choices);
    }

    [Fact]
    public async Task New_bots_default_to_the_workspace_model()
    {
        var bot = await Registry.CreateAsync(new BotDefinition { Name = "Plain", ModelProfile = "  " });
        Assert.Equal(ModelRouter.DefaultProfile, bot.ModelProfile);
        var fromTemplate = BotRegistry.FromTemplate((await _sp.GetRequiredService<TemplateService>().GetAsync("researcher"))!);
        Assert.Equal(ModelRouter.DefaultProfile, fromTemplate.ModelProfile);
    }

    [Fact]
    public async Task Each_bot_runs_on_its_own_model()
    {
        var atlas = (await Registry.GetAsync("atlas"))!;
        atlas.ModelProfile = "lab/lab-large";
        await Registry.UpdateAsync(atlas);
        var quinn = (await Registry.GetAsync("quinn"))!;
        quinn.ModelProfile = "fast";
        await Registry.UpdateAsync(quinn);

        var a = await RunAsync("atlas", "hello atlas");
        var q = await RunAsync("quinn", "hello quinn");
        var w = await RunAsync("wren", "hello wren");

        Assert.Equal("lab/lab-large", a.Model);
        Assert.Equal("lab/lab-fast", q.Model);
        Assert.Equal("mock/mock", w.Model); // wren keeps "default"
        Assert.Contains(Router.Mock.Requests, r => r.Model == "lab-large" && r.Messages.Any(m => m.Content == "hello atlas"));
        Assert.Contains(Router.Mock.Requests, r => r.Model == "lab-fast" && r.Messages.Any(m => m.Content == "hello quinn"));
        Assert.Contains(Router.Mock.Requests, r => r.Model == "mock" && r.Messages.Any(m => m.Content == "hello wren"));
    }

    [Fact]
    public async Task Bots_with_an_unusable_model_fall_back_to_the_default()
    {
        var atlas = (await Registry.GetAsync("atlas"))!;
        atlas.ModelProfile = "ghost/gpt-x";
        await Registry.UpdateAsync(atlas);
        var t = await RunAsync("atlas", "still works?");
        Assert.Equal(TaskState.Completed, t.State);
        Assert.Equal("mock/mock", t.Model);
    }

    [Fact]
    public async Task Changing_the_default_moves_only_default_bots()
    {
        var atlas = (await Registry.GetAsync("atlas"))!;
        atlas.ModelProfile = "lab/lab-large";
        await Registry.UpdateAsync(atlas);

        await Router.SetDefaultAsync("lab/lab-small");
        Assert.Equal("lab/lab-small", ModelRouter.Describe(Router.Default));

        Assert.Equal("lab/lab-small", (await RunAsync("wren", "x")).Model);
        Assert.Equal("lab/lab-large", (await RunAsync("atlas", "y")).Model);
        await Assert.ThrowsAsync<ArgumentException>(() => Router.SetDefaultAsync("ghost/gpt-x"));
    }

    [Fact]
    public async Task Boss_man_can_hire_a_bot_with_a_specific_model()
    {
        Router.Mock.EnqueueTool("create_bot", """{"name":"Nova","template_id":"copywriter","model":"lab/lab-large"}""");
        Router.Mock.Enqueue(req => new ModelResponse { Content = req.Messages[^1].Content });
        var t = await RunAsync(WellKnown.BossManId, "hire a copywriter on the large model");
        Assert.Contains("lab/lab-large", t.Result);
        Assert.Equal("lab/lab-large", (await Registry.ResolveAsync("Nova"))!.ModelProfile);
    }
}
