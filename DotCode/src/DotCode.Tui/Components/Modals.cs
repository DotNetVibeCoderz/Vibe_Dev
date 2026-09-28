using DotCode.Abstractions;
using DotCode.Engine.Agent;
using DotCode.Tui.Rendering;
using DotCode.Tui.Themes;

namespace DotCode.Tui.Components;

public enum ModalResult { None, Close }

/// <summary>A dialog that temporarily replaces the input box and owns keyboard input.</summary>
public abstract class Modal
{
    public abstract List<string> Render(Theme t, Blocks blocks, int width);
    /// <summary>Returns true when the modal is finished and should be removed.</summary>
    public abstract bool HandleKey(ConsoleKeyInfo key);
    public virtual (int Row, int Col)? Cursor => null;
    /// <summary>Called when the modal is dismissed externally (turn interrupted).</summary>
    public virtual void Cancel() { }
}

/// <summary>Claude Code-style permission prompt: tool-specific title and preview, numbered options, esc = no.</summary>
public sealed class PermissionModal : Modal
{
    private readonly PermissionRequest _request;
    private readonly TaskCompletionSource<PermissionDecision> _tcs;
    private readonly List<(string Label, PermissionDecision Decision)> _options = [];
    private readonly string _projectName;
    private int _index;

    public PermissionModal(PermissionRequest request, TaskCompletionSource<PermissionDecision> tcs, string projectRoot)
    {
        _request = request;
        _tcs = tcs;
        _projectName = Path.GetFileName(projectRoot.TrimEnd(Path.DirectorySeparatorChar));
        var isEdit = request.ToolName is "Edit" or "Write" or "NotebookEdit";
        _options.Add(("Yes", PermissionDecision.AllowOnce));
        if (isEdit)
            _options.Add(("Yes, allow all edits during this session (shift+tab)", new PermissionDecision(PermissionDecisionKind.AllowSession)));
        else if (request.SuggestedRule is { } rule)
        {
            var label = request.ToolName is "Bash" or "PowerShell"
                ? $"Yes, and don't ask again for {Spec(rule)} commands in {_projectName}"
                : request.ToolName is "WebFetch"
                    ? $"Yes, and don't ask again for {Spec(rule).Replace("domain:", "")}"
                    : $"Yes, and don't ask again for {rule} in {_projectName}";
            _options.Add((label, new PermissionDecision(PermissionDecisionKind.AllowAlways, Rule: rule)));
        }
        _options.Add(("No, and tell DotCode what to do differently (esc)", PermissionDecision.Deny()));
    }

    private static string Spec(string rule)
    {
        var open = rule.IndexOf('(');
        return open > 0 && rule.EndsWith(')') ? rule[(open + 1)..^1].Replace(":*", "") : rule;
    }

    public override List<string> Render(Theme t, Blocks blocks, int width)
    {
        var content = new List<string>();
        var r = _request;
        switch (r.ToolName)
        {
            case "Bash" or "PowerShell":
                content.Add(t.B($"{r.ToolName} command"));
                content.Add("");
                foreach (var l in (r.Input.GetString("command") ?? "").Split('\n').Take(12)) content.Add("  " + l);
                if (r.Detail is { Length: > 0 } d) content.Add("  " + t.Dim(d));
                break;
            case "Edit" or "Write" or "NotebookEdit":
                content.Add(t.B(r.Title));
                if (r.Diff is { Length: > 0 } diff)
                {
                    content.Add("");
                    content.AddRange(blocks.DiffLines(diff, width - 6, 24));
                }
                break;
            case "WebFetch":
                content.Add(t.B("Fetch"));
                content.Add("");
                content.Add("  " + (r.Input.GetString("url") ?? ""));
                content.Add("  " + t.Dim(r.Input.GetString("prompt") ?? ""));
                break;
            default:
                content.Add(t.B(r.Title.Length > 0 ? r.Title : "Tool use"));
                content.Add("");
                content.Add("  " + r.DisplayName);
                if (r.Detail is { Length: > 0 } dd) content.Add("  " + t.Dim(dd));
                break;
        }
        content.Add("");
        var file = r.Input.GetString("file_path") is { } fp ? Path.GetFileName(fp) : null;
        content.Add(r.ToolName is "Edit" or "NotebookEdit" && file is not null ? $"Do you want to make this edit to {t.B(file)}?"
            : r.ToolName == "Write" && file is not null ? $"Do you want to create {t.B(file)}?"
            : "Do you want to proceed?");
        for (var i = 0; i < _options.Count; i++)
        {
            var label = $"{i + 1}. {_options[i].Label}";
            content.Add(i == _index ? t.C(t.Glyphs.Pointer + " " + label, t.Suggestion) : "  " + label);
        }
        return blocks.Box(content, width, t.Permission);
    }

