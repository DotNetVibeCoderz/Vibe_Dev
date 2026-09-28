using DotCode.Abstractions;
using DotCode.Engine.Permissions;
using DotCode.Tui.Components;
using DotCode.Tui.Rendering;

namespace DotCode.Tui;

internal sealed partial class App
{
    /// <summary>Builds and draws the live region: streaming text, running tools, spinner, queue, then the input box
    /// (or the active dialog), suggestions and footer.</summary>
    private void Render()
    {
        _dirty = false;
        var w = _screen.ContentWidth;
        var t = _theme;
        var lines = new List<string>();
        (int Row, int Col)? cursor = null;

        // Streaming assistant text (tail only; the full message is committed when complete).
        if (_stream.Length > 0)
        {
            var rendered = _blocks.Assistant(_stream.ToString(), w);
            var max = Math.Max(4, _screen.Height / 2);
            if (lines.Count == 0 && _anyCommitted) lines.Add("");
            lines.AddRange(rendered.Count > max ? rendered.Skip(rendered.Count - max) : rendered);
        }

        foreach (var p in _pending)
        {
            lines.Add("");
            if (p.Name == "TodoWrite" && p.Completed is null) { lines.Add(_blocks.ToolHeader("Update Todos", ToolState.Running, w, _frame / 4)); continue; }
            var state = p.Completed is null ? ToolState.Running : p.Completed.IsError ? ToolState.Error : ToolState.Success;
            lines.Add(_blocks.ToolHeader(p.DisplayName, state, w, _frame / 4));
            if (p.IsSubagent || p.Children.Count > 0)
            {
                var shown = p.Children.TakeLast(3).ToList();
                if (p.Children.Count > 3) lines.Add("     " + t.Dim($"+{p.Children.Count - 3} more tool uses"));
                foreach (var (i, c) in shown.Select((c, i) => (i, c)))
                {
                    var dot = c.Done ? t.C(t.Glyphs.Dot, c.Error ? t.Error : t.Success) : t.Dim(t.Glyphs.Dot);
                    lines.Add((i == 0 && p.Children.Count <= 3 ? _blocks.ResultPrefix(true) : "     ") + dot + " " + TextWidth.Truncate(t.Dim(c.Display), w - 9));
                }
                if (p.Children.Count == 0) lines.Add(_blocks.ResultPrefix(true) + t.Dim("Initializing…"));
            }
            else if (p.Progress is { Length: > 0 } progress)
            {
                var pl = progress.Split('\n').TakeLast(_verbose ? 10 : 3).ToList();
                for (var i = 0; i < pl.Count; i++) lines.Add(_blocks.ResultPrefix(i == 0) + t.Dim(TextWidth.Truncate(pl[i], w - 6)));
            }
            else if (p.Completed is null && (DateTime.UtcNow - p.Started).TotalSeconds > 1.5 && p.Name is "Bash" or "PowerShell")
            {
                lines.Add(_blocks.ResultPrefix(true) + t.Dim($"Running… ({(int)(DateTime.UtcNow - p.Started).TotalSeconds}s)"));
            }
        }

        if (_busy && _modal is null)
        {
            lines.Add("");
            var elapsed = DateTime.UtcNow - _turnStart;
            // Live count: reported output tokens of finished model calls (incl. subagents) + estimate for the call in flight.
            var tokens = _turnTokens + _charsSinceUsage / 4;
            lines.Add(SpinnerLine.Render(t, _frame, _verb, elapsed, tokens, _streamChars > 0, _thinking, _retryDetail, w));
            if (_showTodos && _session.Todos.Count > 0)
                foreach (var l in _blocks.Todos(_session.Todos, w).Skip(1)) lines.Add(l);
            else if (elapsed.TotalSeconds > 4 && _runtime.Settings.Tui?.ShowTips != false)
                lines.Add(_blocks.ResultPrefix(true) + t.Dim("Tip: " + _tip));
        }
        foreach (var q in _queued)
            lines.Add("  " + t.Dim(t.Glyphs.Prompt + " " + TextWidth.Truncate(q.Replace('\n', ' '), w - 14) + "  (queued)"));

        if (_modal is not null)
        {
            PlanModal.MaxPlanLines = _screen.Height - 16;
            lines.Add("");
            lines.AddRange(_modal.Render(t, _blocks, w));
        }
        else
        {
            if (!_busy && _stream.Length == 0 && _pending.Count == 0 || _busy) lines.Add("");
            cursor = RenderInput(lines, w);
            RenderBelowInput(lines, w);
        }

        _screen.Render(lines, _modal is null ? cursor : null);
    }

