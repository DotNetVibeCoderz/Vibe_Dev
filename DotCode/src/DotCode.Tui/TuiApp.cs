using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using DotCode.Abstractions;
using DotCode.Engine;
using DotCode.Engine.Agent;
using DotCode.Engine.Permissions;
using DotCode.Engine.Sessions;
using DotCode.Tui.Components;
using DotCode.Tui.Input;
using DotCode.Tui.Input;
using DotCode.Tui.Rendering;
using DotCode.Tui.Themes;

namespace DotCode.Tui;

public sealed class TuiLaunch
{
    public string? InitialPrompt { get; init; }
    public bool Continue { get; init; }
    public bool Resume { get; init; }
    public string? ResumeId { get; init; }
    public bool ForkSession { get; init; }
    public string? Theme { get; init; }
}

internal abstract record UiEvent;
internal sealed record KeyUiEvent(ConsoleKeyInfo Key, bool Burst) : UiEvent;
internal sealed record AgentUiEvent(AgentEvent Event) : UiEvent;
internal sealed record TickUiEvent : UiEvent;
internal sealed record TurnDoneUiEvent(TurnResult? Result, Exception? Error) : UiEvent;
internal sealed record ModalUiEvent(Modal Modal) : UiEvent;
internal sealed record ActionUiEvent(Action Action) : UiEvent;

internal sealed class PendingTool
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string DisplayName { get; init; }
    public ToolCompletedEvent? Completed { get; set; }
    public string? Progress { get; set; }
    public bool IsSubagent { get; set; }
    public List<(string Name, string Display, bool Done, bool Error)> Children { get; } = [];
    public SubagentCompletedEvent? SubagentDone { get; set; }
    public DateTime Started { get; } = DateTime.UtcNow;
}

/// <summary>Interactive terminal UI. One UI thread consumes a channel of key presses, engine events, timer ticks and
/// modal requests; the agent turn runs on the thread pool and talks to the UI only through that channel.</summary>
public static partial class TuiApp
{
    public static async Task<int> RunAsync(RuntimeOptions options, TuiLaunch launch)
    {
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            Console.Error.WriteLine("Interactive mode needs a terminal. Use -p/--print for non-interactive use.");
            return 2;
        }
        var app = new App(options, launch);
        return await app.RunAsync().ConfigureAwait(false);
    }
}

internal sealed partial class App : IInteractionHandler
{
    private readonly RuntimeOptions _options;
    private readonly TuiLaunch _launch;
    private readonly Channel<UiEvent> _events = Channel.CreateUnbounded<UiEvent>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Screen _screen = new();
    private Theme _theme = Theme.Dark();
    private Blocks _blocks = null!;
    private AgentRuntime _runtime = null!;
    private AgentSession _session = null!;
    private InputEditor _input = null!;
    private readonly List<string> _history = [];

    // turn state
    private bool _busy;
    private CancellationTokenSource? _turnCts;
    private DateTime _turnStart;
    private string _verb = "Working";
    private int _frame;
    private long _streamChars;
    private long _turnTokens;
    private long _charsSinceUsage;
    private DateTime _verbChangedAt;
    private string[] _verbs = SpinnerLine.Verbs;
    private bool _thinking;
    private string? _retryDetail;
    private readonly StringBuilder _stream = new();
    private readonly StringBuilder _thinkingText = new();
    private readonly List<PendingTool> _pending = [];
    private readonly Queue<string> _queued = new();
    private string _tip = "";
    private bool _anyCommitted;

    // UI state
    private Modal? _modal;
    private readonly Queue<Modal> _modalQueue = new();
    private bool _verbose;
    private bool _showShortcuts;
    private bool _showTodos;
    private string? _flash;
    private DateTime _flashUntil;
    private DateTime _lastCtrlC = DateTime.MinValue;
    private DateTime _lastEsc = DateTime.MinValue;
    private bool _exit;
    private const int _exitCode = 0;
    private List<(string Label, string Description, string Insert)> _suggestions = [];
    private int _suggestIndex;
    private string? _statusLine;
    private long _linesAdded, _linesRemoved;
    private readonly Stopwatch _wall = Stopwatch.StartNew();
    private List<string>? _fileIndex;
    private bool _dirty = true;
    private VimMode? _vim;
    private HistorySearch? _search;
    private readonly List<(List<string> Lines, bool Spacing)> _deferredCommits = [];