    public override bool HandleKey(ConsoleKeyInfo key)
    {
        switch (key.Key)
        {
            case ConsoleKey.UpArrow: _index = (_index + _options.Count - 1) % _options.Count; return false;
            case ConsoleKey.DownArrow: _index = (_index + 1) % _options.Count; return false;
            case ConsoleKey.Enter: _tcs.TrySetResult(_options[_index].Decision); return true;
            case ConsoleKey.Escape: _tcs.TrySetResult(PermissionDecision.Deny()); return true;
            case ConsoleKey.Tab when (key.Modifiers & ConsoleModifiers.Shift) != 0 && _options.Count == 3 && _request.ToolName is "Edit" or "Write" or "NotebookEdit":
                _tcs.TrySetResult(_options[1].Decision);
                return true;
        }
        if (key.KeyChar is >= '1' and <= '9' && key.KeyChar - '1' < _options.Count)
        {
            _tcs.TrySetResult(_options[key.KeyChar - '1'].Decision);
            return true;
        }
        if (key.KeyChar is 'y' or 'Y') { _tcs.TrySetResult(_options[0].Decision); return true; }
        if (key.KeyChar is 'n' or 'N') { _tcs.TrySetResult(PermissionDecision.Deny()); return true; }
        return false;
    }

    public override void Cancel() => _tcs.TrySetResult(PermissionDecision.Deny());
}

/// <summary>Generic single-choice picker (models, themes, sessions, rewind points, config values...).</summary>
public sealed class SelectModal : Modal
{
    private readonly string _title;
    private readonly string? _subtitle;
    private readonly List<(string Label, string? Description, object? Value)> _items;
    private readonly Func<object?, bool> _onSelect;
    private readonly Action<object?>? _onHighlight;
    private readonly Func<Theme, int, List<string>>? _preview;
    private readonly Action? _onCancel;
    private int _index;
    private string _filter = "";
    public bool Filterable { get; init; }

    public SelectModal(string title, IEnumerable<(string Label, string? Description, object? Value)> items, Func<object?, bool> onSelect,
        string? subtitle = null, int initialIndex = 0, Action<object?>? onHighlight = null, Func<Theme, int, List<string>>? preview = null, Action? onCancel = null)
    {
        _title = title;
        _subtitle = subtitle;
        _items = items.ToList();
        _onSelect = onSelect;
        _onHighlight = onHighlight;
        _preview = preview;
        _onCancel = onCancel;
        _index = Math.Clamp(initialIndex, 0, Math.Max(0, _items.Count - 1));
    }

