// Auto Code — Gravicode Studios (Kang Fadhil)

using Spectre.Console;

namespace AutoCode.Cli.Ui;

/// <summary>
/// The input line, with slash-command completion.
///
/// EN: replaces Console.ReadLine because a completion menu has to react to every keystroke, and
/// ReadLine hands back only a finished line. The menu appears the moment a line starts with "/" and
/// disappears the moment it does not, so the feature costs nothing to anyone typing prose.
///
/// Falls back to ReadLine whenever input or output is redirected — piping a prompt into Auto Code
/// must keep working, and a key-by-key reader has nothing to read from a pipe.
///
/// ID: menggantikan Console.ReadLine karena menu pelengkapan harus bereaksi pada tiap ketukan,
/// sedangkan ReadLine hanya mengembalikan baris yang sudah selesai. Menu muncul saat baris diawali
/// "/" dan hilang saat tidak. Bila input dialihkan (pipa), kembali memakai ReadLine.
/// </summary>
public sealed class LineEditor(Theme theme, Glyphs glyphs, Func<IEnumerable<string>> skillNames)
{
    private const int MaxVisible = 8;

    /// <summary>Width reserved for the command and its argument hint, so descriptions line up.</summary>
    private const int UsageColumn = 24;

    private readonly List<string> _history = [];
    private int _historyCursor = -1;

    /// <summary>Reads one line, drawing the prompt and any completion menu.</summary>
    public string Read()
    {
        AnsiConsole.WriteLine();

        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            AnsiConsole.Markup($"  [{theme.Accent}]{glyphs.Prompt}[/] ");
            return Console.ReadLine() ?? "/exit";
        }

        var buffer = new System.Text.StringBuilder();
        var menuLines = 0;
        var selected = 0;

        Redraw(buffer.ToString(), ref menuLines, ref selected, reset: true);

        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            var matches = CurrentMatches(buffer.ToString());