    private (int, int) RenderInput(List<string> lines, int w)
    {
        var t = _theme;
        var text = _input.Text;
        var bash = text.StartsWith('!');
        var memory = text.StartsWith('#');
        var borderColor = bash ? t.Bash : memory ? t.Suggestion : _session.Mode == PermissionMode.Plan ? t.PlanMode : t.PromptBorder;
        var boxed = _runtime.Settings.Tui?.Border is { } b && b != "lines" && _runtime.Settings.Tui?.Compact != true || _theme.Ascii;
        var innerWidth = boxed ? w - 4 : w;
        var promptGlyph = bash ? t.C("!", t.Bash) : memory ? t.C("#", t.Suggestion) : t.Glyphs.Prompt;

        // Character-wrap the input so the cursor maps exactly onto rows/columns.
        var rows = new List<(string Text, int Start)>();
        var logical = text.Split('\n');
        var offset = 0;
        var contentWidth = innerWidth - 2;
        foreach (var line in logical)
        {
            var start = 0;
            if (line.Length == 0) rows.Add(("", offset));
            while (start < line.Length)
            {
                var len = 0;
                var width = 0;
                while (start + len < line.Length)
                {
                    var cw = TextWidth.RuneWidth(line[start + len]);
                    if (width + cw > contentWidth) break;
                    width += cw;
                    len++;
                }
                if (len == 0) len = 1;
                rows.Add((line.Substring(start, len), offset + start));
                start += len;
            }
            offset += line.Length + 1;
        }

        // Cursor row/col.
        int cRow = 0, cCol = 0;
        for (var i = 0; i < rows.Count; i++)
        {
            var (rt, rs) = rows[i];
            if (_input.Cursor >= rs && _input.Cursor <= rs + rt.Length)
            {
                cRow = i;
                cCol = TextWidth.Of(text.Substring(rs, _input.Cursor - rs));
                if (cCol >= contentWidth && i + 1 < rows.Count) { cRow = i + 1; cCol = 0; }
            }
        }

        var top = lines.Count;
        if (boxed) lines.Add(t.C(t.Border.TopLeft + Blocks.Repeat(t.Border.Horizontal, w - 2) + t.Border.TopRight, borderColor));
        else lines.Add(t.C(Blocks.Repeat(t.Ascii ? "-" : "─", w), borderColor));
        for (var i = 0; i < rows.Count; i++)
        {
            var prefix = i == 0 ? promptGlyph + " " : "  ";
            var content = rows[i].Text;
            if (i == 0 && (bash || memory)) content = content.Length > 0 ? content[1..] : content;
            var line = prefix + content;
            if (text.Length == 0 && i == 0) line = prefix + t.Dim(Placeholder());
            lines.Add(boxed ? t.C(t.Border.Vertical, borderColor) + " " + TextWidth.Pad(line, innerWidth) + " " + t.C(t.Border.Vertical, borderColor) : line);
        }
        if (boxed) lines.Add(t.C(t.Border.BottomLeft + Blocks.Repeat(t.Border.Horizontal, w - 2) + t.Border.BottomRight, borderColor));
        else lines.Add(t.C(Blocks.Repeat(t.Ascii ? "-" : "─", w), borderColor));

        // In bash/memory mode the "!"/"#" is drawn as the prompt glyph, so shift the cursor accordingly.
        if ((bash || memory) && cRow == 0) cCol = Math.Max(0, cCol - 1);
        return (top + 1 + cRow, (boxed ? 2 : 0) + 2 + cCol);
    }

