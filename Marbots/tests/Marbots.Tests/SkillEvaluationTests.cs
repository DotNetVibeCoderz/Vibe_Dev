using Marbots.Abstractions;
using Marbots.Providers;
using Marbots.Runtime;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Marbots.Tests;

public sealed class SkillEvaluationTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mb-skilleval-" + Guid.NewGuid().ToString("N")[..8]);
    private ServiceProvider _sp = default!;
    private SkillRegistry Registry => _sp.GetRequiredService<SkillRegistry>();
    private SkillEvaluator Evaluator => _sp.GetRequiredService<SkillEvaluator>();

    public async Task InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        services.AddMarbotsRuntime(new MarbotsOptions { DataDirectory = _dir, ApprovalTimeoutMinutes = 1 });
        _sp = services.BuildServiceProvider();
        foreach (var hosted in _sp.GetServices<IHostedService>().OfType<MarbotsBootstrapper>()) await hosted.StartAsync(default);
    }

    public async Task DisposeAsync()
    {
        _sp.GetRequiredService<MarbotsEngine>().Shutdown();
        await _sp.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static SkillStats Stats(int ok, int failed, string version = "1.0.0") =>
        new() { Id = SkillStats.Key("s", version), Name = "s", Version = version, Successes = ok, Failures = failed };

    [Theory]
    [InlineData(1, 0, false, false, SkillVerdict.CollectingEvidence)]
    [InlineData(5, 0, false, false, SkillVerdict.Healthy)]
    [InlineData(2, 2, false, false, SkillVerdict.Underperforming)]
    [InlineData(1, 4, false, false, SkillVerdict.Underperforming)]
    [InlineData(1, 4, false, true, SkillVerdict.RollbackRecommended)]
    [InlineData(4, 0, true, false, SkillVerdict.ReadyToPromote)]
    [InlineData(0, 3, true, false, SkillVerdict.DiscardRecommended)]
    public void Verdict_rules(int ok, int failed, bool pending, bool hasPrevious, SkillVerdict expected) =>
        Assert.Equal(expected, SkillEvaluator.Judge(Stats(ok, failed), null, pending, hasPrevious).Item1);

    [Fact]
    public void Rollback_is_not_recommended_when_the_previous_version_did_worse()
    {
        var (verdict, _) = SkillEvaluator.Judge(Stats(1, 3, "1.1.0"), Stats(0, 5), pending: false, hasPrevious: true);
        Assert.Equal(SkillVerdict.Underperforming, verdict);
    }

    [Fact]
    public async Task Outcomes_are_counted_once_per_task_and_cancelled_tasks_do_not_count()
    {
        await Registry.CreateAsync("report-style", "How to write reports", "Use short headings and a summary first.", pending: false, default);
        async Task Run(string task, string outcome)
        {
            await Evaluator.ObserveAsync(new AgentEvent { Type = EventTypes.SkillLoaded, TaskId = task, BotId = "atlas", Message = "report-style" });
            await Evaluator.ObserveAsync(new AgentEvent { Type = EventTypes.SkillLoaded, TaskId = task, BotId = "atlas", Message = "report-style" });
            await Evaluator.ObserveAsync(new AgentEvent { Type = EventTypes.TaskStateChanged, TaskId = task, Data = outcome });
        }
        await Run("t1", "Completed");
        await Run("t2", "Completed");
        await Run("t3", "Failed");
        await Run("t4", "Cancelled");
        var eval = (await Evaluator.EvaluateAsync("report-style"))!;
        Assert.Equal((4L, 2L, 1L), (eval.Current.Loads, eval.Current.Successes, eval.Current.Failures));
        Assert.Equal(SkillVerdict.Underperforming, eval.Verdict);
    }

    [Fact]
    public async Task Publishing_a_new_version_keeps_the_old_one_and_rollback_restores_it()
    {
        await Registry.CreateAsync("triage", "Ticket triage", "Version one instructions.", pending: false, default);
        await Registry.CreateAsync("triage", "Ticket triage", "Version two instructions, drafted by auto-learn.", pending: true, default, author: "kirana");
        Assert.True(Registry.ApprovePending("triage"));
        var current = Registry.Find("triage")!;
        Assert.Equal("1.1.0", current.Version);
        Assert.Contains("Version two", await Registry.LoadBodyAsync(current, default));
        Assert.Single(Registry.History("triage"));

        var eval = (await Evaluator.EvaluateAsync("triage"))!;
        Assert.Equal("1.0.0", eval.PreviousVersion);
        Assert.True(eval.CanRollback);

        Assert.Equal("1.0.0", await Evaluator.RollbackAsync("triage", "test"));
        Assert.Equal("1.0.0", Registry.Find("triage")!.Version);
        Assert.Contains("Version one", await Registry.LoadBodyAsync(Registry.Find("triage")!, default));
        // The replaced version is kept, so the rollback itself can be undone.
        Assert.Equal("1.1.0", Registry.History("triage")[^1].Version);
    }

    [Fact]
    public async Task Drafts_are_trialled_only_by_the_bot_that_wrote_them()
    {
        await Registry.CreateAsync("refund-flow", "Refund handling", "Check the order, then the policy, then answer.", pending: true, default, author: "kirana");
        var bots = _sp.GetRequiredService<BotRegistry>();
        var kirana = (await bots.ActiveAsync()).FirstOrDefault(b => b.Id == "kirana") ?? new BotDefinition { Id = "kirana" };
        Assert.Contains(Registry.ForBot(kirana), s => s.Name == "refund-flow" && s.Pending);
        Assert.DoesNotContain(Registry.ForBot(new BotDefinition { Id = "atlas" }), s => s.Name == "refund-flow");
    }

    [Fact]
    public async Task Automatic_rollback_when_enabled_and_the_new_version_keeps_failing()
    {
        await _sp.GetRequiredService<ApprovalService>().SetAutoRollbackSkillsAsync(true, "test");
        await Registry.CreateAsync("deploy-notes", "Deploy notes", "Old but reliable.", pending: false, default);
        await Registry.CreateAsync("deploy-notes", "Deploy notes", "New and flaky.", pending: true, default, author: "alice");
        Registry.ApprovePending("deploy-notes");
        for (var i = 0; i < 3; i++)
        {
            await Evaluator.ObserveAsync(new AgentEvent { Type = EventTypes.SkillLoaded, TaskId = "f" + i, BotId = "alice", Message = "deploy-notes" });
            await Evaluator.ObserveAsync(new AgentEvent { Type = EventTypes.TaskStateChanged, TaskId = "f" + i, Data = "Failed" });
        }
        Assert.Equal("1.0.0", Registry.Find("deploy-notes")!.Version);
    }

    [Fact]
    public async Task A_real_run_that_loads_a_trial_skill_is_recorded()
    {
        await Evaluator.StartAsync(default);
        await Registry.CreateAsync("bakery-pricing", "Price cakes", "Cost plus 60 percent, rounded to 500.", pending: true, default, author: "atlas");
        var mock = _sp.GetRequiredService<ModelRouter>().Mock;
        mock.EnqueueTool("load_skill", """{"name":"bakery-pricing"}""");
        mock.EnqueueText("A 40k cake sells for 64k.");
        var engine = _sp.GetRequiredService<MarbotsEngine>();
        var thread = await engine.CreateThreadAsync("atlas");
        var task = await engine.WaitAsync((await engine.SendAsync(thread.Id, "price a cake")).Id, TimeSpan.FromSeconds(10));
        Assert.Equal(TaskState.Completed, task.State);
        SkillEvaluation? eval = null;
        for (var i = 0; i < 50 && eval?.Current.Successes != 1; i++)
        {
            await Task.Delay(50);
            eval = await Evaluator.EvaluateAsync("bakery-pricing");
        }
        Assert.Equal(1, eval!.Current.Successes);
        Assert.True(eval.Pending);
        await Evaluator.StopAsync(default);
    }

    private async Task<FunctionResult> CallAsync(string tool, string json)
    {
        var fn = _sp.GetServices<IKernelFunction>().First(f => f.Descriptor.Name == tool);
        using var args = System.Text.Json.JsonDocument.Parse(json);
        return await fn.InvokeAsync(new FunctionCall("c1", tool, args.RootElement.Clone()),
            new FunctionExecutionContext { Bot = new BotDefinition { Id = WellKnown.BossManId, Name = "Boss Man" }, TaskId = "t", ThreadId = "th", WorkspacePath = _dir, Services = _sp }, default);
    }

    [Fact]
    public async Task Available_skills_are_given_to_bots_without_a_download()
    {
        var builtIn = Registry.All.First(s => s.Trust == "Marbots Verified");
        var r = await CallAsync("install_skill", $$"""{"name":"{{builtIn.Name}}","bots":["atlas"]}""");
        Assert.True(r.Success, r.Content);
        Assert.Contains(builtIn.Name, (await _sp.GetRequiredService<BotRegistry>().GetAsync("atlas"))!.Skills);
        Assert.Empty(await _sp.GetRequiredService<ApprovalService>().PendingAsync());
        Assert.Contains("pptx", (await CallAsync("list_skill_catalog", "{}")).Content); // curated, downloadable
    }

    [Fact]
    public async Task Skills_outside_the_catalog_are_refused_and_downloads_need_approval()
    {
        Assert.Contains("curated catalog", (await CallAsync("install_skill", """{"name":"totally-unknown"}""")).Content);
        var approvals = _sp.GetRequiredService<ApprovalService>();
        var call = CallAsync("install_skill", """{"name":"pptx","bots":["dara-not-needed"]}""");
        Assert.False((await call).Success); // unknown bot is reported before anything else
        var pending = CallAsync("install_skill", """{"name":"pptx"}""");
        ApprovalRequest? ask = null;
        for (var i = 0; i < 100 && ask is null; i++) { await Task.Delay(30); ask = (await approvals.PendingAsync()).FirstOrDefault(a => a.ToolName == "install_skill"); }
        Assert.NotNull(ask);
        Assert.Contains("anthropics/skills", ask.Summary);
        await approvals.ResolveAsync(ask.Id, false, ApprovalScope.Once, "test");
        Assert.False((await pending).Success);
        Assert.Null(Registry.Find("pptx"));
    }

    [Fact]
    public async Task Installing_from_a_source_can_pick_single_skills()
    {
        var src = Path.Combine(_dir, "repo");
        foreach (var n in new[] { "alpha", "beta" })
        {
            Directory.CreateDirectory(Path.Combine(src, n));
            await File.WriteAllTextAsync(Path.Combine(src, n, "SKILL.md"), $"---\nname: {n}\ndescription: {n} skill\n---\nBody");
        }
        var installed = await Registry.InstallAsync(src, default, ["beta"]);
        Assert.Equal(["beta"], installed.Select(s => s.Name));
        Assert.Null(Registry.Find("alpha"));
    }
}
