namespace DotCode.Tui.Input;

public enum VimState { Insert, Normal }

/// <summary>Vim keybindings for the prompt editor (enabled with <c>tui.vim</c> or <c>/vim</c>). Supports the common
/// subset: motions h l w b e W B E 0 ^ $ gg G (j/k move lines or walk history), counts, operators d c y with
/// motions or doubled (dd cc yy), x X s S D C r ~ p P u, and i a I A o O to enter insert mode.</summary>
public sealed class VimMode
{
    public VimState State { get; private set; } = VimState.Insert;
    private char? _operator;
    private string _count = "";
    private bool _pendingG;
    private bool _pendingReplace;
    private string _register = "";
    private bool _linewise;

    /// <summary>What the host should do after a normal-mode key.</summary>
    public enum Result { Handled, NotHandled, HistoryPrev, HistoryNext }

    public string Pending => _count + (_operator?.ToString() ?? "") + (_pendingG ? "g" : "") + (_pendingReplace ? "r" : "");

    public void Reset()
    {
        State = VimState.Insert;
        ClearPending();
    }

    private void ClearPending()
    {
        _operator = null;
        _count = "";
        _pendingG = false;
        _pendingReplace = false;
    }

    /// <summary>Esc in insert mode: switch to normal mode, cursor moves back one like vim.</summary>
    public void EnterNormal(InputEditor e)
    {
        State = VimState.Normal;
        ClearPending();
        var t = e.Text;
        if (e.Cursor > 0 && (e.Cursor == t.Length || t[e.Cursor] == '\n') && t[e.Cursor - 1] != '\n') e.Left();
    }

