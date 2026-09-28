using DotCode.Tui.Input;
using DotCode.Tui.Rendering;
using DotCode.Tui.Themes;

namespace DotCode.Tui.Components;

/// <summary>Full-screen transcript viewer (Ctrl+O): the whole conversation in full detail — thinking, complete tool
/// inputs and outputs — with scrolling and search. Takes over the live region; Esc, q or Ctrl+O closes it.</summary>
public sealed class TranscriptModal(IReadOnlyList<string> lines) : Modal
{
    private int _top = int.MaxValue;     // first visible line; starts at the bottom
    private string? _query;              // non-null while typing a search
    private string _lastQuery = "";
    private string? _status;

    /// <summary>Viewport height in rows (set by the host from the terminal size before each render).</summary>
    public int Height { get; set; } = 24;

    private int PageSize => Math.Max(1, Height - 2);
    private int MaxTop => Math.Max(0, lines.Count - PageSize);
    public int Top => Math.Clamp(_top, 0, MaxTop);

    public override List<string> Render(Theme t, Blocks blocks, int width)
    {
        var ui = UiText.Current;
        var top = Top;
        _top = top;
        var result = new List<string>(Height);
        var end = Math.Min(lines.Count, top + PageSize);
        var position = lines.Count == 0 ? "" : $" {top + 1}–{end}/{lines.Count}";
        var header = t.C(t.B(" " + ui.Transcript + " "), t.Brand) + t.Dim(position);
        result.Add(TextWidth.Truncate(header, width));
        if (lines.Count == 0) result.Add("  " + t.Dim(ui.TranscriptEmpty));
        for (var i = top; i < end; i++) result.Add(TextWidth.Truncate(lines[i], width));
        while (result.Count < Height - 1) result.Add("");
        var footer = _query is not null
            ? t.C(ui.TranscriptSearch, t.Suggestion) + _query
            : _status is { } s ? t.C(s, t.Warning) + t.Dim("  ·  " + ui.TranscriptHint)
            : t.Dim(ui.TranscriptHint);
        result.Add(TextWidth.Truncate(footer, width));
        return result;
    }

    public override (int Row, int Col)? Cursor => _query is null ? null : (Height - 1, TextWidth.Of(UiText.Current.TranscriptSearch + _query));

    public override bool HandleKey(ConsoleKeyInfo key)
    {
        var ctrl = (key.Modifiers & ConsoleModifiers.Control) != 0;
        if (_query is not null)
        {
            switch (key.Key)
            {
                case ConsoleKey.Escape: _query = null; return false;
                case ConsoleKey.Enter:
                    _lastQuery = _query;
                    _query = null;
                    Find(forward: true, fromCurrent: true);
                    return false;
                case ConsoleKey.Backspace: if (_query.Length > 0) _query = _query[..^1]; return false;
            }
            if (key.KeyChar != '\0' && !char.IsControl(key.KeyChar)) _query += key.KeyChar;
            return false;
        }

        _status = null;
        if (ctrl && key.Key is ConsoleKey.O) return true;
        if (ctrl && key.Key is ConsoleKey.D) { Scroll(PageSize / 2); return false; }
        if (ctrl && key.Key is ConsoleKey.U) { Scroll(-PageSize / 2); return false; }
        switch (key.Key)
        {
            case ConsoleKey.Escape: return true;
            case ConsoleKey.UpArrow: Scroll(-1); return false;
            case ConsoleKey.DownArrow or ConsoleKey.Enter: Scroll(1); return false;
            case ConsoleKey.PageUp: Scroll(-PageSize); return false;
            case ConsoleKey.PageDown or ConsoleKey.Spacebar: Scroll(PageSize); return false;
            case ConsoleKey.Home: _top = 0; return false;
            case ConsoleKey.End: _top = MaxTop; return false;
        }
        switch (key.KeyChar)
        {
            case 'q': return true;
            case 'k': Scroll(-1); break;
            case 'j': Scroll(1); break;
            case 'b': Scroll(-PageSize); break;
            case 'f': Scroll(PageSize); break;
            case 'g': _top = 0; break;
            case 'G': _top = MaxTop; break;
            case '/': _query = ""; break;
            case 'n': Find(forward: true, fromCurrent: false); break;
            case 'N': Find(forward: false, fromCurrent: false); break;
        }
        return false;
    }

    private void Scroll(int delta) => _top = Math.Clamp(Top + delta, 0, MaxTop);

    /// <summary>Scrolls so the next (or previous) line containing the query is at the top of the page.</summary>
    private void Find(bool forward, bool fromCurrent)
    {
        if (_lastQuery.Length == 0) return;
        var start = Top + (fromCurrent ? 0 : forward ? 1 : -1);
        for (var n = 0; n < lines.Count; n++)
        {
            var i = ((forward ? start + n : start - n) % lines.Count + lines.Count) % lines.Count;
            if (Ansi.Strip(lines[i]).Contains(_lastQuery, StringComparison.OrdinalIgnoreCase))
            {
                _top = Math.Min(i, MaxTop);
                return;
            }
        }
        _status = UiText.Current.NoMatch + ": " + _lastQuery;
    }
}
