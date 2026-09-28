using System.Text;

namespace DotCode.Tui.Input;

/// <summary>Multi-line prompt editor with readline/Emacs keybindings, history, undo and pasted-text placeholders.</summary>
public sealed class InputEditor
{
    private readonly StringBuilder _text = new();
    private readonly Stack<(string Text, int Cursor)> _undo = new();
    private readonly List<string> _history;
    private int _historyIndex = -1;
    private string _draft = "";
    private readonly Dictionary<int, string> _pastes = [];
    private int _pasteCounter;
    private string _killRing = "";

    public int Cursor { get; private set; }
    public string Text => _text.ToString();
    public bool IsEmpty => _text.Length == 0;

    public InputEditor(List<string> history) => _history = history;

    public void SetText(string text, int? cursor = null)
    {
        _text.Clear().Append(text);
        Cursor = Math.Clamp(cursor ?? text.Length, 0, text.Length);
    }

    public void Clear()
    {
        SaveUndo();
        _text.Clear();
        Cursor = 0;
        _historyIndex = -1;
        _pastes.Clear();
    }

    /// <summary>Text with paste placeholders expanded (what gets submitted).</summary>
    public string ExpandedText()
    {
        var t = Text;
        foreach (var (id, content) in _pastes)
            t = t.Replace(PastePlaceholder(id, content), content, StringComparison.Ordinal);
        return t;
    }

    private static string PastePlaceholder(int id, string content)
    {
        var lines = content.Split('\n').Length;
        return lines > 1 ? $"[Pasted text #{id} +{lines} lines]" : $"[Pasted text #{id} {content.Length} chars]";
    }

    private void SaveUndo()
    {
        if (_undo.Count > 0 && _undo.Peek().Text == Text) return;
        _undo.Push((Text, Cursor));
        if (_undo.Count > 200) { var keep = _undo.Take(100).Reverse().ToList(); _undo.Clear(); foreach (var k in keep) _undo.Push(k); }
    }

    public void Undo()
    {
        if (_undo.Count == 0) return;
        var (t, c) = _undo.Pop();
        _text.Clear().Append(t);
        Cursor = Math.Min(c, t.Length);
    }

    public void Insert(string s)
    {
        if (s.Length == 0) return;
        SaveUndo();
        s = s.Replace("\r\n", "\n").Replace('\r', '\n');
        // Large pastes collapse into a placeholder, like Claude Code.
        if (s.Length > 800 || s.Count(ch => ch == '\n') > 10)
        {
            var id = ++_pasteCounter;
            _pastes[id] = s;
            s = PastePlaceholder(id, s);
        }
        _text.Insert(Cursor, s);
        Cursor += s.Length;
        _historyIndex = -1;
    }

    public void Backspace()
    {
        if (Cursor == 0) return;
        SaveUndo();
        // Delete a whole placeholder at once.
        var before = Text[..Cursor];
        if (before.EndsWith(']') && before.LastIndexOf("[Pasted text #", StringComparison.Ordinal) is var p and >= 0)
        {
            _text.Remove(p, Cursor - p);
            Cursor = p;
            return;
        }
        var len = Cursor >= 2 && char.IsLowSurrogate(_text[Cursor - 1]) ? 2 : 1;
        _text.Remove(Cursor - len, len);
        Cursor -= len;
    }

    public void Delete()
    {
        if (Cursor >= _text.Length) return;
        SaveUndo();
        var len = Cursor + 1 < _text.Length && char.IsHighSurrogate(_text[Cursor]) ? 2 : 1;
        _text.Remove(Cursor, len);
    }

    public void Left() { if (Cursor > 0) Cursor -= Cursor >= 2 && char.IsLowSurrogate(_text[Cursor - 1]) ? 2 : 1; }
    public void Right() { if (Cursor < _text.Length) Cursor += Cursor + 1 < _text.Length && char.IsHighSurrogate(_text[Cursor]) ? 2 : 1; }

    public void Home()
    {
        var t = Text;
        var nl = Cursor > 0 ? t.LastIndexOf('\n', Cursor - 1) : -1;
        Cursor = nl + 1;
    }