    public App(RuntimeOptions options, TuiLaunch launch)
    {
        _options = options;
        _launch = launch;
    }

    public async Task<int> RunAsync()
    {
        Console.TreatControlCAsInput = true;
        _runtime = AgentRuntime.Create(_options);
        ApplyUiSettings();
        _theme = Theme.Load(_launch.Theme ?? _runtime.Settings.Theme, _runtime.Settings.Tui);
        _blocks = new Blocks(_theme);
        _verbose = _options.Verbose;
        LoadHistory();
        _input = new InputEditor(_history);

        try { _session = OpenSession(); }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
        _session.Sink = new DelegateEventSink(e => _events.Writer.TryWrite(new AgentUiEvent(e)));
        _session.Interaction = this;

        _screen.Write(Ansi.BracketedPasteOff);
        Commit(Banner.Render(_theme, _session, _screen.ContentWidth), spacing: false);
        foreach (var err in _runtime.Loader.Errors.Concat(_runtime.Extensions.Errors))
            Commit(_blocks.Notice(NoticeLevel.Warning, err, _screen.ContentWidth));
        if (_session.Mode == PermissionMode.BypassPermissions)
            Commit(_blocks.Notice(NoticeLevel.Warning, "Bypass permissions mode is ON: DotCode will run every tool without asking. Use only in a sandbox.", _screen.ContentWidth));
        if (_session.Messages.Count > 0) ReplayTranscript();

        _ = Task.Run(McpStatusAsync);
        StartInputThread();
        StartTicker();
        await _session.FireSessionStartAsync(_session.Messages.Count > 0 ? "resume" : "startup", CancellationToken.None).ConfigureAwait(false);
        RefreshStatusLine();

        if (_launch.InitialPrompt is { Length: > 0 } initial) Submit(initial);
        Render();

        while (!_exit)
        {
            var ev = await _events.Reader.ReadAsync().ConfigureAwait(false);
            Handle(ev);
            // Drain bursts (streaming deltas, pasted keys) before rendering once.
            var drained = 0;
            while (!_exit && drained++ < 500 && _events.Reader.TryRead(out var more)) Handle(more);
            if (_dirty && !_exit) Render();
        }

        _turnCts?.Cancel();
        _screen.ClearLive();
        PrintExitSummary();
        await _session.DisposeAsync().ConfigureAwait(false);
        await _runtime.DisposeAsync().ConfigureAwait(false);
        Console.TreatControlCAsInput = false;
        return _exitCode;
    }

    private AgentSession OpenSession()
    {
        if (_launch.Continue && SessionStore.List(_runtime.Cwd, 1).FirstOrDefault() is { } latest)
            return _runtime.ResumeSession(latest.Path, _launch.ForkSession);
        if (_launch.Resume && _launch.ResumeId is { } id)
        {
            var path = SessionStore.FindPath(_runtime.Cwd, id) ?? throw new InvalidOperationException($"No session found with id {id}");
            return _runtime.ResumeSession(path, _launch.ForkSession);
        }
        var session = _runtime.CreateSession();
        if (_launch.Resume) Post(new ActionUiEvent(OpenResumePicker));
        return session;
    }

    private void Post(UiEvent e) => _events.Writer.TryWrite(e);

    // ------------------------------------------------------------------ input thread & ticker

    private void StartInputThread()
    {
        var thread = new Thread(() =>
        {
            while (!_exit)
            {
                ConsoleKeyInfo key;
                try { key = Console.ReadKey(intercept: true); }
                catch (InvalidOperationException) { break; }
                var burst = false;
                try { burst = Console.KeyAvailable; } catch (InvalidOperationException) { }
                Post(new KeyUiEvent(key, burst));
            }
        }) { IsBackground = true, Name = "dotcode-input" };
        thread.Start();
    }

