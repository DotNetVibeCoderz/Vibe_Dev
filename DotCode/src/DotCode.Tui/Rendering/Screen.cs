using System.Runtime.InteropServices;
using System.Text;

namespace DotCode.Tui.Rendering;

/// <summary>Ink-style terminal renderer: finished content is written once into the scrollback ("static"), while a
/// small "live" region at the bottom (streaming text, spinner, input box, dialogs) is erased and redrawn in place.
/// Every frame is written in one syscall inside a synchronized-update block to avoid flicker.</summary>
public sealed partial class Screen
{
    private readonly Stream _out;
    private readonly Lock _gate = new();
    private List<string> _live = [];
    private int _cursorRow;          // physical row of the cursor inside the live region
    private int _lastWidth;
    private bool _cursorVisible;

    public int Width { get; private set; } = 100;
    public int Height { get; private set; } = 30;
    public bool Interactive { get; }

    public Screen()
    {
        _out = Console.OpenStandardOutput();
        Interactive = !Console.IsOutputRedirected;
        if (OperatingSystem.IsWindows()) EnableVirtualTerminal();
        RefreshSize();
        _lastWidth = Width;
    }

    public bool RefreshSize()
    {
        int w = 100, h = 30;
        try
        {
            if (Interactive)
            {
                w = Console.WindowWidth;
                h = Console.WindowHeight;
            }
        }
        catch (IOException) { }
        if (Environment.GetEnvironmentVariable("DOTCODE_COLUMNS") is { } cols && int.TryParse(cols, out var c)) w = c;
        if (Environment.GetEnvironmentVariable("DOTCODE_LINES") is { } rows && int.TryParse(rows, out var r)) h = r;
        w = Math.Max(20, w);
        h = Math.Max(8, h);
        var changed = w != Width || h != Height;
        Width = w;
        Height = h;
        return changed;
    }

    /// <summary>Usable content width. One column is kept free so writing a full row never triggers a pending wrap.</summary>
    public int ContentWidth => Math.Max(20, Width - 1);

    public void Write(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        lock (_gate)
        {
            _out.Write(bytes);
            _out.Flush();
        }
    }

    /// <summary>Prints lines permanently above the live region, then redraws the live region.</summary>
    public void Commit(IEnumerable<string> lines) => Render(lines, _live, null, keepCursor: true);

    /// <summary>Redraws the live region. <paramref name="cursor"/> is (row, col) within <paramref name="live"/>.</summary>
    public void Render(IReadOnlyList<string> live, (int Row, int Col)? cursor) => Render([], live, cursor, keepCursor: false);

    private (int Row, int Col)? _lastCursor;

    private void Render(IEnumerable<string> committed, IReadOnlyList<string> live, (int Row, int Col)? cursor, bool keepCursor)
    {
        lock (_gate)
        {
            if (keepCursor) cursor = _lastCursor;
            var sb = new StringBuilder(4096);
            sb.Append(Ansi.SyncStart).Append(Ansi.HideCursor);

            // Erase the previous live region (physical rows computed for the current width, handling resizes).
            var prevRows = PhysicalRows(_live, Width);
            var up = Math.Min(_cursorRow, Math.Max(0, prevRows - 1));
            if (_lastWidth != Width)
            {
                // After a resize the terminal re-wrapped the old region; the cursor sits on its last row.
                up = Math.Min(prevRows - 1, Height - 1);
                _lastWidth = Width;
            }
            sb.Append('\r').Append(Ansi.CursorUp(up)).Append(Ansi.ClearToEnd);

            foreach (var line in committed)
                sb.Append(line).Append(Ansi.Reset).Append("\r\n");

            // Clip the live region to the viewport (the bottom part — input and dialogs — matters most).
            var maxRows = Math.Max(3, Height - 1);
            var visible = live.Count > maxRows ? live.Skip(live.Count - maxRows).ToList() : live.ToList();
            var clipOffset = live.Count - visible.Count;
            for (var i = 0; i < visible.Count; i++)
            {
                sb.Append(visible[i]).Append(Ansi.Reset);
                if (i < visible.Count - 1) sb.Append("\r\n");
            }
            _live = visible;

            var lastRow = Math.Max(0, visible.Count - 1);
            if (cursor is { } c && c.Row - clipOffset >= 0)
            {
                var row = c.Row - clipOffset;
                sb.Append(Ansi.CursorUp(lastRow - row)).Append('\r');
                if (c.Col > 0) sb.Append(Ansi.CursorColumn(c.Col));
                _cursorRow = row;
                sb.Append(Ansi.ShowCursor);
                _cursorVisible = true;
            }
            else
            {
                _cursorRow = lastRow;
                _cursorVisible = false;
            }
            _lastCursor = cursor;
            sb.Append(Ansi.SyncEnd);
            var bytes = Encoding.UTF8.GetBytes(sb.ToString());
            _out.Write(bytes);
            _out.Flush();
        }
    }

    private static int PhysicalRows(List<string> lines, int width)
    {
        if (lines.Count == 0) return 0;
        var rows = 0;
        foreach (var l in lines)
        {
            var w = TextWidth.Of(l);
            rows += w == 0 ? 1 : (w + width - 1) / width;
        }
        return rows;
    }

    /// <summary>Clears the live region (e.g. before exiting or handing the terminal to a child process).</summary>
    public void ClearLive()
    {
        Render([], [], null, keepCursor: false);
        Write(Ansi.ShowCursor);
    }

    public void ClearScreen()
    {
        lock (_gate)
        {
            _live = [];
            _cursorRow = 0;
            var bytes = Encoding.UTF8.GetBytes(Ansi.ClearScreen);
            _out.Write(bytes);
            _out.Flush();
        }
    }

    public bool CursorVisible => _cursorVisible;

    // ---------- Windows console mode ----------

    private const int StdOutputHandle = -11;
    private const int StdInputHandle = -10;
    private const uint EnableVirtualTerminalProcessing = 0x0004;
    private const uint EnableProcessedOutput = 0x0001;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint GetStdHandle(int handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetConsoleMode(nint handle, out uint mode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetConsoleMode(nint handle, uint mode);

    private static void EnableVirtualTerminal()
    {
        try
        {
            var h = GetStdHandle(StdOutputHandle);
            if (GetConsoleMode(h, out var mode))
                SetConsoleMode(h, mode | EnableVirtualTerminalProcessing | EnableProcessedOutput);
        }
        catch (Exception) { /* not a console */ }
    }
}
