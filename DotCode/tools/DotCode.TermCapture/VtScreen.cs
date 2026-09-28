using System.Text;

namespace DotCode.TermCapture;

internal record struct CellStyle(int Fg, int Bg, bool Bold, bool Dim, bool Italic, bool Underline, bool Inverse, bool Strike)
{
    public static readonly CellStyle Default = new(-1, -1, false, false, false, false, false, false);
}

internal struct Cell
{
    public string Text;   // grapheme (may be a surrogate pair); "" for the right half of a wide char
    public CellStyle Style;
}

/// <summary>A small VT100/xterm screen emulator: enough of CSI/SGR/OSC to faithfully reproduce ConPTY output
/// (cursor movement, erase, scrolling, 16/256/24-bit colors, wide characters).</summary>
internal sealed class VtScreen
{
    public int Cols { get; }
    public int Rows { get; }
    private readonly Cell[][] _grid;
    public int CursorX { get; private set; }
    public int CursorY { get; private set; }
    public bool CursorVisible { get; private set; } = true;
    private CellStyle _style = CellStyle.Default;
    private int _scrollTop, _scrollBottom;
    private (int X, int Y) _saved;
    private readonly StringBuilder _pending = new();
    private bool _wrapPending;
    public List<Cell[]> Scrollback { get; } = [];

    public VtScreen(int cols, int rows)
    {
        Cols = cols;
        Rows = rows;
        _grid = new Cell[rows][];
        for (var i = 0; i < rows; i++) _grid[i] = NewRow();
        _scrollBottom = rows - 1;
    }

    private Cell[] NewRow()
    {
        var row = new Cell[Cols];
        for (var i = 0; i < Cols; i++) row[i] = new Cell { Text = " ", Style = CellStyle.Default };
        return row;
    }

    public Cell[] Row(int y) => _grid[y];

    public string RowText(int y) => RowText(_grid[y]);

    public static string RowText(Cell[] row)
    {
        var sb = new StringBuilder();
        foreach (var c in row) sb.Append(c.Text);
        return sb.ToString().TrimEnd();
    }

    /// <summary>Scrollback followed by the visible screen (for tall "full session" captures).</summary>
    public List<Cell[]> AllRows() => [.. Scrollback, .. _grid];

    public string Text() => string.Join('\n', Enumerable.Range(0, Rows).Select(RowText));

