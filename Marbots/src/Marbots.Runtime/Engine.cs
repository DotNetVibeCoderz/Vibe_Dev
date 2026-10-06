using System.Collections.Concurrent;
using System.Text;
using Marbots.Abstractions;
using Microsoft.Extensions.Logging;

namespace Marbots.Runtime;

public sealed record DelegationSpec(string Key, string Bot, string Objective, IReadOnlyList<string> DependsOn);

public sealed record DelegationOutcome(string Key, string BotId, string BotName, string TaskId, TaskState State, string Output);

/// <summary>
/// Orchestration entry point: threads, root tasks, delegation DAGs, cancellation and bot run-state.
/// </summary>
public sealed class MarbotsEngine(
    BotRegistry registry,
    IDocumentStore<ChatThread> threads,
    IDocumentStore<TaskRecord> tasks,
    IMessageStore messages,
    AgentRuntime runtime,
    ContextManager context,
    AutoLearnService autoLearn,
    IEventBus bus,
    MarbotsOptions options,
    ILogger<MarbotsEngine> log) : IDisposable
{
    private int _disposed;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        Shutdown();
        _shutdown.Dispose();
        _rootSlots.Dispose();
    }

    private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<TaskRecord>> _completions = new();
    private readonly ConcurrentDictionary<string, int> _activeByBot = new();
    private readonly SemaphoreSlim _rootSlots = new(options.MaxConcurrentRuns, options.MaxConcurrentRuns);
    private readonly CancellationTokenSource _shutdown = new();

    public string WorkspaceFor(string threadId) => options.DataPath("workspaces", Ids.Slug(threadId) is { Length: > 0 } slug ? slug : "default");

    // ---------------- threads ----------------

    public async Task<ChatThread> CreateThreadAsync(string botId, string? title = null, CancellationToken ct = default)
    {
        var bot = await registry.ResolveAsync(botId, ct) ?? throw new BotValidationException($"Bot '{botId}' not found.");
        var thread = new ChatThread { Id = Ids.New("thr"), BotId = bot.Id, Title = string.IsNullOrWhiteSpace(title) ? $"Chat with {bot.Name}" : title };
        await threads.UpsertAsync(thread, ct);
        return thread;
    }

    public Task<ChatThread?> GetThreadAsync(string id, CancellationToken ct = default) => threads.GetAsync(id, ct);

    public async Task<IReadOnlyList<ChatThread>> ListThreadsAsync(string? botId = null, bool includeArchived = false, CancellationToken ct = default) =>
        (await threads.ListAsync(ct))
            .Where(t => (botId is null || t.BotId == botId) && (includeArchived || !t.Archived))
            .OrderByDescending(t => t.Pinned).ThenByDescending(t => t.UpdatedAt).ToList();

    public async Task<ChatThread> UpdateThreadAsync(string id, Action<ChatThread> change, CancellationToken ct = default)
    {
        var t = await threads.GetAsync(id, ct) ?? throw new BotValidationException("Thread not found.");
        change(t);
        t.UpdatedAt = DateTimeOffset.UtcNow;
        await threads.UpsertAsync(t, ct);
        return t;
    }

    /// <summary>Starts a fresh model context. History stays visible for audit; long-term memory is kept unless <paramref name="forgetMemory"/>.</summary>
    public async Task<ChatThread> ResetContextAsync(string id, bool forgetMemory, IMemoryStore memory, CancellationToken ct = default)
    {
        var list = await messages.ListAsync(id, 0, 100_000, ct);
        var t = await UpdateThreadAsync(id, t =>
        {
            t.ContextStartSeq = list.Count == 0 ? 0 : list[^1].Seq;
            t.Summary = null;
            t.SummaryUpToSeq = 0;
        }, ct);
        await messages.AppendAsync(new ChatMessage { ThreadId = id, Role = "system", Author = "system", Content = forgetMemory ? "Context reset — learned memory was cleared." : "Context reset — the bot starts fresh from here." }, ct);
        if (forgetMemory) await memory.DeleteOwnerAsync(t.BotId, ct);
        await bus.PublishAsync(new AgentEvent { Type = EventTypes.MessageAdded, ThreadId = id, Message = "Context reset" }, ct);
        return t;
    }

    /// <summary>Branches a thread from a checkpoint (message sequence). The fork shares the workspace folder.</summary>
    public async Task<ChatThread> ForkAsync(string id, long uptoSeq, CancellationToken ct = default)
    {
        var src = await threads.GetAsync(id, ct) ?? throw new BotValidationException("Thread not found.");
        var fork = new ChatThread { Id = Ids.New("thr"), BotId = src.BotId, Title = src.Title + " (fork)", ParentThreadId = src.Id };
        await threads.UpsertAsync(fork, ct);
        foreach (var m in await messages.ListAsync(id, 0, 100_000, ct))
        {
            if (uptoSeq > 0 && m.Seq > uptoSeq) break;
            await messages.AppendAsync(new ChatMessage
            {
                ThreadId = fork.Id, Role = m.Role, Author = m.Author, Content = m.Content, ToolCalls = m.ToolCalls,
                ToolCallId = m.ToolCallId, ToolName = m.ToolName, TaskId = m.TaskId, CreatedAt = m.CreatedAt,
            }, ct);
        }
        var srcWs = WorkspaceFor(src.Id);
        if (Directory.Exists(srcWs)) SkillRegistry.CopyDirectory(srcWs, WorkspaceFor(fork.Id));
        return fork;
    }

    public async Task DeleteThreadAsync(string id, CancellationToken ct = default)
    {
        await messages.DeleteThreadAsync(id, ct);
        await threads.DeleteAsync(id, ct);
    }

    public async Task<string> ExportTranscriptAsync(string id, CancellationToken ct = default)
    {
        var t = await threads.GetAsync(id, ct) ?? throw new BotValidationException("Thread not found.");
        var bots = (await registry.ListAsync(ct)).ToDictionary(b => b.Id, b => b.Name);
        var sb = new StringBuilder();
        sb.Append("# ").AppendLine(t.Title).AppendLine()
          .Append("_Exported from Marbots — ").Append(WellKnown.CreditsEn).Append(" — ").Append(DateTimeOffset.UtcNow.ToString("u")).AppendLine("_").AppendLine();
        foreach (var m in await messages.ListAsync(id, 0, 100_000, ct))
        {
            if (m.Role == "tool")
            {
                sb.Append("> 🔧 `").Append(m.ToolName).Append("` → ").AppendLine(AgentRuntime.Preview(m.Content, 300)).AppendLine();
                continue;
            }
            var who = m.Role == "user" ? "User" : m.Role == "system" ? "System" : bots.GetValueOrDefault(m.Author, m.Author);
            sb.Append("**").Append(who).Append("** · ").AppendLine(m.CreatedAt.ToString("yyyy-MM-dd HH:mm")).AppendLine();
            if (m.Content.Length > 0) sb.AppendLine(m.Content).AppendLine();
            if (m.ToolCalls is { Count: > 0 }) sb.Append("_calls: ").Append(string.Join(", ", m.ToolCalls.Select(c => c.Name))).AppendLine("_").AppendLine();
        }
        return sb.ToString();
    }

    // ---------------- root tasks ----------------

    /// <summary>Accepts a user message and starts the bot in the background. Supports /compact and /reset commands.</summary>
    public Task<TaskRecord> SendAsync(string threadId, string text, CancellationToken ct = default) =>
        SendAsync(threadId, text, WellKnown.UserAuthor, ct);

    /// <summary>Like <see cref="SendAsync(string, string, CancellationToken)"/>, recording who started the task (user, trigger:id, channel:id).</summary>
    public async Task<TaskRecord> SendAsync(string threadId, string text, string assignedBy, CancellationToken ct = default)
    {
        var thread = await threads.GetAsync(threadId, ct) ?? throw new BotValidationException("Thread not found.");
        var bot = await registry.GetAsync(thread.BotId, ct) ?? throw new BotValidationException("The bot for this thread no longer exists.");
        if (bot.Status is BotStatus.Paused or BotStatus.Archived) throw new BotValidationException($"{bot.Name} is {bot.Status.ToString().ToLowerInvariant()}. Resume the bot to chat.");
        text = text.Trim();
        if (text.Length == 0) throw new BotValidationException("Message is empty.");

        var userMsg = await messages.AppendAsync(new ChatMessage { ThreadId = threadId, Role = "user", Author = WellKnown.UserAuthor, Content = text }, ct);
        await bus.PublishAsync(new AgentEvent { Type = EventTypes.MessageAdded, ThreadId = threadId, BotId = WellKnown.UserAuthor, Data = userMsg.Id, Message = AgentRuntime.Preview(text, 120) }, ct);

        if (thread.Title.StartsWith("Chat with ", StringComparison.Ordinal) || thread.Title == "New thread")
        {
            thread.Title = AgentRuntime.Preview(text, 48);
        }
        thread.UpdatedAt = DateTimeOffset.UtcNow;
        await threads.UpsertAsync(thread, ct);

        var task = new TaskRecord
        {
            Id = Ids.New("task"), ThreadId = threadId, TranscriptId = threadId, BotId = bot.Id,
            AssignedBy = assignedBy, Objective = text, Depth = 0,
        };
        task.RootTaskId = task.Id;
        await tasks.UpsertAsync(task, ct);
        await bus.PublishAsync(new AgentEvent { Type = EventTypes.TaskCreated, TaskId = task.Id, BotId = bot.Id, ThreadId = threadId, Message = AgentRuntime.Preview(text, 120) }, ct);

        var cts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        _running[task.Id] = cts;
        _completions[task.Id] = new TaskCompletionSource<TaskRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(() => RunRootAsync(task, thread, bot, text, cts), CancellationToken.None);
        return task;
    }

    private async Task RunRootAsync(TaskRecord task, ChatThread thread, BotDefinition bot, string text, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        try
        {
            await _rootSlots.WaitAsync(ct);
            try
            {
                if (text.Equals("/compact", StringComparison.OrdinalIgnoreCase))
                {
                    var compacted = await context.CompactAsync(thread, bot, null, "manual /compact", ct);
                    await FinishAsync(task, TaskState.Completed, compacted is null ? "Nothing to compact yet." : $"Context compacted. Summary:\n\n{compacted.Summary}", null, true);
                    return;
                }
                await MarkStartedAsync(task, ct);
                var result = await runtime.RunAsync(new AgentRunRequest
                {
                    Bot = bot, Task = task, ThreadId = thread.Id, ConversationThread = thread, Input = text, Workspace = WorkspaceFor(thread.Id),
                }, ct);
                await FinishAsync(task, TaskState.Completed, result.Output, null, false);
                _ = autoLearn.LearnAsync(bot, task, CancellationToken.None);
            }
            finally
            {
                _rootSlots.Release();
            }
        }
        catch (OperationCanceledException)
        {
            await FinishAsync(task, TaskState.Cancelled, null, "Cancelled.", true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            log.LogError(ex, "Task {Task} failed", task.Id);
            await FinishAsync(task, TaskState.Failed, null, ex.Message, true);
        }
        finally
        {
            await MarkStoppedAsync(task.BotId);
            if (_running.TryRemove(task.Id, out var c)) c.Dispose();
        }
    }

    private async Task MarkStartedAsync(TaskRecord task, CancellationToken ct)
    {
        task.State = TaskState.Preparing;
        task.StartedAt = DateTimeOffset.UtcNow;
        await tasks.UpsertAsync(task, ct);
        await bus.PublishAsync(new AgentEvent { Type = EventTypes.TaskStateChanged, TaskId = task.Id, BotId = task.BotId, ThreadId = task.ThreadId, Message = "Started", Data = nameof(TaskState.Preparing) }, ct);
        if (_activeByBot.AddOrUpdate(task.BotId, 1, (_, n) => n + 1) == 1)
            await registry.SetStatusAsync(task.BotId, BotStatus.Running, ct);
    }

    private async Task MarkStoppedAsync(string botId)
    {
        if (!_activeByBot.TryGetValue(botId, out var n) || n <= 0) return;
        if (_activeByBot.AddOrUpdate(botId, 0, (_, v) => Math.Max(0, v - 1)) == 0)
        {
            var bot = await registry.GetAsync(botId);
            if (bot?.Status == BotStatus.Running) await registry.SetStatusAsync(botId, BotStatus.Ready);
        }
    }

    private async Task FinishAsync(TaskRecord task, TaskState state, string? result, string? error, bool writeToTranscript)
    {
        task.State = state;
        task.Result = result;
        task.Error = error;
        task.CurrentActivity = null;
        task.CompletedAt = DateTimeOffset.UtcNow;
        await tasks.UpsertAsync(task, CancellationToken.None);
        if (writeToTranscript && task.Depth == 0)
        {
            var msg = await messages.AppendAsync(new ChatMessage
            {
                ThreadId = task.TranscriptId, Role = state == TaskState.Completed ? "assistant" : "system", Author = state == TaskState.Completed ? task.BotId : "system",
                Content = result ?? $"⚠ {error}", TaskId = task.Id,
            }, CancellationToken.None);
            await bus.PublishAsync(new AgentEvent { Type = EventTypes.MessageAdded, ThreadId = task.TranscriptId, TaskId = task.Id, BotId = task.BotId, Data = msg.Id }, CancellationToken.None);
        }
        await bus.PublishAsync(new AgentEvent
        {
            Type = EventTypes.TaskStateChanged, TaskId = task.Id, BotId = task.BotId, ThreadId = task.ThreadId,
            Message = state == TaskState.Completed ? "Completed" : $"{state}: {error}", Data = state.ToString(),
        }, CancellationToken.None);
        if (_completions.TryRemove(task.Id, out var tcs)) tcs.TrySetResult(task);
    }

    public async Task<TaskRecord> WaitAsync(string taskId, TimeSpan timeout, CancellationToken ct = default)
    {
        if (_completions.TryGetValue(taskId, out var tcs))
        {
            try { return await tcs.Task.WaitAsync(timeout, ct); }
            catch (TimeoutException) { }
        }
        return await tasks.GetAsync(taskId, ct) ?? throw new BotValidationException("Task not found.");
    }

    public async Task<bool> CancelAsync(string taskId)
    {
        var any = false;
        // Cancel the task and every descendant still running.
        foreach (var t in await tasks.ListAsync())
        {
            if ((t.Id == taskId || t.RootTaskId == taskId || t.ParentTaskId == taskId) && _running.TryGetValue(t.Id, out var cts))
            {
                await cts.CancelAsync();
                any = true;
            }
        }
        return any;
    }

    public Task<TaskRecord?> GetTaskAsync(string id, CancellationToken ct = default) => tasks.GetAsync(id, ct);

    public async Task<IReadOnlyList<TaskRecord>> ListTasksAsync(CancellationToken ct = default) =>
        (await tasks.ListAsync(ct)).OrderByDescending(t => t.CreatedAt).ToList();

    public async Task<TaskRecord> RetryAsync(string taskId, CancellationToken ct = default)
    {
        var t = await tasks.GetAsync(taskId, ct) ?? throw new BotValidationException("Task not found.");
        if (t.Depth != 0) throw new BotValidationException("Only top-level tasks can be retried.");
        return await SendAsync(t.ThreadId, t.Objective, ct);
    }

    /// <summary>Tasks interrupted by a restart are marked failed (retryable); bots that were running are reset.</summary>
    public async Task RecoverAsync(CancellationToken ct)
    {
        foreach (var t in await tasks.ListAsync(ct))
        {
            if (t.State.IsTerminal()) continue;
            t.State = TaskState.Failed;
            t.Error = "Interrupted by a platform restart. Use Retry to run it again.";
            t.CompletedAt = DateTimeOffset.UtcNow;
            await tasks.UpsertAsync(t, ct);
        }
        foreach (var b in await registry.ListAsync(ct))
            if (b.Status == BotStatus.Running) await registry.SetStatusAsync(b.Id, BotStatus.Ready, ct);
    }

    public void Shutdown()
    {
        if (Volatile.Read(ref _disposed) == 1 && _shutdown.IsCancellationRequested) return;
        try { _shutdown.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    // ---------------- delegation ----------------

    /// <summary>Runs a dependency graph of sub-tasks; independent tasks run in parallel (bounded).</summary>
    public async Task<IReadOnlyList<DelegationOutcome>> DelegateAsync(TaskRecord parent, BotDefinition from, IReadOnlyList<DelegationSpec> specs, CancellationToken ct)
    {
        if (specs.Count == 0) throw new BotValidationException("No tasks to delegate.");
        if (specs.Count > 10) throw new BotValidationException("Delegate at most 10 tasks at a time.");
        var keys = specs.Select(s => s.Key).ToList();
        if (keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != keys.Count) throw new BotValidationException("Task keys must be unique.");
        foreach (var s in specs)
            foreach (var dep in s.DependsOn)
                if (!keys.Contains(dep, StringComparer.OrdinalIgnoreCase)) throw new BotValidationException($"Task '{s.Key}' depends on unknown key '{dep}'.");
        DetectCycle(specs);

        var resolved = new Dictionary<string, BotDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in specs)
        {
            var bot = await registry.ResolveAsync(s.Bot, ct) ?? throw new BotValidationException($"No bot named '{s.Bot}'. Call list_bots to see the team.");
            if (bot.Id == from.Id) throw new BotValidationException("You cannot delegate to yourself.");
            if (bot.Status is BotStatus.Paused or BotStatus.Archived) throw new BotValidationException($"{bot.Name} is {bot.Status}.");
            resolved[s.Key] = bot;
        }

        var outcomes = new ConcurrentDictionary<string, DelegationOutcome>(StringComparer.OrdinalIgnoreCase);
        var started = new ConcurrentDictionary<string, Task>(StringComparer.OrdinalIgnoreCase);
        using var slots = new SemaphoreSlim(options.MaxParallelDelegations, options.MaxParallelDelegations);

        Task Start(DelegationSpec spec) => started.GetOrAdd(spec.Key, _ => Task.Run(async () =>
        {
            foreach (var dep in spec.DependsOn) await Start(specs.First(x => x.Key.Equals(dep, StringComparison.OrdinalIgnoreCase)));
            var bot = resolved[spec.Key];
            var failedDeps = spec.DependsOn.Where(d => outcomes.TryGetValue(d, out var o) && o.State != TaskState.Completed).ToList();
            if (failedDeps.Count > 0)
            {
                outcomes[spec.Key] = new(spec.Key, bot.Id, bot.Name, "", TaskState.Cancelled, $"Skipped because prerequisite(s) failed: {string.Join(", ", failedDeps)}");
                return;
            }
            var objective = new StringBuilder(spec.Objective);
            foreach (var dep in spec.DependsOn)
            {
                var o = outcomes[dep];
                objective.AppendLine().AppendLine().Append("--- Result from prerequisite task '").Append(dep).Append("' (").Append(o.BotName).AppendLine(") ---")
                    .AppendLine(o.Output.Length > 6000 ? o.Output[..6000] + "…" : o.Output);
            }
            await slots.WaitAsync(ct);
            try
            {
                outcomes[spec.Key] = await RunChildAsync(parent, from, bot, spec.Key, objective.ToString(), ct);
            }
            finally { slots.Release(); }
        }, ct));

        await Task.WhenAll(specs.Select(Start));
        return specs.Select(s => outcomes[s.Key]).ToList();
    }

    private async Task<DelegationOutcome> RunChildAsync(TaskRecord parent, BotDefinition from, BotDefinition bot, string key, string objective, CancellationToken ct)
    {
        var child = new TaskRecord
        {
            Id = Ids.New("task"), ParentTaskId = parent.Id, RootTaskId = parent.RootTaskId, ThreadId = parent.ThreadId,
            BotId = bot.Id, AssignedBy = from.Id, Objective = objective, Depth = parent.Depth + 1,
        };
        child.TranscriptId = "transcript_" + child.Id;
        await tasks.UpsertAsync(child, ct);
        await bus.PublishAsync(new AgentEvent
        {
            Type = EventTypes.TaskDelegated, TaskId = child.Id, BotId = bot.Id, ThreadId = parent.ThreadId,
            Message = $"{from.Name} → {bot.Name}: {AgentRuntime.Preview(objective, 140)}", Data = from.Id,
        }, ct);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _running[child.Id] = cts;
        try
        {
            await MarkStartedAsync(child, cts.Token);
            var result = await runtime.RunAsync(new AgentRunRequest
            {
                Bot = bot, Task = child, ThreadId = parent.ThreadId, Input = objective, Workspace = WorkspaceFor(parent.ThreadId),
            }, cts.Token);
            await FinishAsync(child, TaskState.Completed, result.Output, null, false);
            _ = autoLearn.LearnAsync(bot, child, CancellationToken.None);
            return new(key, bot.Id, bot.Name, child.Id, TaskState.Completed, result.Output);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested || cts.IsCancellationRequested)
        {
            await FinishAsync(child, TaskState.Cancelled, null, "Cancelled.", false);
            if (ct.IsCancellationRequested) throw;
            return new(key, bot.Id, bot.Name, child.Id, TaskState.Cancelled, "Cancelled by the user.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            log.LogWarning(ex, "Delegated task {Task} failed", child.Id);
            await FinishAsync(child, TaskState.Failed, null, ex.Message, false);
            return new(key, bot.Id, bot.Name, child.Id, TaskState.Failed, "Failed: " + ex.Message);
        }
        finally
        {
            _running.TryRemove(child.Id, out _);
            await MarkStoppedAsync(bot.Id);
        }
    }

    private static void DetectCycle(IReadOnlyList<DelegationSpec> specs)
    {
        var map = specs.ToDictionary(s => s.Key, s => s.DependsOn, StringComparer.OrdinalIgnoreCase);
        var state = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        void Visit(string k)
        {
            if (state.TryGetValue(k, out var s))
            {
                if (s == 1) throw new BotValidationException($"Dependency cycle detected at '{k}'.");
                return;
            }
            state[k] = 1;
            foreach (var d in map[k]) Visit(d);
            state[k] = 2;
        }
        foreach (var k in map.Keys) Visit(k);
    }
}