    private void StartTicker()
    {
        _ = Task.Run(async () =>
        {
            while (!_exit)
            {
                var animating = (_busy || _pending.Count > 0) && _modal is null;
                await Task.Delay(animating ? 90 : 400).ConfigureAwait(false);
                Post(new TickUiEvent());
            }
        });
    }

    // ------------------------------------------------------------------ event handling

    private void Handle(UiEvent ev)
    {
        switch (ev)
        {
            case KeyUiEvent k:
                HandleKey(k.Key, k.Burst);
                _dirty = true;
                break;
            case AgentUiEvent a:
                ApplyAgentEvent(a.Event);
                break;
            case TickUiEvent:
                if (_screen.RefreshSize()) _dirty = true;
                // Animate only when something is visibly in progress (not while a dialog waits for the user).
                if ((_busy || _pending.Count > 0) && _modal is null) { _frame++; _dirty = true; }
                // Like Claude Code, the whimsical verb changes as the work goes on.
                if (_busy && (DateTime.UtcNow - _verbChangedAt).TotalSeconds > 15) RotateVerb();
                if (_flash is not null && DateTime.UtcNow > _flashUntil) { _flash = null; _dirty = true; }
                break;
            case TurnDoneUiEvent d:
                OnTurnDone(d.Result, d.Error);
                _dirty = true;
                break;
            case ModalUiEvent m:
                // A request from the agent takes over from the transcript viewer so it is never hidden.
                if (_modal is TranscriptModal) _modal = null;
                if (_modal is null) _modal = m.Modal; else _modalQueue.Enqueue(m.Modal);
                Notify(UiText.Current.NotifyWaiting);
                _dirty = true;
                break;
            case ActionUiEvent act:
                act.Action();
                _dirty = true;
                break;
        }
    }

    private void Flash(string text, double seconds = 2.5)
    {
        _flash = text;
        _flashUntil = DateTime.UtcNow.AddSeconds(seconds);
    }

