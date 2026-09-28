using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DotCode.Abstractions;
using DotCode.Engine.Extensibility;
using DotCode.Engine.Hooks;
using DotCode.Engine.Permissions;
using DotCode.Engine.Sessions;
using DotCode.Engine.Tools;
using DotCode.Engine.Tools.Builtin;
using DotCode.Engine.Util;

namespace DotCode.Engine.Agent;

/// <summary>Tracks which files the model has read (and when) so edits can require a prior read and detect
/// external modifications.</summary>
public sealed class FileStateCache
{
    private readonly Dictionary<string, (DateTime Mtime, bool Partial)> _state = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    public void MarkRead(string path, bool partial = false)
    {
        var full = Path.GetFullPath(path);
        lock (_gate) _state[full] = (File.Exists(full) ? File.GetLastWriteTimeUtc(full) : DateTime.MinValue, partial);
    }

    public bool WasRead(string path)
    {
        lock (_gate) return _state.ContainsKey(Path.GetFullPath(path));
    }

    /// <summary>True when the file changed on disk after it was last read or written by the agent.</summary>
    public bool ChangedSinceRead(string path)
    {
        var full = Path.GetFullPath(path);
        lock (_gate)
        {
            if (!_state.TryGetValue(full, out var s)) return false;
            return File.Exists(full) && File.GetLastWriteTimeUtc(full) > s.Mtime.AddMilliseconds(5);
        }
    }

    public IReadOnlyList<string> Paths { get { lock (_gate) return [.. _state.Keys]; } }
}

/// <summary>One conversation with the agent (main session or subagent). Owns history, todos, mode, model, usage and
/// runs the agentic loop: model → tools → model … until the model stops calling tools.</summary>
public sealed partial class AgentSession : IAsyncDisposable
{
    private readonly SemaphoreSlim _permissionGate;
    private readonly Lock _messageGate = new();
    private int _stopHookRetries;

    public AgentRuntime Runtime { get; }
    public string Id { get; }
    public string Cwd => Runtime.Cwd;
    public List<Message> Messages { get; } = [];
    public List<TodoItem> Todos { get; private set; } = [];
    public PermissionMode Mode { get; private set; }
    public bool BypassAvailable { get; }
    /// <summary>Auto mode is part of the Shift+Tab cycle (enabled in settings or started in auto mode).</summary>
    public bool AutoModeAvailable { get; }
    /// <summary>Set when web/MCP content was read during the current turn (auto mode treats later actions with suspicion).</summary>
    public bool UntrustedContentThisTurn { get; internal set; }
    public ResolvedModel Model { get; private set; }
    public ReasoningEffort Effort { get; set; }
    public PermissionEngine Permissions { get; }
    public FileStateCache FileState { get; } = new();
    public CheckpointManager Checkpoints { get; }
    public SessionStore? Store { get; internal set; }
    public IAgentEventSink Sink { get; set; } = NullEventSink.Instance;
    public IInteractionHandler Interaction { get; set; } = NonInteractiveHandler.Instance;
    public AgentSession? Parent { get; }
    public string? ParentToolUseId { get; init; }
    public AgentDefinition? AgentDefinition { get; init; }
    /// <summary>Tools registered by an SDK host (executed via callback).</summary>
    public List<Tool> ExtraTools { get; } = [];
    public string? Title { get; set; }
    public string? OutputStyle { get; set; }
    public string? CustomSystemPrompt { get; set; }
    public string? AppendSystemPrompt { get; set; }
    public int? MaxTurns { get; set; }

    public Usage TotalUsage { get; private set; } = Usage.Zero;
    public decimal TotalCostUsd { get; private set; }
    public Dictionary<string, (Usage Usage, decimal Cost)> UsageByModel { get; } = [];
    public long LastContextTokens { get; private set; }
    public TimeSpan ApiDuration { get; private set; }
    public int ModelCalls { get; private set; }
    public bool IsBusy { get; private set; }
    public bool IsSubagent => Parent is not null;
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
    internal bool RejectedThisTurn { get; set; }
    internal int MessagesAtLastCall { get; set; }

