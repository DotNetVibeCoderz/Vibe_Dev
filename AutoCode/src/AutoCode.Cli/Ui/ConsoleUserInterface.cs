// Auto Code — Gravicode Studios (Kang Fadhil)

using AutoCode.Core.Abstractions;
using AutoCode.Core.Permissions;
using Spectre.Console;

namespace AutoCode.Cli.Ui;

/// <summary>
/// The interactive terminal surface.
///
/// EN: the design idea is that a turn should read as one connected trace rather than as scattered
/// lines. Tool calls hang off a vertical spine with their durations right-aligned, so the shape of
/// what the agent did — how many steps, which ones were slow — is legible at a glance without
/// reading a word. Everything else stays deliberately quiet: colour is reserved for state, and
/// structure does the rest.
/// ID: satu giliran harus terbaca sebagai satu rangkaian, bukan baris-baris terpisah. Pemanggilan
/// tool menggantung pada batang vertikal dengan durasi rata kanan, sehingga bentuk pekerjaan agent
/// terbaca sekilas. Selebihnya sengaja tenang: warna hanya untuk status, sisanya dibawa struktur.
/// </summary>
public sealed class ConsoleUserInterface : IAgentUserInterface
{
    private readonly Theme _theme;
    private readonly Glyphs _glyphs;
    private readonly bool _showThinking;
    private readonly MarkdownStream _markdown;

    private readonly Dictionary<string, string> _pendingCalls = new(StringComparer.Ordinal);
    private readonly ThinkingIndicator _waiting;
    private bool _thinkingOpen;
    private bool _spineOpen;

    public ConsoleUserInterface(Theme theme, bool showThinking)
    {
        _theme = theme;
        _glyphs = Glyphs.Detect();
        _showThinking = showThinking;
        _markdown = new MarkdownStream(theme, _glyphs);
        _waiting = new ThinkingIndicator(theme);
    }