    private void ApplyAgentEvent(AgentEvent e)
    {
        var w = _screen.ContentWidth;
        if (e.ParentToolUseId is { } parent)
        {
            // Subagent activity is summarized under its Agent tool block.
            var owner = _pending.FirstOrDefault(p => p.Id == parent);
            if (owner is null) return;
            switch (e)
            {
                case ToolStartedEvent ts: owner.Children.Add((ts.Name, ts.DisplayName, false, false)); break;
                case ToolCompletedEvent tc:
                    var idx = owner.Children.FindLastIndex(c => !c.Done && c.Name == tc.Name);
                    if (idx >= 0) owner.Children[idx] = owner.Children[idx] with { Done = true, Error = tc.IsError };
                    break;
                case AssistantTextDeltaEvent td: _streamChars += td.Text.Length; _charsSinceUsage += td.Text.Length; break;
                case AssistantThinkingDeltaEvent sth: _streamChars += sth.Text.Length; _charsSinceUsage += sth.Text.Length; break;
                case UsageUpdatedEvent su: _turnTokens += su.TurnUsage.OutputTokens; _charsSinceUsage = 0; break;
            }
            _dirty = true;
            return;
        }

        switch (e)
        {
            case AssistantTextDeltaEvent td:
                _stream.Append(td.Text);
                _streamChars += td.Text.Length;
                _charsSinceUsage += td.Text.Length;
                _thinking = false;
                _dirty = true;
                break;
            case AssistantThinkingDeltaEvent th:
                _thinkingText.Append(th.Text);
                _streamChars += th.Text.Length;
                _charsSinceUsage += th.Text.Length;
                _thinking = true;
                _dirty = true;
                break;
            case AssistantMessageEvent am:
                if (am.Thinking is { Length: > 0 } thinking && (_verbose || _runtime.Settings.Tui?.ShowThinking == true))
                    Commit(_blocks.Thinking(thinking, w, expanded: true));
                if (am.Text.Trim().Length > 0) Commit(_blocks.Assistant(am.Text, w));
                _stream.Clear();
                _thinkingText.Clear();
                _thinking = false;
                break;
            case ToolStartedEvent ts:
                _pending.Add(new PendingTool { Id = ts.ToolUseId, Name = ts.Name, DisplayName = ts.DisplayName });
                _dirty = true;
                break;
            case ToolProgressEvent tp:
                if (_pending.FirstOrDefault(p => p.Id == tp.ToolUseId) is { } pt) pt.Progress = tp.Text;
                _dirty = true;
                break;
            case ToolCompletedEvent tc:
                if (_pending.FirstOrDefault(p => p.Id == tc.ToolUseId) is { } done) done.Completed = tc;
                else _pending.Add(new PendingTool { Id = tc.ToolUseId, Name = tc.Name, DisplayName = tc.Name, Completed = tc });
                if (tc.Diff is { } diff)
                {
                    var (a, r) = Engine.Util.UnifiedDiff.Count(diff);
                    _linesAdded += a;
                    _linesRemoved += r;
                }
                FlushCompletedTools();
                break;
            case SubagentStartedEvent ss:
                if (_pending.FirstOrDefault(p => p.Id == ss.ToolUseId) is { } sp) sp.IsSubagent = true;
                break;
            case SubagentCompletedEvent sc:
                if (_pending.FirstOrDefault(p => p.Id == sc.ToolUseId) is { } sd) sd.SubagentDone = sc;
                break;
            case TodoUpdatedEvent:
                _dirty = true;
                break;
            case ContextCompactedEvent cc:
                Commit([_theme.Dim(_theme.Glyphs.Star + $" Conversation compacted ({Engine.Util.TextUtil.FormatTokens(cc.TokensBefore)} → {Engine.Util.TextUtil.FormatTokens(cc.TokensAfter)} tokens){(cc.Automatic ? " · automatically" : "")}")]);
                break;
            case UsageUpdatedEvent uu:
                _turnTokens += uu.TurnUsage.OutputTokens;
                _charsSinceUsage = 0;
                RotateVerb();
                _dirty = true;
                break;
            case RetryEvent re:
                _retryDetail = $"retrying in {re.DelaySeconds:0}s · attempt {re.Attempt}/{re.MaxAttempts}";
                _dirty = true;
                break;
            case ModelFallbackEvent mf:
                Commit(_blocks.Notice(NoticeLevel.Warning, $"{mf.From} unavailable ({mf.Reason}) — switched to {mf.To}", w));
                break;
            case NoticeEvent n:
                Commit(_blocks.Notice(n.Level, n.Text, w));
                break;
            case ErrorEvent err:
                Commit(_blocks.ErrorBlock($"API Error: {err.Message}", w));
                break;
            case ModeChangedEvent:
                RefreshStatusLine();
                _dirty = true;
                break;
            case ModelChangedEvent:
                RefreshStatusLine();
                _dirty = true;
                break;
        }
    }

    /// <summary>Commits finished tool blocks in call order (a slower earlier call holds back later ones).</summary>
    private void FlushCompletedTools()
    {
        var w = _screen.ContentWidth;
        while (_pending.Count > 0 && _pending[0].Completed is { } c)
        {
            var p = _pending[0];
            _pending.RemoveAt(0);
            Commit(RenderToolBlock(p, c, w));
        }
        _dirty = true;
    }