    internal AgentSession(AgentRuntime runtime, string id, ResolvedModel model, AgentSession? parent)
    {
        Runtime = runtime;
        Id = id;
        Model = model;
        Parent = parent;
        _permissionGate = parent?._permissionGate ?? new SemaphoreSlim(1, 1);
        Permissions = parent?.Permissions ?? new PermissionEngine(runtime.Settings, runtime.Cwd, runtime.ProjectRoot,
            runtime.Options.AllowedTools, runtime.Options.DisallowedTools, runtime.Options.AddDirs);
        Mode = parent?.Mode ?? runtime.InitialMode();
        BypassAvailable = parent?.BypassAvailable ?? (Mode == PermissionMode.BypassPermissions || runtime.Options.AllowDangerouslySkipPermissions) && !Permissions.BypassDisabled;
        AutoModeAvailable = parent?.AutoModeAvailable ?? (Mode == PermissionMode.Auto || runtime.Settings.Permissions?.AutoMode?.Enabled == true);
        Effort = parent?.Effort ?? runtime.InitialEffort();
        Checkpoints = parent?.Checkpoints ?? new CheckpointManager(Path.Combine(DotCodePaths.ProjectDataDir(runtime.Cwd), "checkpoints", id));
        OutputStyle = runtime.Options.OutputStyle ?? runtime.Settings.OutputStyle;
        CustomSystemPrompt = runtime.Options.SystemPrompt;
        AppendSystemPrompt = runtime.Options.AppendSystemPrompt;
        MaxTurns = runtime.Options.MaxTurns ?? runtime.Settings.MaxTurns;
        if (parent is not null)
        {
            Sink = parent.Sink;
            Interaction = parent.Interaction;
        }
    }

    public void Emit(AgentEvent e) => Sink.Emit(e with { SessionId = Parent?.Id ?? Id, ParentToolUseId = e.ParentToolUseId ?? ParentToolUseId });

    public void AppendMessage(Message m)
    {
        lock (_messageGate) Messages.Add(m);
        Store?.AppendMessage(m);
    }

    public void SetMode(PermissionMode mode)
    {
        if (mode == PermissionMode.BypassPermissions && !BypassAvailable) return;
        if (Mode == mode) return;
        Mode = mode;
        Emit(new ModeChangedEvent(mode.ToSetting()));
    }

    public void SetModel(string reference)
    {
        Model = Runtime.Router.Resolve(reference, Runtime.MainModelReference);
        Store?.AppendModel(Model.Qualified);
        Emit(new ModelChangedEvent(Model.Qualified));
    }

    public void SetTodos(List<TodoItem> todos)
    {
        Todos = todos;
        Emit(new TodoUpdatedEvent(todos));
    }

    internal void RestoreTodosFromHistory()
    {
        for (var i = Messages.Count - 1; i >= 0; i--)
        {
            var call = Messages[i].ToolUses.LastOrDefault(t => t.Name == "TodoWrite");
            if (call is null) continue;
            Todos = TodoWriteTool.Parse(call.Input);
            return;
        }
    }

    // ---------------- tools ----------------

    public IReadOnlyList<Tool> GetTools()
    {
        IEnumerable<Tool> tools = Runtime.BuiltinTools.Where(t => t.IsEnabled(this));
        if (Runtime.Options.Tools is { Count: > 0 } whitelist)
            tools = tools.Where(t => whitelist.Contains(t.Name, StringComparer.OrdinalIgnoreCase));
        tools = tools.Concat(Runtime.Mcp.CreateTools()).Concat(ExtraTools);

        var deniedWholeTools = Permissions.DenyRules.Where(r => r.Specifier is null).ToList();
        tools = tools.Where(t => !deniedWholeTools.Any(r => r.MatchesTool(t.Name)));

        if (IsSubagent)
        {
            tools = tools.Where(t => t.Name is not ("Agent" or "ExitPlanMode" or "AskUserQuestion"));
            if (AgentDefinition?.Tools is { } allowed)
                tools = tools.Where(t => allowed.Contains(t.Name, StringComparer.OrdinalIgnoreCase) || allowed.Any(a => a.StartsWith("mcp__", StringComparison.Ordinal) && t.Name.StartsWith(a, StringComparison.Ordinal)));
            if (AgentDefinition?.DisallowedTools is { } disallowed)
                tools = tools.Where(t => !disallowed.Contains(t.Name, StringComparer.OrdinalIgnoreCase));
        }
        // De-duplicate by name (host tools can shadow built-ins).
        return tools.GroupBy(t => t.Name).Select(g => g.Last()).ToList();
    }