    private List<(string Label, string? Description, object? Value)> Visible =>
        _filter.Length == 0 ? _items : _items.Where(i => i.Label.Contains(_filter, StringComparison.OrdinalIgnoreCase) || (i.Description?.Contains(_filter, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();

    public override List<string> Render(Theme t, Blocks blocks, int width)
    {
        var lines = new List<string> { t.C(Blocks.Repeat(t.Ascii ? "-" : "─", width), t.Permission), " " + t.C(t.B(_title), t.Permission) };
        if (_subtitle is not null) foreach (var s in TextWidth.Wrap(_subtitle, width - 2)) lines.Add(" " + t.Dim(s));
        if (Filterable) lines.Add(" " + t.Dim("Filter: ") + _filter + t.Dim("▏"));
        lines.Add("");
        var visible = Visible;
        const int pageSize = 12;
        var start = Math.Clamp(_index - pageSize / 2, 0, Math.Max(0, visible.Count - pageSize));
        if (start > 0) lines.Add("   " + t.Dim($"{t.Glyphs.Up} {start} more"));
        var labelWidth = Math.Min(40, visible.Skip(start).Take(pageSize).Select(i => TextWidth.Of(i.Label)).DefaultIfEmpty(10).Max() + 2);
        for (var i = start; i < Math.Min(visible.Count, start + pageSize); i++)
        {
            var item = visible[i];
            var num = $"{i + 1}.".PadRight(4);
            var label = TextWidth.Pad(item.Label, labelWidth);
            var desc = item.Description is null ? "" : TextWidth.Truncate(item.Description, Math.Max(10, width - labelWidth - 10));
            lines.Add(i == _index
                ? " " + t.C(t.Glyphs.Pointer + " " + num + label, t.Suggestion) + t.Dim(desc)
                : "   " + num + label + t.Dim(desc));
        }
        if (start + pageSize < visible.Count) lines.Add("   " + t.Dim($"{t.Glyphs.Down} {visible.Count - start - pageSize} more"));
        if (visible.Count == 0) lines.Add("   " + t.Dim("(no matches)"));
        if (_preview is not null)
        {
            lines.Add("");
            lines.AddRange(_preview(t, width));
        }
        lines.Add("");
        lines.Add(" " + t.Dim($"Enter to select · {t.Glyphs.Up}/{t.Glyphs.Down} to navigate · Esc to cancel{(Filterable ? " · type to filter" : "")}"));
        return lines;
    }

    public override bool HandleKey(ConsoleKeyInfo key)
    {
        var visible = Visible;
        switch (key.Key)
        {
            case ConsoleKey.UpArrow:
                if (visible.Count > 0) _index = (_index + visible.Count - 1) % visible.Count;
                Highlight();
                return false;
            case ConsoleKey.DownArrow:
                if (visible.Count > 0) _index = (_index + 1) % visible.Count;
                Highlight();
                return false;
            case ConsoleKey.PageUp: _index = Math.Max(0, _index - 10); Highlight(); return false;
            case ConsoleKey.PageDown: _index = Math.Min(Math.Max(0, visible.Count - 1), _index + 10); Highlight(); return false;
            case ConsoleKey.Enter:
                if (visible.Count == 0) return false;
                return _onSelect(visible[_index].Value);
            case ConsoleKey.Escape:
                _onCancel?.Invoke();
                return true;
            case ConsoleKey.Backspace when Filterable && _filter.Length > 0:
                _filter = _filter[..^1];
                _index = 0;
                return false;
        }
        if (Filterable && !char.IsControl(key.KeyChar))
        {
            _filter += key.KeyChar;
            _index = 0;
            return false;
        }
        if (key.KeyChar is >= '1' and <= '9' && key.KeyChar - '1' < visible.Count)
        {
            _index = key.KeyChar - '1';
            return _onSelect(visible[_index].Value);
        }
        return false;
    }

    private void Highlight()
    {
        var v = Visible;
        if (v.Count > 0) _onHighlight?.Invoke(v[_index].Value);
    }

    public override void Cancel() => _onCancel?.Invoke();
}

/// <summary>AskUserQuestion: one question at a time with numbered options plus a free-text "Other" answer.</summary>
public sealed class QuestionModal : Modal
{
    private readonly IReadOnlyList<UserQuestion> _questions;
    private readonly TaskCompletionSource<IReadOnlyList<UserQuestionAnswer>?> _tcs;
    private readonly List<UserQuestionAnswer> _answers = [];
    private readonly HashSet<int> _multi = [];
    private int _q;
    private int _index;
    private string _other = "";

    public QuestionModal(IReadOnlyList<UserQuestion> questions, TaskCompletionSource<IReadOnlyList<UserQuestionAnswer>?> tcs)
    {
        _questions = questions;
        _tcs = tcs;
    }

    private UserQuestion Current => _questions[_q];
    private int OtherIndex => Current.Options.Count;

    public override List<string> Render(Theme t, Blocks blocks, int width)
    {
        var lines = new List<string> { t.C(Blocks.Repeat(t.Ascii ? "-" : "─", width), t.Permission) };
        if (_questions.Count > 1)
            lines.Add(" " + string.Join(t.Dim(" · "), _questions.Select((q, i) => i == _q ? t.C(t.B(q.Header), t.Suggestion) : i < _q ? t.C(t.Glyphs.Check + " " + q.Header, t.Success) : t.Dim(q.Header))));
        lines.Add("");
        foreach (var l in TextWidth.Wrap(t.B(Current.Question), width - 2)) lines.Add(" " + l);
        lines.Add("");
        for (var i = 0; i <= OtherIndex; i++)
        {
            var selected = i == _index;
            var check = Current.MultiSelect ? (_multi.Contains(i) ? $"[{t.Glyphs.Check}] " : "[ ] ") : "";
            var label = i < OtherIndex ? Current.Options[i].Label : (_other.Length > 0 || selected ? "Other: " + _other + (selected ? "▏" : "") : "Other (type your own answer)");
            lines.Add(selected ? " " + t.C($"{t.Glyphs.Pointer} {i + 1}. {check}{label}", t.Suggestion) : $"   {i + 1}. {check}{label}");
            if (i < OtherIndex && Current.Options[i].Description is { Length: > 0 } d)
                foreach (var w in TextWidth.Wrap(d, width - 10)) lines.Add("        " + t.Dim(w));
        }
        lines.Add("");
        lines.Add(" " + t.Dim(Current.MultiSelect ? "Space to toggle · Enter to confirm · Esc to cancel" : "Enter to select · Esc to cancel"));
        return lines;
    }

    public override bool HandleKey(ConsoleKeyInfo key)
    {
        switch (key.Key)
        {
            case ConsoleKey.UpArrow: _index = (_index + OtherIndex) % (OtherIndex + 1); return false;
            case ConsoleKey.DownArrow: _index = (_index + 1) % (OtherIndex + 1); return false;
            case ConsoleKey.Escape: _tcs.TrySetResult([]); return true;
            case ConsoleKey.Backspace when _index == OtherIndex && _other.Length > 0: _other = _other[..^1]; return false;
            case ConsoleKey.Spacebar when Current.MultiSelect && _index < OtherIndex:
                if (!_multi.Remove(_index)) _multi.Add(_index);
                return false;
            case ConsoleKey.Enter:
                string answer;
                if (Current.MultiSelect)
                {
                    var picks = _multi.Order().Select(i => Current.Options[i].Label).ToList();
                    if (_other.Length > 0) picks.Add(_other);
                    if (picks.Count == 0 && _index < OtherIndex) picks.Add(Current.Options[_index].Label);
                    answer = string.Join(", ", picks);
                }
                else answer = _index < OtherIndex ? Current.Options[_index].Label : _other;
                if (answer.Length == 0) return false;
                _answers.Add(new UserQuestionAnswer(Current.Question, answer));
                if (++_q >= _questions.Count)
                {
                    _tcs.TrySetResult(_answers);
                    return true;
                }
                _index = 0;
                _other = "";
                _multi.Clear();
                return false;
        }
        if (_index == OtherIndex && !char.IsControl(key.KeyChar)) { _other += key.KeyChar; return false; }
        if (key.KeyChar is >= '1' and <= '9' && key.KeyChar - '1' <= OtherIndex) _index = key.KeyChar - '1';
        return false;
    }

    public override void Cancel() => _tcs.TrySetResult([]);
}

/// <summary>Plan approval shown when the model calls ExitPlanMode.</summary>
public sealed class PlanModal : Modal
{
    private readonly string _plan;
    private readonly TaskCompletionSource<PlanDecision> _tcs;
    private int _index;
    private string _feedback = "";
    private bool _typing;
    private int _scroll;

    /// <summary>Plan lines shown at once; set by the host from the terminal height.</summary>
    public static int MaxPlanLines { get; set; } = 18;

    public PlanModal(string plan, TaskCompletionSource<PlanDecision> tcs)
    {
        _plan = plan;
        _tcs = tcs;
    }

    private static readonly string[] Options = ["Yes, and auto-accept edits", "Yes, and manually approve edits", "No, keep planning"];

    public override List<string> Render(Theme t, Blocks blocks, int width)
    {
        var content = new List<string> { t.B("Ready to code?"), "", "Here is DotCode's plan:", "" };
        // Long plans scroll inside the box (PgUp/PgDn) so the dialog always fits on screen.
        var planLines = Markdown.Render(_plan, width - 10, t);
        var max = Math.Max(6, MaxPlanLines);
        _scroll = Math.Clamp(_scroll, 0, Math.Max(0, planLines.Count - max));
        var visible = planLines.Skip(_scroll).Take(max).ToList();
        if (planLines.Count > max)
            visible.Add(t.Dim($"{t.Glyphs.Ellipsis} lines {_scroll + 1}-{_scroll + visible.Count} of {planLines.Count} · PgUp/PgDn to scroll"));
        var planBox = blocks.Box(visible, width - 4, t.PlanMode);
        content.AddRange(planBox);
        content.Add("");
        content.Add("Would you like to proceed?");
        for (var i = 0; i < Options.Length; i++)
        {
            var label = $"{i + 1}. {Options[i]}" + (i == 2 && (_typing || _feedback.Length > 0) ? ": " + _feedback + (_typing ? "▏" : "") : "");
            content.Add(i == _index ? t.C(t.Glyphs.Pointer + " " + label, t.Suggestion) : "  " + label);
        }
        return blocks.Box(content, width, t.PlanMode);
    }

    public override bool HandleKey(ConsoleKeyInfo key)
    {
        if (_typing)
        {
            switch (key.Key)
            {
                case ConsoleKey.Enter: _tcs.TrySetResult(new PlanDecision(PlanApproval.Reject, _feedback)); return true;
                case ConsoleKey.Escape: _typing = false; return false;
                case ConsoleKey.Backspace: if (_feedback.Length > 0) _feedback = _feedback[..^1]; return false;
            }
            if (!char.IsControl(key.KeyChar)) _feedback += key.KeyChar;
            return false;
        }
        switch (key.Key)
        {
            case ConsoleKey.PageUp: _scroll = Math.Max(0, _scroll - 10); return false;
            case ConsoleKey.PageDown: _scroll += 10; return false;
            case ConsoleKey.UpArrow: _index = (_index + 2) % 3; return false;
            case ConsoleKey.DownArrow: _index = (_index + 1) % 3; return false;
            case ConsoleKey.Escape: _tcs.TrySetResult(new PlanDecision(PlanApproval.Reject)); return true;
            case ConsoleKey.Enter:
                switch (_index)
                {
                    case 0: _tcs.TrySetResult(new PlanDecision(PlanApproval.ApproveAcceptEdits)); return true;
                    case 1: _tcs.TrySetResult(new PlanDecision(PlanApproval.Approve)); return true;
                    default: _typing = true; return false;
                }
        }
        if (key.KeyChar is '1' or '2' or '3') { _index = key.KeyChar - '1'; return HandleKey(new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false)); }
        return false;
    }

    public override void Cancel() => _tcs.TrySetResult(new PlanDecision(PlanApproval.Reject));
}