    public void End()
    {
        var nl = Text.IndexOf('\n', Cursor);
        Cursor = nl < 0 ? _text.Length : nl;
    }

    public void WordLeft()
    {
        var t = Text;
        while (Cursor > 0 && char.IsWhiteSpace(t[Cursor - 1])) Cursor--;
        while (Cursor > 0 && !char.IsWhiteSpace(t[Cursor - 1])) Cursor--;
    }

    public void WordRight()
    {
        var t = Text;
        while (Cursor < t.Length && char.IsWhiteSpace(t[Cursor])) Cursor++;
        while (Cursor < t.Length && !char.IsWhiteSpace(t[Cursor])) Cursor++;
    }

    public void DeleteWordBack()
    {
        var end = Cursor;
        WordLeft();
        if (end > Cursor)
        {
            SaveUndo();
            _killRing = Text[Cursor..end];
            _text.Remove(Cursor, end - Cursor);
        }
    }

    public void DeleteWordForward()
    {
        var start = Cursor;
        WordRight();
        var end = Cursor;
        Cursor = start;
        if (end > start)
        {
            SaveUndo();
            _killRing = Text[start..end];
            _text.Remove(start, end - start);
        }
    }

    public void KillToLineStart()
    {
        var end = Cursor;
        Home();
        if (end > Cursor)
        {
            SaveUndo();
            _killRing = Text[Cursor..end];
            _text.Remove(Cursor, end - Cursor);
        }
    }

    public void KillToLineEnd()
    {
        var start = Cursor;
        End();
        var end = Cursor;
        Cursor = start;
        if (end > start)
        {
            SaveUndo();
            _killRing = Text[start..end];
            _text.Remove(start, end - start);
        }
    }

    public void Yank() => Insert(_killRing);

    /// <summary>Line/column of the cursor within the (unwrapped) text.</summary>
    public (int Line, int Column) CursorPosition()
    {
        var before = Text[..Cursor];
        var line = before.Count(c => c == '\n');
        var col = Cursor - (before.LastIndexOf('\n') + 1);
        return (line, col);
    }

    public int LineCount => Text.Count(c => c == '\n') + 1;

    /// <summary>Moves up a line inside multi-line input; returns false when already on the first line.</summary>
    public bool Up()
    {
        var (line, col) = CursorPosition();
        if (line == 0) return false;
        var lines = Text.Split('\n');
        var start = lines.Take(line - 1).Sum(l => l.Length + 1);
        Cursor = start + Math.Min(col, lines[line - 1].Length);
        return true;
    }

    public bool Down()
    {
        var (line, col) = CursorPosition();
        var lines = Text.Split('\n');
        if (line >= lines.Length - 1) return false;
        var start = lines.Take(line + 1).Sum(l => l.Length + 1);
        Cursor = start + Math.Min(col, lines[line + 1].Length);
        return true;
    }

    public void HistoryPrev()
    {
        if (_history.Count == 0) return;
        if (_historyIndex == -1) { _draft = Text; _historyIndex = _history.Count; }
        if (_historyIndex > 0)
        {
            _historyIndex--;
            SetText(_history[_historyIndex]);
        }
    }

    public void HistoryNext()
    {
        if (_historyIndex == -1) return;
        _historyIndex++;
        if (_historyIndex >= _history.Count)
        {
            _historyIndex = -1;
            SetText(_draft);
        }
        else SetText(_history[_historyIndex]);
    }

    /// <summary>The token at the cursor (for "/" command and "@" file completion).</summary>
    public (string Token, int Start) CurrentToken()
    {
        var t = Text;
        var start = Cursor;
        while (start > 0 && !char.IsWhiteSpace(t[start - 1])) start--;
        return (t[start..Cursor], start);
    }

    public void ReplaceToken(int start, string replacement)
    {
        SaveUndo();
        _text.Remove(start, Cursor - start).Insert(start, replacement);
        Cursor = start + replacement.Length;
    }
}