    public ValueTask EmitAsync(AgentEvent evt, CancellationToken cancellationToken)
    {
        // The indicator covers exactly one interval: request sent, nothing back yet. The first
        // sign of life from the model — prose, reasoning, or a tool call — retires it.
        if (evt is TurnStartedEvent)
            _waiting.Start();
        else if (evt is AssistantTextEvent or AssistantThinkingEvent or ToolCallStartedEvent
                     or ToolCallCompletedEvent or ToolCallDeniedEvent or TurnCompletedEvent
                     or ErrorEvent or NoticeEvent)
        {
            _waiting.Stop();
        }

        switch (evt)
        {
            case AssistantThinkingEvent thinking when _showThinking:
                RenderThinking(thinking.Text);
                break;

            case AssistantThinkingEvent:
                break;

            case AssistantTextEvent { IsFinal: true }:
                _markdown.Flush();
                _markdown.Reset();
                break;

            case AssistantTextEvent text:
                CloseThinking();
                CloseSpine();
                _markdown.Append(text.Text);
                break;

            case ToolCallStartedEvent tool:
                CloseThinking();
                _markdown.Flush();
                OpenSpine();
                _pendingCalls[tool.CallId] = tool.Summary;
                break;

            case ToolCallCompletedEvent completed:
                RenderToolCall(completed);
                break;

            case ToolCallDeniedEvent denied:
                RenderDenial(denied);
                break;

            case TodoUpdatedEvent todos:
                RenderTodos(todos.Items);
                break;

            case SubagentStartedEvent started:
                CloseThinking();
                _markdown.Flush();
                OpenSpine();
                AnsiConsole.MarkupLine(
                    $"  [{_theme.Accent}]{_glyphs.Subagent}[/] [{_theme.Strong}]{Escape(started.AgentName)}[/] " +
                    $"[{_theme.Faint}]{Escape(started.Description)}[/]");
                break;

            case SubagentCompletedEvent completed:
                AnsiConsole.MarkupLine(
                    $"  [{_theme.Faint}]{_glyphs.TraceMid}[/] [{_theme.Muted}]{Escape(completed.AgentName)} finished[/] " +
                    $"[{_theme.Faint}]{Duration(completed.ElapsedMs)}[/]");
                break;

            case CompactionEvent compaction:
                CloseSpine();
                AnsiConsole.MarkupLine(
                    $"  [{_theme.Faint}]{_glyphs.Compact} context compacted · {compaction.MessagesBefore} → " +
                    $"{compaction.MessagesAfter} messages[/]");
                break;

            case NoticeEvent notice:
                CloseSpine();
                var colour = notice.Severity switch
                {
                    NoticeSeverity.Warning => _theme.Warning,
                    NoticeSeverity.Success => _theme.Success,
                    _ => _theme.Muted,
                };
                AnsiConsole.MarkupLine($"  [{colour}]{Escape(notice.Message)}[/]");
                break;

            case ErrorEvent error:
                CloseSpine();
                AnsiConsole.MarkupLine($"  [{_theme.Error}]{_glyphs.Denied} {Escape(error.Message)}[/]");
                if (error.Detail is { Length: > 0 } detail)
                    AnsiConsole.MarkupLine($"    [{_theme.Faint}]{Escape(detail)}[/]");
                break;

            case TurnCompletedEvent:
                _markdown.Flush();
                _markdown.Reset();
                CloseSpine();
                break;
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// One tool call, drawn as a single line: what was called on the left, how long it took on the
    /// right, and its result indented beneath. Duration is shown because it is the only way to
    /// learn which parts of a run are actually expensive.
    /// </summary>
    private void RenderToolCall(ToolCallCompletedEvent completed)
    {
        _pendingCalls.Remove(completed.CallId, out var summary);
        summary ??= completed.ToolName;

        var glyph = completed.Success ? _glyphs.ToolCall : _glyphs.Denied;
        var glyphColour = completed.Success ? _theme.Accent : _theme.Error;

        AnsiConsole.MarkupLine(
            $"  [{glyphColour}]{glyph}[/] [{_theme.Strong}]{Escape(summary)}[/]" +
            $"  [{_theme.Faint}]{Duration(completed.ElapsedMs)}[/]");

        var body = FirstLine(completed.Display);

        if (body.Length > 0 && !body.Equals(summary, StringComparison.Ordinal))
        {
            var colour = completed.Success ? _theme.Muted : _theme.Error;
            AnsiConsole.MarkupLine($"  [{_theme.Faint}]{_glyphs.TraceMid}[/] [{colour}]{Escape(body)}[/]");
        }
    }

    private void RenderDenial(ToolCallDeniedEvent denied)
    {
        _pendingCalls.Remove(denied.CallId);
        OpenSpine();

        AnsiConsole.MarkupLine(
            $"  [{_theme.Error}]{_glyphs.Denied}[/] [{_theme.Strong}]{Escape(denied.ToolName)}[/]");
        AnsiConsole.MarkupLine(
            $"  [{_theme.Faint}]{_glyphs.TraceMid}[/] [{_theme.Muted}]{Escape(FirstLine(denied.Reason))}[/]");
    }

    /// <summary>
    /// Reasoning is written inline and dim rather than in a panel: it is an aside the user may
    /// glance at, and boxing it would give it more weight than the answer it precedes.
    /// </summary>
    private void RenderThinking(string text)
    {
        CloseSpine();

        if (!_thinkingOpen)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.Markup($"  [{_theme.Thinking}]{_glyphs.Thinking} [/]");
            _thinkingOpen = true;
        }

        AnsiConsole.Markup($"[{_theme.Thinking}]{Escape(text.ReplaceLineEndings(" "))}[/]");
    }

    private void RenderTodos(IReadOnlyList<TodoItem> items)
    {
        CloseSpine();

        if (items.Count == 0)
            return;

        AnsiConsole.WriteLine();

        foreach (var item in items)
        {
            var (glyph, colour) = item.Status switch
            {
                TodoStatus.Completed => (_glyphs.TodoDone, _theme.Success),
                TodoStatus.InProgress => (_glyphs.TodoActive, _theme.Accent),
                _ => (_glyphs.TodoPending, _theme.Faint),
            };

            var text = item.Status == TodoStatus.InProgress && item.ActiveForm is { Length: > 0 }
                ? item.ActiveForm
                : item.Content;

            // Completed work is struck through: the list is a record of progress, and finished
            // items should recede rather than compete with what is still outstanding.
            var body = item.Status == TodoStatus.Completed
                ? $"[{_theme.Faint} strikethrough]{Escape(text)}[/]"
                : $"[{_theme.Muted}]{Escape(text)}[/]";

            AnsiConsole.MarkupLine($"  [{colour}]{glyph}[/] {body}");
        }

        AnsiConsole.WriteLine();
    }

    /// <summary>Stops any running indicator; used when the REPL takes the screen back.</summary>
    public void StopWaiting() => _waiting.Stop();

    public async ValueTask<PermissionDecision> RequestPermissionAsync(
        PermissionRequest request,
        CancellationToken cancellationToken)
    {
        _waiting.Stop();
        CloseThinking();
        CloseSpine();
        _markdown.Flush();

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(
            $"  [{_theme.Warning}]{DescribeCapability(request.Capability)}[/]  " +
            $"[{_theme.Strong}]{Escape(request.Summary)}[/]");

        RenderPermissionDetail(request);

        AnsiConsole.WriteLine();

        const string allowOnce = "Yes";
        var allowAlways = $"Yes, and don't ask again for {request.SuggestedRule ?? request.ToolName}";
        const string deny = "No — tell Auto Code what to do instead";
        const string abort = "No — stop this turn";

        var choice = await AnsiConsole.PromptAsync(
            new SelectionPrompt<string>()
                .Title($"  [{_theme.Accent}]Allow this?[/]")
                .HighlightStyle(new Style(_theme.AccentColor, decoration: Decoration.Bold))
                .AddChoices(allowOnce, allowAlways, deny, abort),
            cancellationToken).ConfigureAwait(false);

        if (choice == allowOnce)
            return PermissionDecision.Allow;

        if (choice == allowAlways)
            return new PermissionDecision(PermissionOutcome.AllowAlways, null, request.SuggestedRule);

        if (choice == abort)
            return new PermissionDecision(PermissionOutcome.Abort, "The user stopped the turn.");

        var reason = await AnsiConsole.PromptAsync(
            new TextPrompt<string>($"  [{_theme.Accent}]What should it do instead?[/]").AllowEmpty(),
            cancellationToken).ConfigureAwait(false);

        return new PermissionDecision(
            PermissionOutcome.Deny,
            string.IsNullOrWhiteSpace(reason) ? "The user declined." : reason);
    }

    /// <summary>
    /// Shows the pending call in whatever form makes its risk legible: a diff for an edit, the
    /// command for a shell call, a preview for a write.
    /// </summary>
    private void RenderPermissionDetail(PermissionRequest request)
    {
        if (DiffRenderer.TryBuild(_theme, _glyphs, request.Arguments) is { } diff)
        {
            AnsiConsole.Write(new Padder(diff, new Padding(4, 0, 0, 0)));
            return;
        }

        if (request.Detail is not { Length: > 0 } detail)
            return;

        foreach (var line in Clip(detail, 12))
            AnsiConsole.MarkupLine($"    [{_theme.Code}]{Escape(line)}[/]");
    }

    /// <summary>Opens a blank line before a run of tool calls, so turns are visually separable.</summary>
    private void OpenSpine()
    {
        if (_spineOpen)
            return;

        AnsiConsole.WriteLine();
        _spineOpen = true;
    }

    private void CloseSpine() => _spineOpen = false;

    private void CloseThinking()
    {
        if (!_thinkingOpen)
            return;

        AnsiConsole.WriteLine();
        _thinkingOpen = false;
    }

    private static IEnumerable<string> Clip(string value, int maxLines)
    {
        var lines = value.ReplaceLineEndings("\n").Split('\n');

        foreach (var line in lines.Take(maxLines))
            yield return line.Length <= 110 ? line : line[..110] + "…";

        if (lines.Length > maxLines)
            yield return $"… {lines.Length - maxLines} more line(s)";
    }

    private static string DescribeCapability(ToolCapability capability)
    {
        if ((capability & ToolCapability.ExecutesCommands) != 0) return "run command";
        if ((capability & ToolCapability.WritesFiles) != 0) return "modify files";
        if ((capability & ToolCapability.AccessesNetwork) != 0) return "network";
        return "permission";
    }

    /// <summary>Milliseconds below a second, seconds above — precision nobody needs is noise.</summary>
    private static string Duration(long milliseconds) =>
        milliseconds >= 1000 ? $"{milliseconds / 1000.0:0.0}s" : $"{milliseconds}ms";

    private static string FirstLine(string value)
    {
        var newline = value.IndexOf('\n');
        var line = (newline < 0 ? value : value[..newline]).TrimEnd();
        return line.Length <= 150 ? line : line[..150] + "…";
    }

    private static string Escape(string value) => Markup.Escape(value);
}