    public void Feed(string data)
    {
        _pending.Append(data);
        var s = _pending.ToString();
        _pending.Clear();
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (c == '\u001b')
            {
                var consumed = ParseEscape(s, i);
                if (consumed < 0) { _pending.Append(s, i, s.Length - i); return; } // incomplete sequence
                i += consumed;
                continue;
            }
            switch (c)
            {
                case '\r': CursorX = 0; _wrapPending = false; break;
                case '\n': LineFeed(); break;
                case '\b': if (CursorX > 0) CursorX--; _wrapPending = false; break;
                case '\t': CursorX = Math.Min(Cols - 1, (CursorX / 8 + 1) * 8); break;
                case '\a': break;
                default:
                    if (c < ' ') break;
                    string g;
                    if (char.IsHighSurrogate(c) && i + 1 < s.Length) { g = s.Substring(i, 2); i++; }
                    else g = c.ToString();
                    Put(g);
                    break;
            }
            i++;
        }
    }

    private static int Width(string g)
    {
        var cp = char.ConvertToUtf32(g, 0);
        if (cp is >= 0x1100 and <= 0x115F or >= 0x2E80 and <= 0xA4CF or >= 0xAC00 and <= 0xD7A3 or >= 0xF900 and <= 0xFAFF or >= 0xFF00 and <= 0xFF60 or >= 0x1F300 and <= 0x1F64F or >= 0x1F900 and <= 0x1F9FF) return 2;
        if (cp is 0x200B or 0x200D or 0xFE0F) return 0;
        return 1;
    }

    private void Put(string g)
    {
        var w = Width(g);
        if (w == 0) return;
        if (_wrapPending) { CursorX = 0; LineFeed(); _wrapPending = false; }
        if (CursorX + w > Cols) { CursorX = 0; LineFeed(); }
        _grid[CursorY][CursorX] = new Cell { Text = g, Style = _style };
        if (w == 2 && CursorX + 1 < Cols) _grid[CursorY][CursorX + 1] = new Cell { Text = "", Style = _style };
        CursorX += w;
        if (CursorX >= Cols) { CursorX = Cols - 1; _wrapPending = true; }
    }

    private void LineFeed()
    {
        if (CursorY == _scrollBottom) ScrollUp(1);
        else if (CursorY < Rows - 1) CursorY++;
    }

    private void ScrollUp(int n)
    {
        for (var k = 0; k < n; k++)
        {
            if (_scrollTop == 0) Scrollback.Add(_grid[0]);
            for (var y = _scrollTop; y < _scrollBottom; y++) _grid[y] = _grid[y + 1];
            _grid[_scrollBottom] = NewRow();
        }
    }

    private void ScrollDown(int n)
    {
        for (var k = 0; k < n; k++)
        {
            for (var y = _scrollBottom; y > _scrollTop; y--) _grid[y] = _grid[y - 1];
            _grid[_scrollTop] = NewRow();
        }
    }

    /// <summary>Returns the number of chars consumed, or -1 if the sequence is incomplete.</summary>
    private int ParseEscape(string s, int i)
    {
        if (i + 1 >= s.Length) return -1;
        var next = s[i + 1];
        if (next == '[')
        {
            var j = i + 2;
            while (j < s.Length && !(s[j] >= '@' && s[j] <= '~')) j++;
            if (j >= s.Length) return -1;
            Csi(s[(i + 2)..j], s[j]);
            return j - i + 1;
        }
        if (next == ']')
        {
            var j = i + 2;
            while (j < s.Length && s[j] != '\a' && !(s[j] == '\u001b' && j + 1 < s.Length && s[j + 1] == '\\')) j++;
            if (j >= s.Length) return -1;
            return s[j] == '\a' ? j - i + 1 : j - i + 2;
        }
        switch (next)
        {
            case '7': _saved = (CursorX, CursorY); return 2;
            case '8': (CursorX, CursorY) = _saved; return 2;
            case 'M': if (CursorY == _scrollTop) ScrollDown(1); else if (CursorY > 0) CursorY--; return 2;
            case 'D': LineFeed(); return 2;
            case 'E': CursorX = 0; LineFeed(); return 2;
            case 'c': return 2;
            case '(' or ')': return i + 2 < s.Length ? 3 : -1;
            case '=' or '>': return 2;
        }
        return 2;
    }

    private void Csi(string param, char final)
    {
        var priv = param.StartsWith('?') || param.StartsWith('>') || param.StartsWith('=');
        var p = (priv ? param[1..] : param).Split(';');
        int Arg(int idx, int def) => idx < p.Length && int.TryParse(p[idx], out var v) ? v : def;
        _wrapPending = false;
        switch (final)
        {
            case 'A': CursorY = Math.Max(0, CursorY - Math.Max(1, Arg(0, 1))); break;
            case 'B': CursorY = Math.Min(Rows - 1, CursorY + Math.Max(1, Arg(0, 1))); break;
            case 'C': CursorX = Math.Min(Cols - 1, CursorX + Math.Max(1, Arg(0, 1))); break;
            case 'D': CursorX = Math.Max(0, CursorX - Math.Max(1, Arg(0, 1))); break;
            case 'E': CursorX = 0; CursorY = Math.Min(Rows - 1, CursorY + Math.Max(1, Arg(0, 1))); break;
            case 'F': CursorX = 0; CursorY = Math.Max(0, CursorY - Math.Max(1, Arg(0, 1))); break;
            case 'G' or '`': CursorX = Math.Clamp(Arg(0, 1) - 1, 0, Cols - 1); break;
            case 'd': CursorY = Math.Clamp(Arg(0, 1) - 1, 0, Rows - 1); break;
            case 'H' or 'f': CursorY = Math.Clamp(Arg(0, 1) - 1, 0, Rows - 1); CursorX = Math.Clamp(Arg(1, 1) - 1, 0, Cols - 1); break;
            case 'J':
                switch (Arg(0, 0))
                {
                    case 0: ClearRange(CursorY, CursorX, Rows - 1, Cols - 1); break;
                    case 1: ClearRange(0, 0, CursorY, CursorX); break;
                    default: ClearRange(0, 0, Rows - 1, Cols - 1); break;
                }
                break;
            case 'K':
                switch (Arg(0, 0))
                {
                    case 0: ClearRange(CursorY, CursorX, CursorY, Cols - 1); break;
                    case 1: ClearRange(CursorY, 0, CursorY, CursorX); break;
                    default: ClearRange(CursorY, 0, CursorY, Cols - 1); break;
                }
                break;
            case 'X':
                for (var k = 0; k < Math.Max(1, Arg(0, 1)) && CursorX + k < Cols; k++) _grid[CursorY][CursorX + k] = new Cell { Text = " ", Style = _style with { } };
                break;
            case 'P':
            {
                var n = Math.Max(1, Arg(0, 1));
                var row = _grid[CursorY];
                for (var x = CursorX; x < Cols; x++) row[x] = x + n < Cols ? row[x + n] : new Cell { Text = " ", Style = CellStyle.Default };
                break;
            }
            case '@':
            {
                var n = Math.Max(1, Arg(0, 1));
                var row = _grid[CursorY];
                for (var x = Cols - 1; x >= CursorX; x--) row[x] = x - n >= CursorX ? row[x - n] : new Cell { Text = " ", Style = CellStyle.Default };
                break;
            }
            case 'L':
            {
                var n = Math.Max(1, Arg(0, 1));
                var top = _scrollTop;
                _scrollTop = CursorY;
                ScrollDown(n);
                _scrollTop = top;
                break;
            }
            case 'M':
            {
                var n = Math.Max(1, Arg(0, 1));
                var top = _scrollTop;
                _scrollTop = CursorY;
                for (var k = 0; k < n; k++)
                {
                    for (var y = _scrollTop; y < _scrollBottom; y++) _grid[y] = _grid[y + 1];
                    _grid[_scrollBottom] = NewRow();
                }
                _scrollTop = top;
                break;
            }
            case 'S': ScrollUp(Math.Max(1, Arg(0, 1))); break;
            case 'T': ScrollDown(Math.Max(1, Arg(0, 1))); break;
            case 'r':
                _scrollTop = Math.Clamp(Arg(0, 1) - 1, 0, Rows - 1);
                _scrollBottom = Math.Clamp(Arg(1, Rows) - 1, 0, Rows - 1);
                CursorX = 0;
                CursorY = 0;
                break;
            case 's': _saved = (CursorX, CursorY); break;
            case 'u': (CursorX, CursorY) = _saved; break;
            case 'h' or 'l':
                if (priv && p.Contains("25")) CursorVisible = final == 'h';
                break;
            case 'm': Sgr(p); break;
        }
    }

    private void ClearRange(int y0, int x0, int y1, int x1)
    {
        for (var y = y0; y <= y1; y++)
            for (var x = y == y0 ? x0 : 0; x <= (y == y1 ? x1 : Cols - 1); x++)
                _grid[y][x] = new Cell { Text = " ", Style = CellStyle.Default with { Bg = _style.Bg } };
    }

    private void Sgr(string[] p)
    {
        if (p.Length == 0 || p.Length == 1 && p[0] == "") { _style = CellStyle.Default; return; }
        for (var k = 0; k < p.Length; k++)
        {
            if (!int.TryParse(p[k], out var v)) v = 0;
            switch (v)
            {
                case 0: _style = CellStyle.Default; break;
                case 1: _style = _style with { Bold = true }; break;
                case 2: _style = _style with { Dim = true }; break;
                case 3: _style = _style with { Italic = true }; break;
                case 4: _style = _style with { Underline = true }; break;
                case 7: _style = _style with { Inverse = true }; break;
                case 9: _style = _style with { Strike = true }; break;
                case 22: _style = _style with { Bold = false, Dim = false }; break;
                case 23: _style = _style with { Italic = false }; break;
                case 24: _style = _style with { Underline = false }; break;
                case 27: _style = _style with { Inverse = false }; break;
                case 29: _style = _style with { Strike = false }; break;
                case >= 30 and <= 37: _style = _style with { Fg = Ansi16(v - 30) }; break;
                case >= 90 and <= 97: _style = _style with { Fg = Ansi16(v - 90 + 8) }; break;
                case >= 40 and <= 47: _style = _style with { Bg = Ansi16(v - 40) }; break;
                case >= 100 and <= 107: _style = _style with { Bg = Ansi16(v - 100 + 8) }; break;
                case 39: _style = _style with { Fg = -1 }; break;
                case 49: _style = _style with { Bg = -1 }; break;
                case 38 or 48:
                    int color;
                    if (k + 1 < p.Length && p[k + 1] == "2" && k + 4 < p.Length)
                    {
                        color = (int.Parse(p[k + 2]) << 16) | (int.Parse(p[k + 3]) << 8) | int.Parse(p[k + 4]);
                        k += 4;
                    }
                    else if (k + 1 < p.Length && p[k + 1] == "5" && k + 2 < p.Length)
                    {
                        color = Xterm256(int.Parse(p[k + 2]));
                        k += 2;
                    }
                    else break;
                    _style = v == 38 ? _style with { Fg = color } : _style with { Bg = color };
                    break;
            }
        }
    }

    private static readonly int[] Palette16 =
    [
        0x0C0C0C, 0xC50F1F, 0x13A10E, 0xC19C00, 0x0037DA, 0x881798, 0x3A96DD, 0xCCCCCC,
        0x767676, 0xE74856, 0x16C60C, 0xF9F1A5, 0x3B78FF, 0xB4009E, 0x61D6D6, 0xF2F2F2,
    ];

    private static int Ansi16(int i) => Palette16[i];

    private static int Xterm256(int n)
    {
        if (n < 16) return Palette16[n];
        if (n >= 232) { var g = 8 + (n - 232) * 10; return (g << 16) | (g << 8) | g; }
        n -= 16;
        int C(int x) => x == 0 ? 0 : 55 + x * 40;
        return (C(n / 36) << 16) | (C(n / 6 % 6) << 8) | C(n % 6);
    }
}
