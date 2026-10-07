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
}