    public Tool? FindTool(string name) => GetTools().FirstOrDefault(t => t.Name == name)
        ?? GetTools().FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

    internal async ValueTask<PermissionDecision> RequestPermissionAsync(PermissionRequest request, CancellationToken ct)
    {
        await _permissionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Runtime.Hooks.Has(HookEvents.Notification))
                _ = Runtime.Hooks.RunAsync(HookEvents.Notification, null, w => w.WriteString("message", $"DotCode needs your permission to use {request.ToolName}"), Id, Store?.FilePath ?? "", ct);
            return await Interaction.RequestPermissionAsync(request, ct).ConfigureAwait(false);
        }
        finally { _permissionGate.Release(); }
    }

    // ---------------- the agent loop ----------------

    /// <summary>Runs one user turn to completion (possibly many model calls and tool executions).</summary>
    public async Task<TurnResult> RunTurnAsync(string prompt, IReadOnlyList<ContentPart>? attachments = null, CancellationToken ct = default, bool isMeta = false)
    {
        var sw = Stopwatch.StartNew();
        var turnUsage = Usage.Zero;
        var turnCost = 0m;
        var calls = 0;
        var lastText = "";
        var stop = StopReason.EndTurn;
        IsBusy = true;
        RejectedThisTurn = false;
        if (!IsSubagent) UntrustedContentThisTurn = false;
        _stopHookRetries = 0;
        var turnId = Guid.NewGuid().ToString("n");
        if (!IsSubagent) Checkpoints.CurrentTurnId = turnId;

        try
        {
            await Runtime.McpReady.WaitAsync(TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        }
        catch (TimeoutException) { }

        try
        {
            // UserPromptSubmit hooks can block the prompt or add context.
            var parts = new List<ContentPart>();
            if (!IsSubagent && Runtime.Hooks.Has(HookEvents.UserPromptSubmit))
            {
                var hook = await Runtime.Hooks.RunAsync(HookEvents.UserPromptSubmit, null, w => w.WriteString("prompt", prompt), Id, Store?.FilePath ?? "", ct).ConfigureAwait(false);
                foreach (var m in hook.SystemMessages) Emit(new NoticeEvent(NoticeLevel.Warning, m));
                if (hook.Block || hook.StopAgent)
                {
                    var reason = hook.Reason ?? hook.StopReason ?? "blocked";
                    Emit(new NoticeEvent(NoticeLevel.Error, $"Prompt blocked by UserPromptSubmit hook: {reason}"));
                    return Finish(StopReason.EndTurn, "", true, $"Prompt blocked by hook: {reason}");
                }
                foreach (var ctx in hook.AdditionalContext) parts.Add(new TextPart($"<system-reminder>\n{ctx}\n</system-reminder>"));
            }

            var userParts = new List<ContentPart> { new TextPart(prompt) };
            userParts.AddRange(MentionResolver.Resolve(prompt, this));
            if (attachments is not null) userParts.AddRange(attachments);
            userParts.AddRange(parts);
            var userMessage = new Message { Role = Role.User, Content = userParts, Id = turnId, IsMeta = isMeta };
            AppendMessage(userMessage);
            if (!isMeta) Emit(new UserMessageEvent(prompt));

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (MaxTurns is { } max && calls >= max)
                {
                    Emit(new NoticeEvent(NoticeLevel.Warning, $"Reached max turns ({max})"));
                    stop = StopReason.MaxTokens;
                    break;
                }

                await EnsureContextBudgetAsync(ct).ConfigureAwait(false);
                var (assistant, reason, usage) = await CallModelAsync(ct).ConfigureAwait(false);
                calls++;
                stop = reason;
                var cost = Model.Capabilities.EstimateCost(usage);
                turnUsage += usage;
                turnCost += cost;
                RecordUsage(usage, cost);
                if (assistant.Content.Count > 0) AppendMessage(assistant);
                MessagesAtLastCall = Messages.Count;

                var toolUses = assistant.ToolUses.ToList();
                var text = assistant.Text;
                if (text.Length > 0) lastText = text;
                Emit(new AssistantMessageEvent(assistant.Id, text, assistant.Content.OfType<ThinkingPart>().FirstOrDefault()?.Text,
                    [.. toolUses.Select(t => new ToolCallInfo(t.Id, t.Name, t.Input))], Model.Qualified));

                if (toolUses.Count == 0)
                {
                    if (reason == StopReason.MaxTokens) Emit(new NoticeEvent(NoticeLevel.Warning, "Response was cut off (max output tokens reached)."));
                    if (reason == StopReason.Refusal) Emit(new NoticeEvent(NoticeLevel.Warning, "The model declined to respond."));
                    var hookEvent = IsSubagent ? HookEvents.SubagentStop : HookEvents.Stop;
                    if (Runtime.Hooks.Has(hookEvent) && _stopHookRetries < 3)
                    {
                        var hook = await Runtime.Hooks.RunAsync(hookEvent, null, w => w.WriteBoolean("stop_hook_active", _stopHookRetries > 0), Id, Store?.FilePath ?? "", ct).ConfigureAwait(false);
                        if (hook.Block && hook.Reason is { } feedback)
                        {
                            _stopHookRetries++;
                            AppendMessage(Message.User($"<system-reminder>Stop hook feedback:\n{feedback}\n</system-reminder>", isMeta: true));
                            continue;
                        }
                    }
                    break;
                }

                var results = await ToolExecutor.RunAsync(this, toolUses, ct).ConfigureAwait(false);
                AppendMessage(Message.User(results));
                if (StopRequested is { } stopReason)
                {
                    Emit(new NoticeEvent(NoticeLevel.Info, stopReason));
                    StopRequested = null;
                    break;
                }
                // A plain rejection (no feedback) ends the turn and waits for the user, like Claude Code.
                if (RejectedThisTurn) { stop = StopReason.EndTurn; break; }
            }
            return Finish(stop, lastText, false, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            RepairAfterInterrupt();
            return Finish(StopReason.Aborted, lastText, false, "Interrupted by user");
        }
        catch (ModelProviderException ex)
        {
            Emit(new ErrorEvent(ex.Code, ex.Message, ex.Retryable));
            RepairAfterInterrupt(addMarker: false);
            return Finish(StopReason.Error, lastText, true, ex.Message);
        }
        finally
        {
            IsBusy = false;
            if (!IsSubagent) Checkpoints.CurrentTurnId = null;
        }

        TurnResult Finish(StopReason reason, string text, bool isError, string? error)
        {
            var result = new TurnResult(reason, text, turnUsage, turnCost, sw.Elapsed, isError, error, calls);
            Emit(new TurnCompletedEvent(reason, text, turnUsage, turnCost, (long)sw.Elapsed.TotalMilliseconds, calls, isError));
            if (!IsSubagent && Title is null && !isError && calls > 0) _ = GenerateTitleAsync();
            CheckBudget();
            return result;
        }
    }

    /// <summary>Set by tools/hooks to end the turn after the current tool batch.</summary>
    internal string? StopRequested { get; set; }

    private void RecordUsage(Usage usage, decimal cost)
    {
        TotalUsage += usage;
        TotalCostUsd += cost;
        var key = Model.Qualified;
        UsageByModel[key] = UsageByModel.TryGetValue(key, out var prev) ? (prev.Usage + usage, prev.Cost + cost) : (usage, cost);
        if (usage.ContextTokens > 0) LastContextTokens = usage.ContextTokens;
        Parent?.RecordChildUsage(usage, cost, key);
        Emit(new UsageUpdatedEvent(usage, TotalUsage, TotalCostUsd, LastContextTokens, Model.Capabilities.ContextWindow));
    }

    /// <summary>Usage of helper model calls (auto-mode classifier, titles) counted toward session cost.</summary>
    internal void RecordAuxiliaryUsage(Usage usage, ResolvedModel model)
    {
        var cost = model.Capabilities.EstimateCost(usage);
        var root = this;
        while (root.Parent is not null) root = root.Parent;
        root.RecordChildUsage(usage, cost, model.Qualified);
    }

    private void RecordChildUsage(Usage usage, decimal cost, string key)
    {
        TotalUsage += usage;
        TotalCostUsd += cost;
        UsageByModel[key] = UsageByModel.TryGetValue(key, out var prev) ? (prev.Usage + usage, prev.Cost + cost) : (usage, cost);
        Parent?.RecordChildUsage(usage, cost, key);
    }

    private void CheckBudget()
    {
        if (Runtime.Settings.Budget?.MaxUsdPerSession is not { } max || TotalCostUsd < max || IsSubagent) return;
        Emit(new NoticeEvent(NoticeLevel.Warning, $"Session cost ${TotalCostUsd:0.00} exceeded budget ${max:0.00}."));
    }

    /// <summary>Keeps history valid after an interruption: every tool_use gets a tool_result.</summary>
    private void RepairAfterInterrupt(bool addMarker = true)
    {
        lock (_messageGate)
        {
            if (Messages.Count > 0 && Messages[^1] is { Role: Role.Assistant } last && last.ToolUses.Any())
            {
                var results = last.ToolUses.Select(t => (ContentPart)ToolResultPart.FromText(t.Id, "Interrupted by user", true)).ToList();
                var m = Message.User(results);
                Messages.Add(m);
                Store?.AppendMessage(m);
            }
        }
        if (addMarker) AppendMessage(Message.User("[Request interrupted by user]", isMeta: true));
    }

    /// <summary>Streams one model call with retry/backoff for transient errors and model fallback chains.</summary>
    private async Task<(Message Assistant, StopReason Reason, Usage Usage)> CallModelAsync(CancellationToken ct)
    {
        const int maxRetries = 8;
        var attempt = 0;
        var fallbackIndex = 0;
        IReadOnlyList<string>? chain = null;
        var compactedForLength = false;
        while (true)
        {
            var request = PromptBuilder.Build(this);
            var parts = new List<ContentPart>();
            var usage = Usage.Zero;
            StopReason? reason = null;
            var callWatch = Stopwatch.StartNew();
            try
            {
                await foreach (var ev in Model.Provider.StreamAsync(request, ct).ConfigureAwait(false))
                {
                    switch (ev)
                    {
                        case TextDelta td: Emit(new AssistantTextDeltaEvent(td.Text)); break;
                        case ThinkingDelta th: Emit(new AssistantThinkingDeltaEvent(th.Text)); break;
                        case ContentBlockCompleted cb: parts.Add(cb.Part); break;
                        case UsageUpdated uu: usage = uu.Usage; break;
                        case MessageStopped ms:
                            reason = ms.Reason;
                            if (ms.Usage is not null) usage = ms.Usage;
                            break;
                    }
                }
                ct.ThrowIfCancellationRequested();
                if (reason is null) throw new ModelProviderException(Model.ProviderName, "network", "Stream ended unexpectedly", true);
                ModelCalls++;
                ApiDuration += callWatch.Elapsed;
                // Order: thinking, text, tool calls (stable for all providers).
                var ordered = parts.OfType<ThinkingPart>().Cast<ContentPart>()
                    .Concat(parts.Where(p => p is not ThinkingPart and not ToolUsePart))
                    .Concat(parts.OfType<ToolUsePart>()).ToList();
                var message = new Message { Role = Role.Assistant, Content = ordered, ProviderId = Model.ProviderName, ModelId = Model.Model, Usage = usage };
                return (message, reason.Value, usage);
            }
            catch (ModelProviderException ex) when (!ct.IsCancellationRequested)
            {
                if (ex.Code == "context_length" && !compactedForLength)
                {
                    compactedForLength = true;
                    Emit(new NoticeEvent(NoticeLevel.Warning, "Context too long for the model; compacting conversation…"));
                    await Compactor.CompactAsync(this, null, automatic: true, ct).ConfigureAwait(false);
                    continue;
                }
                if (ex.Retryable && attempt < maxRetries)
                {
                    attempt++;
                    var delay = ex.RetryAfter ?? TimeSpan.FromSeconds(Math.Min(60, 0.5 * Math.Pow(2, attempt)) + Random.Shared.NextDouble());
                    Emit(new RetryEvent(attempt, maxRetries, delay.TotalSeconds, ex.Message));
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                    continue;
                }
                chain ??= Runtime.Options.FallbackModel is { } fb ? [fb] : Runtime.Router.FallbackChain(ex.Code);
                if (fallbackIndex < chain.Count)
                {
                    var from = Model.Qualified;
                    try
                    {
                        Model = Runtime.Router.Resolve(chain[fallbackIndex++], Runtime.MainModelReference);
                        Emit(new ModelFallbackEvent(from, Model.Qualified, ex.Code));
                        attempt = 0;
                        continue;
                    }
                    catch (InvalidOperationException) { }
                }
                throw;
            }
        }
    }

    private async Task EnsureContextBudgetAsync(CancellationToken ct)
    {
        if (Runtime.Settings.AutoCompact == false) return;
        var window = Model.Capabilities.ContextWindow;
        var reserve = Math.Min(Model.Capabilities.MaxOutputTokens, 20_000);
        var threshold = Runtime.Settings.AutoCompactThreshold ?? 0.85;
        var estimate = EstimateContextTokens();
        if (estimate < (window - reserve) * threshold) return;
        if (PromptBuilder.WindowMessages(this).Count < 3) return;
        await Compactor.CompactAsync(this, null, automatic: true, ct).ConfigureAwait(false);
    }

    /// <summary>Tokens the next request will occupy: last reported usage plus an estimate for newer messages.</summary>
    public long EstimateContextTokens()
    {
        var window = PromptBuilder.WindowMessages(this);
        if (LastContextTokens == 0 || MessagesAtLastCall == 0 || MessagesAtLastCall > Messages.Count)
            return window.Sum(TextUtil.EstimateTokens) + PromptBuilder.EstimateSystemTokens(this);
        long extra = 0;
        for (var i = MessagesAtLastCall; i < Messages.Count; i++) extra += TextUtil.EstimateTokens(Messages[i]);
        return LastContextTokens + extra;
    }

    internal void ResetContextTracking(long tokens)
    {
        LastContextTokens = tokens;
        MessagesAtLastCall = Messages.Count;
    }

    // ---------------- session operations ----------------

    public void Clear()
    {
        lock (_messageGate) Messages.Clear();
        Todos = [];
        LastContextTokens = 0;
        MessagesAtLastCall = 0;
        Store?.AppendClear();
    }

    /// <summary>User turns that can be rewound to (newest last).</summary>
    public IReadOnlyList<Message> UserTurns() =>
        Messages.Where(m => m.Role == Role.User && !m.IsMeta && !m.HasToolResults && m.Content.OfType<TextPart>().Any()).ToList();

    /// <summary>Rewinds conversation (and optionally files) to just before the given user message.</summary>
    public (int RemovedMessages, List<string> RestoredFiles) Rewind(string userMessageId, bool conversation, bool code)
    {
        var index = Messages.FindIndex(m => m.Id == userMessageId);
        if (index < 0) return (0, []);
        var turnIds = Messages.Skip(index).Where(m => m.Role == Role.User && !m.HasToolResults).Select(m => m.Id).ToList();
        var restored = code ? Checkpoints.Restore(turnIds) : [];
        var removed = 0;
        if (conversation)
        {
            lock (_messageGate)
            {
                removed = Messages.Count - index;
                Messages.RemoveRange(index, removed);
            }
            Store?.AppendRewind(index > 0 ? Messages[index - 1].Id : "");
            RestoreTodosFromHistory();
            LastContextTokens = 0;
            MessagesAtLastCall = 0;
        }
        return (removed, restored);
    }

    public Task CompactAsync(string? instructions, CancellationToken ct) => Compactor.CompactAsync(this, instructions, automatic: false, ct);

    /// <summary>Runs a shell command typed by the user ("!" bash mode) and records it as context for the model.</summary>
    public async Task<string> RunUserShellAsync(string command, CancellationToken ct)
    {
        var toolUseId = "bash_" + Guid.NewGuid().ToString("n")[..12];
        var input = DotCodeJson.Build(w => { w.WriteStartObject(); w.WriteString("command", command); w.WriteEndObject(); });
        var tool = FindTool("Bash") ?? FindTool("PowerShell");
        if (tool is null) return "No shell available";
        Emit(new ToolStartedEvent(toolUseId, tool.Name, $"{tool.Name}({command})", input));
        var sw = Stopwatch.StartNew();
        var result = await tool.ExecuteAsync(input, new ToolContext { Session = this, ToolUseId = toolUseId }, ct).ConfigureAwait(false);
        Emit(new ToolCompletedEvent(toolUseId, tool.Name, result.IsError, result.Summary, result.Text) { DurationMs = sw.ElapsedMilliseconds });
        AppendMessage(Message.User($"<bash-input>{command}</bash-input>", isMeta: true));
        AppendMessage(Message.User($"<bash-stdout>{TextUtil.Truncate(result.Text, 30_000)}</bash-stdout>", isMeta: true));
        return result.Text;
    }

    private async Task GenerateTitleAsync()
    {
        try
        {
            var first = UserTurns().FirstOrDefault()?.Text;
            if (string.IsNullOrWhiteSpace(first)) return;
            var fast = Runtime.Router.Resolve("fast", Runtime.MainModelReference);
            var request = new ModelRequest
            {
                Model = fast.Model,
                System = [new SystemBlock("Write a concise title (max 6 words, no quotes, no trailing punctuation) for a coding session that starts with the user's message below. Reply with the title only.")],
                Messages = [Message.User(first.Length > 2000 ? first[..2000] : first)],
                MaxOutputTokens = 2000,
                Reasoning = new ReasoningOptions(ReasoningEffort.Off),
            };
            var sb = new StringBuilder();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await foreach (var ev in fast.Provider.StreamAsync(request, cts.Token).ConfigureAwait(false))
                if (ev is ContentBlockCompleted { Part: TextPart t }) sb.Append(t.Text);
            var title = sb.ToString().Trim().Trim('"', '\'', '.').Split('\n')[0];
            if (title.Length is > 0 and < 100)
            {
                Title = title;
                Store?.AppendTitle(title);
            }
        }
        catch (Exception) { /* titles are best-effort */ }
    }

    public async ValueTask DisposeAsync()
    {
        if (!IsSubagent && Runtime.Hooks.Has(HookEvents.SessionEnd))
            await Runtime.Hooks.RunAsync(HookEvents.SessionEnd, null, w => w.WriteString("reason", "exit"), Id, Store?.FilePath ?? "", CancellationToken.None).ConfigureAwait(false);
        Store?.Dispose();
    }

    /// <summary>Creates a child session for a subagent (shares permissions, checkpoints, sink and interaction).</summary>
    public AgentSession CreateSubagent(AgentDefinition definition, string toolUseId, ResolvedModel model) =>
        new(Runtime, Guid.NewGuid().ToString(), model, this) { AgentDefinition = definition, ParentToolUseId = toolUseId };

    public async Task FireSessionStartAsync(string source, CancellationToken ct)
    {
        if (!Runtime.Hooks.Has(HookEvents.SessionStart)) return;
        var hook = await Runtime.Hooks.RunAsync(HookEvents.SessionStart, null, w => w.WriteString("source", source), Id, Store?.FilePath ?? "", ct).ConfigureAwait(false);
        foreach (var ctx in hook.AdditionalContext)
            AppendMessage(Message.User($"<system-reminder>\nSessionStart hook context:\n{ctx}\n</system-reminder>", isMeta: true));
    }
}