    /// <summary>Handles a key in normal mode. Esc clears a pending command (NotHandled when nothing was pending, so
    /// the host can apply its usual Esc behaviour); Enter and control keys are left to the host.</summary>
    public Result HandleNormal(ConsoleKeyInfo key, InputEditor e)
    {
        if (key.Key == ConsoleKey.Escape)
        {
            if (Pending.Length == 0) return Result.NotHandled;
            ClearPending();
            return Result.Handled;
        }
        if (key.Key == ConsoleKey.Enter || (key.Modifiers & (ConsoleModifiers.Control | ConsoleModifiers.Alt)) != 0) return Result.NotHandled;

        var c = key.KeyChar;
        switch (key.Key)
        {
            case ConsoleKey.LeftArrow: c = 'h'; break;
            case ConsoleKey.RightArrow: c = 'l'; break;
            case ConsoleKey.UpArrow: c = 'k'; break;
            case ConsoleKey.DownArrow: c = 'j'; break;
            case ConsoleKey.Home: c = '0'; break;
            case ConsoleKey.End: c = '$'; break;
            case ConsoleKey.Backspace: c = 'h'; break;
            case ConsoleKey.Delete: c = 'x'; break;
        }
        if (c == '\0') return Result.Handled;

        if (_pendingReplace)
        {
            _pendingReplace = false;
            var n = Count();
            var t = e.Text;
            if (e.Cursor + n <= LineEnd(t, e.Cursor))
                e.Replace(e.Cursor, e.Cursor + n, new string(c, n), e.Cursor + n - 1);
            return Result.Handled;
        }

        if (char.IsAsciiDigit(c) && (c != '0' || _count.Length > 0))
        {
            _count += c;
            return Result.Handled;
        }

        if (_pendingG)
        {
            _pendingG = false;
            if (c == 'g') return ApplyMotion(e, 0, inclusive: false, linewise: _operator is not null);
            ClearPending();
            return Result.Handled;
        }

        // Operators: d c y (doubled = whole line).
        if (c is 'd' or 'c' or 'y')
        {
            if (_operator == c)
            {
                LineOperation(e, c);
                ClearPending();
                return Result.Handled;
            }
            if (_operator is null) { _operator = c; return Result.Handled; }
            ClearPending();
            return Result.Handled;
        }

        var text = e.Text;
        var pos = e.Cursor;
        switch (c)
        {
            // motions
            case 'h': return ApplyMotion(e, Math.Max(LineStart(text, pos), pos - Count()), false);
            case 'l' or ' ':
            {
                var end = LineEnd(text, pos);
                var limit = _operator is null ? Math.Max(LineStart(text, pos), end - 1) : end;
                return ApplyMotion(e, Math.Min(limit, pos + Count()), false);
            }
            case 'w' or 'W':
            {
                var p = pos;
                var n = Count();
                // "cw" behaves like "ce" (vim quirk): change to the end of the word, not the following space.
                if (_operator == 'c' && p < text.Length && !char.IsWhiteSpace(text[p]))
                {
                    for (var i = 0; i < n; i++) p = WordEnd(text, i == 0 ? p - 1 : p, c == 'W');
                    return ApplyMotion(e, p, inclusive: true);
                }
                for (var i = 0; i < n; i++) p = WordForward(text, p, c == 'W');
                return ApplyMotion(e, p, false);
            }
            case 'b' or 'B':
            {
                var p = pos;
                for (int i = 0, n = Count(); i < n; i++) p = WordBack(text, p, c == 'B');
                return ApplyMotion(e, p, false);
            }
            case 'e' or 'E':
            {
                var p = pos;
                for (int i = 0, n = Count(); i < n; i++) p = WordEnd(text, p, c == 'E');
                return ApplyMotion(e, p, inclusive: true);
            }
            case '0': return ApplyMotion(e, LineStart(text, pos), false);
            case '^': return ApplyMotion(e, FirstNonBlank(text, pos), false);
            case '$':
            {
                var end = LineEnd(text, pos);
                return ApplyMotion(e, _operator is null ? Math.Max(LineStart(text, pos), end - 1) : end, false);
            }
            case 'g': _pendingG = true; return Result.Handled;
            case 'G': return ApplyMotion(e, text.Length, false, linewise: _operator is not null);
            case 'j' or 'k':
            {
                if (_operator is not null) { LineOperation(e, _operator.Value, c == 'j' ? 1 : -1); ClearPending(); return Result.Handled; }
                var n = Count();
                ClearPending();
                for (var i = 0; i < n; i++)
                    if (!(c == 'j' ? e.Down() : e.Up())) return c == 'j' ? Result.HistoryNext : Result.HistoryPrev;
                return Result.Handled;
            }
        }

        if (_operator is not null) { ClearPending(); return Result.Handled; }

        // commands
        switch (c)
        {
            case 'i': Insert(); break;
            case 'a': if (pos < LineEnd(text, pos)) e.Right(); Insert(); break;
            case 'I': e.SetCursor(FirstNonBlank(text, pos)); Insert(); break;
            case 'A': e.SetCursor(LineEnd(text, pos)); Insert(); break;
            case 'o': { var end = LineEnd(text, pos); e.Replace(end, end, "\n"); Insert(); break; }
            case 'O': { var start = LineStart(text, pos); e.Replace(start, start, "\n", start); Insert(); break; }
            case 'x':
            {
                var end = Math.Min(LineEnd(text, pos), pos + Count());
                if (end > pos) { Yank(text[pos..end], false); e.Replace(pos, end, ""); ClampToLine(e); }
                break;
            }
            case 'X':
            {
                var start = Math.Max(LineStart(text, pos), pos - Count());
                if (start < pos) { Yank(text[start..pos], false); e.Replace(start, pos, ""); }
                break;
            }
            case 's':
            {
                var end = Math.Min(LineEnd(text, pos), pos + Count());
                Yank(text[pos..end], false);
                e.Replace(pos, end, "");
                Insert();
                break;
            }
            case 'S': LineOperation(e, 'c'); break;
            case 'D': { var end = LineEnd(text, pos); Yank(text[pos..end], false); e.Replace(pos, end, ""); ClampToLine(e); break; }
            case 'C': { var end = LineEnd(text, pos); Yank(text[pos..end], false); e.Replace(pos, end, ""); Insert(); break; }
            case 'r': _pendingReplace = true; return Result.Handled;
            case '~':
            {
                var end = Math.Min(LineEnd(text, pos), pos + Count());
                if (end > pos)
                {
                    var toggled = string.Concat(text[pos..end].Select(ch => char.IsUpper(ch) ? char.ToLowerInvariant(ch) : char.ToUpperInvariant(ch)));
                    e.Replace(pos, end, toggled, Math.Min(end, Math.Max(LineStart(text, pos), LineEnd(text, pos) - 1)));
                }
                break;
            }
            case 'p' or 'P': Put(e, after: c == 'p'); break;
            case 'u': for (int i = 0, n = Count(); i < n; i++) e.Undo(); break;
        }
        ClearPending();
        return Result.Handled;

        void Insert()
        {
            State = VimState.Insert;
            ClearPending();
        }
    }

    private int Count()
    {
        var n = _count.Length > 0 && int.TryParse(_count, out var v) ? Math.Clamp(v, 1, 10_000) : 1;
        _count = "";
        return n;
    }