            switch (key.Key)
            {
                case ConsoleKey.Enter:
                    // Enter accepts a highlighted completion first; a second Enter submits. That
                    // ordering means Enter never both completes and runs in one press.
                    if (matches.Count > 0 && selected >= 0 && IsCompleting(buffer.ToString()))
                    {
                        Replace(buffer, "/" + matches[selected].Name + " ");
                        selected = 0;
                        Redraw(buffer.ToString(), ref menuLines, ref selected, reset: false);
                        continue;
                    }

                    ClearMenu(ref menuLines);
                    Console.WriteLine();

                    var line = buffer.ToString().Trim();
                    if (line.Length > 0)
                        _history.Add(line);

                    _historyCursor = -1;
                    return line;

                case ConsoleKey.Tab when matches.Count > 0 && IsCompleting(buffer.ToString()):
                    {
                        // Tab fills in as far as every candidate agrees, the way a shell does; when
                        // there is only one candidate that completes it outright.
                        var shared = SlashCommandCatalog.CommonPrefix(matches);
                        var typed = buffer.ToString().TrimStart('/');

                        Replace(buffer, matches.Count == 1
                            ? "/" + matches[0].Name + " "
                            : "/" + (shared.Length > typed.Length ? shared : typed));

                        Redraw(buffer.ToString(), ref menuLines, ref selected, reset: false);
                        continue;
                    }

                case ConsoleKey.Escape:
                    Replace(buffer, "");
                    selected = 0;
                    Redraw(buffer.ToString(), ref menuLines, ref selected, reset: false);
                    continue;

                case ConsoleKey.Backspace:
                    if (buffer.Length > 0)
                        buffer.Remove(buffer.Length - 1, 1);
                    selected = 0;
                    Redraw(buffer.ToString(), ref menuLines, ref selected, reset: false);
                    continue;

                case ConsoleKey.DownArrow when matches.Count > 0 && IsCompleting(buffer.ToString()):
                    selected = (selected + 1) % Math.Min(matches.Count, MaxVisible);
                    Redraw(buffer.ToString(), ref menuLines, ref selected, reset: false);
                    continue;

                case ConsoleKey.UpArrow when matches.Count > 0 && IsCompleting(buffer.ToString()):
                    var visible = Math.Min(matches.Count, MaxVisible);
                    selected = (selected - 1 + visible) % visible;
                    Redraw(buffer.ToString(), ref menuLines, ref selected, reset: false);
                    continue;

                case ConsoleKey.UpArrow:
                    if (RecallHistory(buffer, -1))
                        Redraw(buffer.ToString(), ref menuLines, ref selected, reset: false);
                    continue;

                case ConsoleKey.DownArrow:
                    if (RecallHistory(buffer, +1))
                        Redraw(buffer.ToString(), ref menuLines, ref selected, reset: false);
                    continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                buffer.Append(key.KeyChar);
                selected = 0;
                Redraw(buffer.ToString(), ref menuLines, ref selected, reset: false);
            }
        }
    }

    /// <summary>The menu only applies while typing the command word itself, not its arguments.</summary>
    private static bool IsCompleting(string text) =>
        text.StartsWith('/') && !text.Contains(' ');

    private IReadOnlyList<SlashCommand> CurrentMatches(string text) =>
        IsCompleting(text) ? SlashCommandCatalog.Match(text, skillNames()) : [];

    private static void Replace(System.Text.StringBuilder buffer, string value)
    {
        buffer.Clear();
        buffer.Append(value);
    }

    private bool RecallHistory(System.Text.StringBuilder buffer, int direction)
    {
        if (_history.Count == 0)
            return false;

        if (_historyCursor == -1 && direction < 0)
            _historyCursor = _history.Count - 1;
        else
            _historyCursor = Math.Clamp(_historyCursor + direction, 0, _history.Count - 1);

        Replace(buffer, _history[_historyCursor]);
        return true;
    }

    /// <summary>
    /// Repaints the prompt and the menu beneath it.
    ///
    /// EN: the menu is drawn below the cursor and then the cursor is moved back up, so the input
    /// line stays where the user is looking. Every repaint clears the rows it drew last time —
    /// without that, a shrinking match list leaves its own debris on screen.
    /// ID: menu digambar di bawah kursor lalu kursor dikembalikan ke atas. Tiap penggambaran ulang
    /// membersihkan baris yang digambar sebelumnya, agar daftar yang menyusut tidak meninggalkan sisa.
    /// </summary>
    /// <summary>
    /// Repaints the prompt and the menu beneath it.
    ///
    /// EN: the rows are reserved before anything is measured. Printing the blank lines first forces
    /// whatever scrolling is going to happen to happen now, while nothing depends on a row number;
    /// only then is the prompt's row recorded. Measuring first and drawing after is what makes a
    /// terminal menu leave debris — the console scrolls between the two and every saved coordinate
    /// is off by one.
    /// ID: barisnya dipesan lebih dulu. Mencetak baris kosong memaksa penggulirannya terjadi sekarang,
    /// selagi belum ada yang bergantung pada nomor baris; barulah posisi prompt dicatat. Mengukur
    /// dulu lalu menggambar adalah penyebab menu terminal meninggalkan sisa.
    /// </summary>
    private void Redraw(string text, ref int menuLines, ref int selected, bool reset)
    {
        ClearMenu(ref menuLines);

        var matches = CurrentMatches(text);
        var visible = Math.Min(matches.Count, MaxVisible);
        var rows = visible > 0 ? visible + (matches.Count > visible ? 1 : 0) : 0;

        if (matches.Count > 0)
            selected = Math.Clamp(selected, 0, visible - 1);

        ClearRow();

        if (rows > 0)
        {
            // Reserve the space first, then come back. After this the row numbers are stable.
            for (var i = 0; i < rows; i++)
                Console.WriteLine();

            Console.SetCursorPosition(0, Math.Max(0, Console.CursorTop - rows));
        }

        var promptRow = Console.CursorTop;

        AnsiConsole.Markup($"  [{theme.Accent}]{glyphs.Prompt}[/] {Markup.Escape(text)}");
        var promptColumn = Console.CursorLeft;

        for (var i = 0; i < visible; i++)
        {
            var row = promptRow + 1 + i;
            if (row >= Console.BufferHeight)
                break;

            Console.SetCursorPosition(0, row);
            ClearRow();

            var match = matches[i];
            var chosen = i == selected;

            // No selection marker. The obvious glyph collides with the prompt's own, and two
            // identical arrows one line apart read as two prompts; weight and colour already
            // separate the highlighted row without adding a character to misread.
            var usage = chosen
                ? $"[{theme.Accent} bold]{Markup.Escape(match.Usage)}[/]"
                : $"[{theme.Muted}]{Markup.Escape(match.Usage)}[/]";

            // Descriptions start at one column so the eye can run straight down them. The usage
            // strings vary in width, so the padding is measured on the plain text, not the markup.
            var pad = new string(' ', Math.Max(1, UsageColumn - match.Usage.Length));

            // The description is the point of the menu: names tell you what exists, not what it
            // does, and /rewind versus /recap is not a guess anyone should have to make.
            AnsiConsole.Markup($"    {usage}{pad}[{theme.Faint}]{Markup.Escape(Fit(match.Summary, 52))}[/]");
        }

        if (matches.Count > visible && promptRow + 1 + visible < Console.BufferHeight)
        {
            Console.SetCursorPosition(0, promptRow + 1 + visible);
            ClearRow();
            AnsiConsole.Markup($"    [{theme.Faint}]… {matches.Count - visible} more — keep typing[/]");
        }

        menuLines = rows;
        Console.SetCursorPosition(promptColumn, promptRow);
    }

    /// <summary>Clips a summary so a long one cannot wrap and push the menu out of alignment.</summary>
    private static string Fit(string value, int width) =>
        value.Length <= width ? value : value[..(width - 1)] + "…";

    private static void ClearRow()
    {
        Console.Write('\r');
        Console.Write(new string(' ', Math.Max(1, Math.Min(Console.WindowWidth - 1, 200))));
        Console.Write('\r');
    }

    private static void ClearMenu(ref int menuLines)
    {
        if (menuLines == 0)
            return;

        var promptRow = Console.CursorTop;

        for (var i = 1; i <= menuLines; i++)
        {
            var row = promptRow + i;
            if (row >= Console.BufferHeight)
                break;

            Console.SetCursorPosition(0, row);
            ClearRow();
        }

        Console.SetCursorPosition(0, promptRow);
        menuLines = 0;
    }
}