    private List<string> RenderToolBlock(PendingTool p, ToolCompletedEvent c, int w)
    {
        if (p.Name == "TodoWrite" && !c.IsError) return _blocks.Todos(_session.Todos, w);
        var state = c.Rejected ? ToolState.Rejected : c.IsError ? ToolState.Error : ToolState.Success;
        var lines = new List<string> { _blocks.ToolHeader(p.DisplayName, state, w) };
        if (p.IsSubagent && !c.IsError)
        {
            var sd = p.SubagentDone;
            var summary = sd is null ? c.Summary : $"Done ({Engine.Util.TextUtil.Plural(sd.ToolUses, "tool use")} · {Engine.Util.TextUtil.FormatTokens(sd.Usage.TotalTokens)} tokens · {Engine.Util.TextUtil.FormatDuration(TimeSpan.FromMilliseconds(sd.DurationMs))})";
            lines.Add(_blocks.ResultPrefix(true) + _theme.Dim(summary));
            if (_verbose && c.Output.Length > 0)
                foreach (var l in Markdown.Render(c.Output, w - 5, _theme)) lines.Add("     " + l);
            return lines;
        }
        lines.AddRange(_blocks.ToolResultLines(c, w, _verbose));
        return lines;
    }

    /// <summary>Applies UI language and vim mode from settings (startup and after /config changes).</summary>
    private void ApplyUiSettings()
    {
        var tui = _runtime.Settings.Tui;
        UiText.Current = UiText.For(tui?.Language);
        if (tui?.Vim == true) _vim ??= new VimMode();
        else _vim = null;
    }

    /// <summary>Terminal notification (bell by default; OSC 9 / OSC 777 desktop notifications where supported).</summary>
    private void Notify(string message)
    {
        switch ((_runtime.Settings.Tui?.Notifications ?? "bell").ToLowerInvariant())
        {
            case "off" or "none" or "false": return;
            case "osc9": _screen.Write($"\u001b]9;{message}\u0007"); return;
            case "osc777": _screen.Write($"\u001b]777;notify;DotCode;{message}\u0007"); return;
            default: _screen.Write("\u0007"); return;
        }
    }

    private void Commit(IEnumerable<string> lines, bool spacing = true)
    {
        var list = lines.ToList();
        if (list.Count == 0) return;
        if (_modal is TranscriptModal)
        {
            _deferredCommits.Add((list, spacing));
            _dirty = true;
            return;
        }
        if (spacing && _anyCommitted) list.Insert(0, "");
        _anyCommitted = true;
        _screen.Commit(list);
        _dirty = true;
    }

    // ------------------------------------------------------------------ submitting prompts & turns

    private void Submit(string raw)
    {
        var text = raw.TrimEnd();
        if (text.Trim().Length == 0) return;
        AddHistory(text);
        var w = _screen.ContentWidth;

        if (_busy)
        {
            _queued.Enqueue(text);
            return;
        }

        if (text.StartsWith('!') && text.Length > 1)
        {
            var command = text[1..].Trim();
            Commit(_blocks.BashInput(command, w));
            StartTurn(async ct => { await _session.RunUserShellAsync(command, ct).ConfigureAwait(false); return (TurnResult?)null; }, showSpinner: true);
            return;
        }
        if (text.StartsWith('#') && text.Length > 1 && !text.StartsWith("##", StringComparison.Ordinal))
        {
            var path = Engine.Context.MemoryLoader.AppendMemory(_runtime.ProjectRoot, text[1..], user: false);
            Commit(_blocks.User(text, w));
            Commit(_blocks.Notice(NoticeLevel.Info, $"Saved to memory: {DotCodePaths.Display(path, _runtime.Cwd)}", w));
            _runtime.Reload();
            return;
        }
        if (CommandExpander.TryParse(text, out var name, out var args))
        {
            if (TryBuiltinCommand(name, args, text)) return;
            if (!CommandExpander.IsPromptCommand(_runtime, name))
            {
                Commit(_blocks.User(text, w));
                Commit(_blocks.Notice(NoticeLevel.Error, $"Unknown command: /{name}. Type /help for available commands.", w));
                return;
            }
            Commit(_blocks.User(text, w));
            StartTurn(async ct =>
            {
                var expanded = await CommandExpander.ExpandAsync(_session, text, ct).ConfigureAwait(false);
                var previous = expanded.ModelOverride is { } m ? _session.Model.Qualified : null;
                if (expanded.ModelOverride is { } mo) { try { _session.SetModel(mo); } catch (InvalidOperationException) { } }
                try { return (TurnResult?)await _session.RunTurnAsync(expanded.Prompt, null, ct).ConfigureAwait(false); }
                finally { if (previous is not null) _session.SetModel(previous); }
            }, showSpinner: true);
            return;
        }

        Commit(_blocks.User(text, w));
        StartTurn(ct => _session.RunTurnAsync(text, null, ct));
    }