    private string Placeholder()
    {
        if (_busy) return "Type to queue a message for when DotCode finishes…";
        string[] ideas = ["Try \"explain this codebase\"", "Try \"write a test for <filepath>\"", "Try \"fix the failing build\"", "Try \"refactor <filepath> to be more readable\"", "Try \"how does <feature> work?\""];
        return ideas[Math.Abs(_session.Id.GetHashCode()) % ideas.Length];
    }

    private void RenderBelowInput(List<string> lines, int w)
    {
        var t = _theme;
        UpdateSuggestions();
        if (_suggestions.Count > 0)
        {
            const int page = 8;
            var start = Math.Clamp(_suggestIndex - page / 2, 0, Math.Max(0, _suggestions.Count - page));
            var labelWidth = Math.Min(32, _suggestions.Skip(start).Take(page).Max(s => TextWidth.Of(s.Label)) + 2);
            for (var i = start; i < Math.Min(_suggestions.Count, start + page); i++)
            {
                var (label, desc, _) = _suggestions[i];
                var l = "  " + TextWidth.Pad(label, labelWidth) + " " + TextWidth.Truncate(desc, Math.Max(10, w - labelWidth - 6));
                lines.Add(i == _suggestIndex ? t.C(TextWidth.Pad(l, w), t.Suggestion) : t.Dim(l));
            }
            return;
        }

        if (_showShortcuts)
        {
            string[][] cols =
            [
                ["! for bash mode", "/ for commands", "@ for file paths", "# to memorize"],
                ["double tap esc to clear input", "shift + tab to cycle modes", "ctrl + o for verbose output", "ctrl + t to show todos"],
                ["\\⏎ or shift + ⏎ for newline", "ctrl + _ / ctrl + z to undo", "ctrl + l to clear screen", "ctrl + c twice to exit"],
            ];
            var cw = Math.Max(20, (w - 2) / 3);
            for (var r = 0; r < 4; r++)
                lines.Add("  " + string.Concat(cols.Select(c => TextWidth.Pad(t.Dim(c[r]), cw))));
            return;
        }

        // Footer: mode / hint on the left, model and context on the right.
        string left;
        if (_flash is not null) left = t.Dim(_flash);
        else left = _session.Mode switch
        {
            PermissionMode.AcceptEdits => t.C($"{t.Glyphs.AcceptEdits} accept edits on", t.AutoAccept) + t.Dim(" (shift+tab to cycle)"),
            PermissionMode.Plan => t.C($"{t.Glyphs.PlanMode} plan mode on", t.PlanMode) + t.Dim(" (shift+tab to cycle)"),
            PermissionMode.BypassPermissions => t.C($"{t.Glyphs.Bypass} bypass permissions on", t.Error) + t.Dim(" (shift+tab to cycle)"),
            _ => _input.IsEmpty ? t.Dim("? for shortcuts") : "",
        };
        var window = _session.Model.Capabilities.ContextWindow;
        var used = _session.LastContextTokens;
        var threshold = (_runtime.Settings.AutoCompactThreshold ?? 0.85) * window;
        var pctLeft = threshold > 0 ? Math.Max(0, (int)Math.Round(100 - used * 100.0 / threshold)) : 100;
        var right = pctLeft <= 20 && used > 0
            ? t.C($"Context left until auto-compact: {pctLeft}%", pctLeft <= 5 ? t.Error : t.Warning)
            : t.Dim(_session.Model.Qualified + (_session.Effort is not (ReasoningEffort.Medium) ? $" · effort {_session.Effort.ToString().ToLowerInvariant()}" : ""));
        // Some terminals render the mode glyphs (⏵⏵/⏸) double-width: keep slack so the right side never wraps.
        var gap = w - 4 - TextWidth.Of(left) - TextWidth.Of(right);
        lines.Add(gap > 1 ? "  " + left + new string(' ', gap) + right : "  " + TextWidth.Truncate(left, w - 2));
        if (_statusLine is { Length: > 0 } sl) lines.Add("  " + TextWidth.Truncate(sl, w - 2));
    }
}