    /// <summary>Moves the cursor, or applies the pending operator over the motion's range.</summary>
    private Result ApplyMotion(InputEditor e, int target, bool inclusive, bool linewise = false)
    {
        var op = _operator;
        _operator = null;
        _count = "";
        target = Math.Clamp(target, 0, e.Text.Length);
        if (op is null)
        {
            e.SetCursor(Math.Min(target, Math.Max(0, e.Text.Length - (inclusive && target == e.Text.Length ? 1 : 0))));
            return Result.Handled;
        }
        var text = e.Text;
        var start = Math.Min(e.Cursor, target);
        var end = Math.Max(e.Cursor, target) + (inclusive ? 1 : 0);
        if (linewise) { start = LineStart(text, start); end = Math.Min(text.Length, LineEnd(text, end)); }
        end = Math.Min(end, text.Length);
        var removed = text[start..end];
        Yank(removed, linewise);
        switch (op)
        {
            case 'y': e.SetCursor(start); break;
            case 'd': e.Replace(start, end, ""); ClampToLine(e); break;
            case 'c': e.Replace(start, end, ""); State = VimState.Insert; break;
        }
        return Result.Handled;
    }

    /// <summary>dd / cc / yy (and dj / dk): whole lines.</summary>
    private void LineOperation(InputEditor e, char op, int extraLines = 0)
    {
        var text = e.Text;
        var pos = e.Cursor;
        var start = LineStart(text, pos);
        var end = LineEnd(text, pos);
        var lines = Count() - 1 + Math.Max(0, extraLines);
        for (var i = 0; i < lines && end < text.Length; i++) end = LineEnd(text, end + 1);
        if (extraLines < 0 && start > 0) start = LineStart(text, start - 1);
        Yank(text[start..end], linewise: true);
        switch (op)
        {
            case 'y': break;
            case 'c': e.Replace(start, end, "", start); State = VimState.Insert; break;
            case 'd':
                // Remove the line together with one adjoining newline.
                if (end < text.Length) e.Replace(start, end + 1, "", start);
                else if (start > 0) e.Replace(start - 1, end, "", LineStart(text, start - 1));
                else e.Replace(start, end, "", 0);
                e.SetCursor(FirstNonBlank(e.Text, e.Cursor));
                break;
        }
    }

    private void Put(InputEditor e, bool after)
    {
        if (_register.Length == 0) return;
        var text = e.Text;
        var pos = e.Cursor;
        var n = Count();
        var content = string.Concat(Enumerable.Repeat(_register, n));
        if (_linewise)
        {
            if (after)
            {
                var end = LineEnd(text, pos);
                e.Replace(end, end, "\n" + content, end + 1);
            }
            else
            {
                var start = LineStart(text, pos);
                e.Replace(start, start, content + "\n", start);
            }
            return;
        }
        var at = after && pos < LineEnd(text, pos) ? pos + 1 : pos;
        e.Replace(at, at, content, at + content.Length - 1);
    }

    private void Yank(string text, bool linewise)
    {
        if (text.Length == 0) return;
        _register = text;
        _linewise = linewise;
    }

    /// <summary>In normal mode the cursor sits on a character, not after the last one.</summary>
    private static void ClampToLine(InputEditor e)
    {
        var t = e.Text;
        var start = LineStart(t, e.Cursor);
        var end = LineEnd(t, e.Cursor);
        if (e.Cursor >= end && end > start) e.SetCursor(end - 1);
    }

    // ---- text helpers

    public static int LineStart(string t, int pos) => pos <= 0 ? 0 : t.LastIndexOf('\n', Math.Min(pos, t.Length) - 1) + 1;

    public static int LineEnd(string t, int pos)
    {
        var nl = t.IndexOf('\n', Math.Min(pos, t.Length));
        return nl < 0 ? t.Length : nl;
    }

    private static int FirstNonBlank(string t, int pos)
    {
        var p = LineStart(t, pos);
        var end = LineEnd(t, pos);
        while (p < end && (t[p] == ' ' || t[p] == '\t')) p++;
        return p;
    }

    private static int Class(char c, bool big) => char.IsWhiteSpace(c) ? 0 : big || char.IsLetterOrDigit(c) || c == '_' ? 1 : 2;

    public static int WordForward(string t, int p, bool big)
    {
        if (p >= t.Length) return t.Length;
        var cls = Class(t[p], big);
        if (cls != 0) while (p < t.Length && Class(t[p], big) == cls) p++;
        while (p < t.Length && char.IsWhiteSpace(t[p])) p++;
        return p;
    }

    public static int WordBack(string t, int p, bool big)
    {
        if (p <= 0) return 0;
        p--;
        while (p > 0 && char.IsWhiteSpace(t[p])) p--;
        var cls = Class(t[p], big);
        while (p > 0 && Class(t[p - 1], big) == cls) p--;
        return p;
    }

    /// <summary>Position of the last character of the current/next word.</summary>
    public static int WordEnd(string t, int p, bool big)
    {
        if (t.Length == 0) return 0;
        p++;
        while (p < t.Length && char.IsWhiteSpace(t[p])) p++;
        if (p >= t.Length) return t.Length - 1;
        var cls = Class(t[p], big);
        while (p + 1 < t.Length && Class(t[p + 1], big) == cls) p++;
        return p;
    }
}
