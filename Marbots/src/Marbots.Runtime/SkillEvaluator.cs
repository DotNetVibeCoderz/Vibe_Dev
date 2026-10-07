using System.Collections.Concurrent;
using System.Threading.Channels;
using Marbots.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Marbots.Runtime;

/// <summary>
/// Learning evaluation: records which skill versions each task loaded and whether the task completed or failed, then
/// judges every skill: healthy, underperforming, worse than its previous version (rollback), or, for auto-learned
/// drafts on trial, ready to promote or better discarded. Publishing stays a human decision; rollback can be automatic
/// (<see cref="WorkspaceSettings.AutoRollbackSkills"/>).
/// </summary>
public sealed class SkillEvaluator(
    IEventBus bus,
    SkillRegistry registry,
    IDocumentStore<SkillStats> store,
    ApprovalService settings,
    ILogger<SkillEvaluator> log) : IHostedService
{
    /// <summary>Runs needed before a verdict other than "collecting evidence".</summary>
    public const int MinRuns = 3;
    public const double HealthyRate = 0.8;
    public const double PoorRate = 0.5;

    private readonly ConcurrentDictionary<string, HashSet<(string Name, string Version)>> _byTask = new();
    private readonly Channel<AgentEvent> _queue = Channel.CreateBounded<AgentEvent>(new BoundedChannelOptions(2048) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IDisposable? _subscription;
    private Task? _worker;
    private CancellationTokenSource? _stop;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _stop = new CancellationTokenSource();
        _subscription = bus.Subscribe(e =>
        {
            if (e.Type == EventTypes.SkillLoaded || e.Type == EventTypes.TaskStateChanged && e.Data is "Completed" or "Failed" or "Cancelled")
                _queue.Writer.TryWrite(e);
        });
        _worker = Task.Run(() => RunAsync(_stop.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var e in _queue.Reader.ReadAllAsync(ct))
            {
                try { await ObserveAsync(e, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Skill evaluation failed for {Type}", e.Type); }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Applies one event (public for tests; normally fed by the event bus).</summary>
    public async Task ObserveAsync(AgentEvent e, CancellationToken ct = default)
    {
        if (e.Type == EventTypes.SkillLoaded && e.TaskId is not null && e.Message is { Length: > 0 } name)
        {
            var skill = registry.All.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && (!s.Pending || s.Author == e.BotId))
                ?? registry.All.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (skill is null) return;
            var loaded = _byTask.GetOrAdd(e.TaskId, _ => []);
            lock (loaded)
                if (!loaded.Add((skill.Name, skill.Version))) return;
            await UpdateAsync(skill.Name, skill.Version, s => { s.Loads++; s.LastUsedAt = e.Timestamp; }, ct);
            return;
        }
        if (e.Type == EventTypes.TaskStateChanged && e.TaskId is not null && e.Data is "Completed" or "Failed" or "Cancelled"
            && _byTask.TryRemove(e.TaskId, out var skills))
        {
            if (e.Data == "Cancelled") return;
            (string Name, string Version)[] list;
            lock (skills) list = [.. skills];
            foreach (var (n, v) in list)
            {
                await UpdateAsync(n, v, s => { if (e.Data == "Completed") s.Successes++; else s.Failures++; }, ct);
                if (e.Data == "Failed") await MaybeAutoRollbackAsync(n, ct);
            }
        }
    }

    private async Task UpdateAsync(string name, string version, Action<SkillStats> change, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var id = SkillStats.Key(name, version);
            var stats = await store.GetAsync(id, ct) ?? new SkillStats { Id = id, Name = name, Version = version };
            change(stats);
            await store.UpsertAsync(stats, ct);
        }
        finally { _gate.Release(); }
    }

    private async Task MaybeAutoRollbackAsync(string name, CancellationToken ct)
    {
        if (!(await settings.GetSettingsAsync(ct)).AutoRollbackSkills) return;
        var eval = await EvaluateAsync(name, ct);
        if (eval?.Verdict != SkillVerdict.RollbackRecommended) return;
        if (registry.Rollback(name) is { } restored)
        {
            log.LogInformation("Auto-rollback of skill {Skill} to {Version}", name, restored);
            await bus.PublishAsync(new AgentEvent { Type = EventTypes.SkillRolledBack, Message = $"{name}: {eval.Version} → {restored} (automatic, {eval.Reason})" }, ct);
        }
    }

    public async Task<string?> RollbackAsync(string name, string by, CancellationToken ct = default)
    {
        var current = registry.All.FirstOrDefault(s => !s.Pending && s.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Version;
        if (registry.Rollback(name) is not { } restored) return null;
        await bus.PublishAsync(new AgentEvent { Type = EventTypes.SkillRolledBack, Message = $"{name}: {current} → {restored} (by {by})" }, ct);
        return restored;
    }

    public async Task<IReadOnlyList<SkillEvaluation>> EvaluateAllAsync(CancellationToken ct = default)
    {
        var stats = (await store.ListAsync(ct)).ToDictionary(s => s.Id);
        return registry.All.Select(s => Evaluate(s, stats)).ToList();
    }

    public async Task<SkillEvaluation?> EvaluateAsync(string name, CancellationToken ct = default)
    {
        var skill = registry.All.Where(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).OrderBy(s => s.Pending).FirstOrDefault();
        if (skill is null) return null;
        var stats = (await store.ListAsync(ct)).ToDictionary(s => s.Id);
        return Evaluate(skill, stats);
    }

    private SkillEvaluation Evaluate(SkillInfo skill, Dictionary<string, SkillStats> stats)
    {
        var current = stats.GetValueOrDefault(SkillStats.Key(skill.Name, skill.Version)) ?? new SkillStats { Id = SkillStats.Key(skill.Name, skill.Version), Name = skill.Name, Version = skill.Version };
        var history = skill.Pending ? [] : registry.History(skill.Name);
        var previousVersion = history.Count > 0 ? history[^1].Version : null;
        var previous = previousVersion is null ? null : stats.GetValueOrDefault(SkillStats.Key(skill.Name, previousVersion));
        var eval = new SkillEvaluation { Name = skill.Name, Version = skill.Version, Pending = skill.Pending, Current = current, PreviousVersion = previousVersion, Previous = previous };
        (eval.Verdict, eval.Reason) = Judge(current, previous, skill.Pending, previousVersion is not null);
        return eval;
    }

    /// <summary>The verdict rules (pure, unit-tested).</summary>
    public static (SkillVerdict, string) Judge(SkillStats current, SkillStats? previous, bool pending, bool hasPrevious)
    {
        var rate = current.SuccessRate;
        if (current.Runs < MinRuns)
            return (SkillVerdict.CollectingEvidence, $"{current.Runs} of {MinRuns} runs needed");
        var summary = $"{rate:P0} success over {current.Runs} runs";
        if (pending)
            return rate >= HealthyRate ? (SkillVerdict.ReadyToPromote, summary + " on trial")
                : rate < PoorRate ? (SkillVerdict.DiscardRecommended, summary + " on trial")
                : (SkillVerdict.CollectingEvidence, summary + " on trial; keep trying");
        if (rate >= HealthyRate) return (SkillVerdict.Healthy, summary);
        if (rate < PoorRate && hasPrevious && (previous is null || previous.Runs == 0 || previous.SuccessRate > rate))
            return (SkillVerdict.RollbackRecommended, previous is { Runs: > 0 }
                ? $"{summary}; previous version {previous.Version} had {previous.SuccessRate:P0}"
                : summary + "; previous version available");
        return (SkillVerdict.Underperforming, summary);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        if (_stop is not null) await _stop.CancelAsync();
        if (_worker is not null) await _worker.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }
}