    private void StartTurn(Func<CancellationToken, Task<TurnResult>> run) =>
        StartTurn(async ct => (TurnResult?)await run(ct).ConfigureAwait(false), showSpinner: true);

    private void StartTurn(Func<CancellationToken, Task<TurnResult?>> run, bool showSpinner)
    {
        _busy = true;
        _turnCts = new CancellationTokenSource();
        _turnStart = DateTime.UtcNow;
        var verbs = _runtime.Settings.SpinnerVerbs is { Count: > 0 } custom ? custom.ToArray() : SpinnerLine.Verbs;
        _verbs = verbs;
        _verb = verbs[Random.Shared.Next(verbs.Length)];
        _verbChangedAt = DateTime.UtcNow;
        _charsSinceUsage = 0;
        _tip = UiText.Current.Tips[Random.Shared.Next(UiText.Current.Tips.Length)];
        _streamChars = 0;
        _turnTokens = 0;
        _retryDetail = null;
        var ct = _turnCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await run(ct).ConfigureAwait(false);
                Post(new TurnDoneUiEvent(result, null));
            }
            catch (OperationCanceledException) { Post(new TurnDoneUiEvent(null, null)); }
            catch (Exception ex) { Post(new TurnDoneUiEvent(null, ex)); }
        });
    }

    private void RotateVerb()
    {
        if (_verbs.Length < 2) return;
        string next;
        do next = _verbs[Random.Shared.Next(_verbs.Length)]; while (next == _verb);
        _verb = next;
        _verbChangedAt = DateTime.UtcNow;
        _dirty = true;
    }

    private void OnTurnDone(TurnResult? result, Exception? error)
    {
        var w = _screen.ContentWidth;
        // Anything still streaming or pending is committed as-is.
        if (_stream.Length > 0) { Commit(_blocks.Assistant(_stream.ToString(), w)); _stream.Clear(); }
        foreach (var p in _pending.ToList())
        {
            var c = p.Completed ?? new ToolCompletedEvent(p.Id, p.Name, true, "Interrupted", "Interrupted by user");
            Commit(RenderToolBlock(p, c, w));
        }
        _pending.Clear();
        _thinkingText.Clear();
        _busy = false;
        _retryDetail = null;
        CloseModals();

        var interrupted = result is { StopReason: StopReason.Aborted } || result is null && _turnCts?.IsCancellationRequested == true;
        if (error is not null) Commit(_blocks.ErrorBlock($"Error: {error.Message}", w));
        else if (interrupted)
            Commit([_blocks.ResultPrefix(true) + _theme.C(UiText.Current.Interrupted, _theme.Error) + _theme.Dim(UiText.Current.WhatInstead)]);
        // Long turns ring the terminal so the user can switch away and come back.
        if (!interrupted && _queued.Count == 0 && (DateTime.UtcNow - _turnStart).TotalSeconds >= NotifyAfterSeconds)
            Notify(UiText.Current.NotifyDone);

        _turnCts?.Dispose();
        _turnCts = null;
        RefreshStatusLine();
        if (_queued.Count > 0) Submit(_queued.Dequeue());
    }

    private void Interrupt()
    {
        if (!_busy) return;
        _turnCts?.Cancel();
        CloseModals();
    }

    private const double NotifyAfterSeconds = 15;

    /// <summary>Cancels pending agent dialogs (the transcript viewer, a user-opened view, stays open).</summary>
    private void CloseModals()
    {
        if (_modal is not TranscriptModal)
        {
            _modal?.Cancel();
            _modal = null;
        }
        while (_modalQueue.TryDequeue(out var m)) m.Cancel();
    }

    // ------------------------------------------------------------------ IInteractionHandler (called from agent threads)

    public async ValueTask<PermissionDecision> RequestPermissionAsync(PermissionRequest request, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<PermissionDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(new ModalUiEvent(new PermissionModal(request, tcs, _runtime.ProjectRoot)));
        using var reg = ct.Register(() => tcs.TrySetResult(PermissionDecision.Deny()));
        return await tcs.Task.ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<UserQuestionAnswer>?> AskQuestionsAsync(IReadOnlyList<UserQuestion> questions, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<IReadOnlyList<UserQuestionAnswer>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(new ModalUiEvent(new QuestionModal(questions, tcs)));
        using var reg = ct.Register(() => tcs.TrySetResult([]));
        return await tcs.Task.ConfigureAwait(false);
    }

    public async ValueTask<PlanDecision> ReviewPlanAsync(string plan, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<PlanDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(new ModalUiEvent(new PlanModal(plan, tcs)));
        using var reg = ct.Register(() => tcs.TrySetResult(new PlanDecision(PlanApproval.Reject)));
        return await tcs.Task.ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ history, transcript replay, misc

    private void LoadHistory()
    {
        try
        {
            if (!File.Exists(DotCodePaths.HistoryFile)) return;
            foreach (var line in File.ReadLines(DotCodePaths.HistoryFile).TakeLast(500))
            {
                try
                {
                    var el = DotCodeJson.Parse(line);
                    if (el.GetString("project") == _options.Cwd && el.GetString("display") is { } d) _history.Add(d);
                }
                catch (JsonException) { }
            }
        }
        catch (IOException) { }
    }

    private void AddHistory(string text)
    {
        if (_history.Count > 0 && _history[^1] == text) return;
        _history.Add(text);
        try
        {
            Directory.CreateDirectory(DotCodePaths.UserDir);
            var json = DotCodeJson.Build(w =>
            {
                w.WriteStartObject();
                w.WriteString("display", text);
                w.WriteString("project", _options.Cwd);
                w.WriteNumber("timestamp", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                w.WriteEndObject();
            });
            File.AppendAllText(DotCodePaths.HistoryFile, json.GetRawText() + "\n");
        }
        catch (IOException) { }
    }

    private void ReplayTranscript()
    {
        var w = _screen.ContentWidth;
        var results = new Dictionary<string, ToolResultPart>();
        foreach (var m in _session.Messages)
            foreach (var r in m.ToolResults) results[r.ToolUseId] = r;
        Commit([_theme.Dim($"Resumed session {_session.Id[..8]}{(_session.Title is { } t ? " · " + t : "")} · {_session.Messages.Count} messages")]);
        foreach (var m in _session.Messages)
        {
            if (m.Role == Role.User)
            {
                if (m.IsMeta || m.HasToolResults) continue;
                var text = m.Content.OfType<TextPart>().FirstOrDefault()?.Text ?? "";
                if (text.StartsWith("<command-name>", StringComparison.Ordinal)) text = text[14..text.IndexOf('<', 14)];
                Commit(_blocks.User(text, w));
                continue;
            }
            if (m.Text.Trim().Length > 0) Commit(_blocks.Assistant(m.Text, w));
            foreach (var tu in m.ToolUses)
            {
                var tool = _session.FindTool(tu.Name);
                string display;
                try { display = tool?.DisplayName(tu.Input, _session) ?? tu.Name; } catch { display = tu.Name; }
                results.TryGetValue(tu.Id, out var res);
                var lines = new List<string> { _blocks.ToolHeader(display, res?.IsError == true ? ToolState.Error : ToolState.Success, w) };
                if (res is not null) lines.Add(_blocks.ResultPrefix(true) + _theme.Dim(TextUtilLocal.FirstLine(res.TextContent.Trim(), w - 6)));
                Commit(lines);
            }
        }
    }

    private async Task McpStatusAsync()
    {
        try { await _runtime.McpReady.ConfigureAwait(false); } catch { return; }
        var failed = _runtime.Mcp.Servers.Where(s => s.Status == Engine.Mcp.McpServerStatus.Failed).ToList();
        if (failed.Count > 0)
            Post(new ActionUiEvent(() => Commit(_blocks.Notice(NoticeLevel.Warning,
                $"{failed.Count} MCP server{(failed.Count > 1 ? "s" : "")} failed to connect ({string.Join(", ", failed.Select(f => f.Name))}) · /mcp for details", _screen.ContentWidth))));
    }

    private void RefreshStatusLine()
    {
        if (_runtime.Settings.StatusLine?.Command is not { Length: > 0 } command) return;
        var payload = DotCodeJson.Build(w =>
        {
            w.WriteStartObject();
            w.WriteString("session_id", _session.Id);
            w.WriteString("cwd", _runtime.Cwd);
            w.WriteStartObject("model"); w.WriteString("id", _session.Model.Qualified); w.WriteString("display_name", _session.Model.Model); w.WriteEndObject();
            w.WriteStartObject("workspace"); w.WriteString("current_dir", _runtime.Cwd); w.WriteString("project_dir", _runtime.ProjectRoot); w.WriteEndObject();
            w.WriteStartObject("cost"); w.WriteNumber("total_cost_usd", _session.TotalCostUsd); w.WriteEndObject();
            w.WriteString("permission_mode", _session.Mode.ToSetting());
            w.WriteEndObject();
        }).GetRawText();
        _ = Task.Run(async () =>
        {
            var psi = new ProcessStartInfo(Engine.Util.ProcessRunner.BashPath ?? (OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh")) { WorkingDirectory = _runtime.Cwd };
            psi.ArgumentList.Add(Engine.Util.ProcessRunner.BashPath is null && OperatingSystem.IsWindows() ? "/c" : "-c");
            psi.ArgumentList.Add(command);
            var result = await Engine.Util.ProcessRunner.RunAsync(psi, TimeSpan.FromSeconds(5), CancellationToken.None, stdin: payload).ConfigureAwait(false);
            var line = result.Output.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.TrimEnd();
            Post(new ActionUiEvent(() => _statusLine = line));
        });
    }

    private void PrintExitSummary()
    {
        var t = _theme;
        var ui = UiText.Current;
        var sb = new StringBuilder();
        if (_session.ModelCalls == 0) return;
        sb.Append(t.Dim(ui.TotalCost)).Append($"${_session.TotalCostUsd:0.0000}").Append("\r\n");
        sb.Append(t.Dim(ui.DurationApi)).Append(Engine.Util.TextUtil.FormatDuration(_session.ApiDuration)).Append("\r\n");
        sb.Append(t.Dim(ui.DurationWall)).Append(Engine.Util.TextUtil.FormatDuration(_wall.Elapsed)).Append("\r\n");
        sb.Append(t.Dim(ui.CodeChanges)).Append(string.Format(ui.LinesChanged, _linesAdded, _linesRemoved)).Append("\r\n");
        sb.Append(t.Dim(ui.UsageByModel)).Append("\r\n");
        foreach (var (model, (usage, cost)) in _session.UsageByModel)
            sb.Append($"    {model}:  {Engine.Util.TextUtil.FormatTokens(usage.InputTokens)} input, {Engine.Util.TextUtil.FormatTokens(usage.OutputTokens)} output, {Engine.Util.TextUtil.FormatTokens(usage.CacheReadTokens)} cache read, {Engine.Util.TextUtil.FormatTokens(usage.CacheWriteTokens)} cache write (${cost:0.0000})").Append("\r\n");
        sb.Append(t.Dim($"{ui.ResumeWith} dotcode --resume {_session.Id}")).Append("\r\n");
        _screen.Write(sb.ToString());
    }
}
