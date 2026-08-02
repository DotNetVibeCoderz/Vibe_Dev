// Auto Code — Gravicode Studios (Kang Fadhil)

using Spectre.Console;

namespace AutoCode.Cli.Ui;

/// <summary>
/// The wordmark and the robot.
///
/// EN: the mark is drawn once, on launch and in <c>/about</c>, and never again. A logo that
/// reappears every time you clear the screen stops being an identity and becomes clutter — so it
/// gets one generous moment and then gets out of the way.
///
/// The colour runs as a gradient across the wordmark's columns rather than tinting whole letters,
/// which is what stops block-character art reading as a 1997 BBS header.
///
/// ID: tanda ini digambar sekali saat mulai dan pada <c>/about</c>, tidak lebih. Logo yang muncul
/// berulang berhenti menjadi identitas dan berubah jadi kekacauan. Warnanya berupa gradien menyusuri
/// kolom, bukan mewarnai huruf utuh — itulah yang menjaganya tidak terasa seperti header BBS lama.
/// </summary>
public static class Mascot
{
    private static readonly string[] Wordmark =
    [
        "▄▀█ █░█ ▀█▀ █▀█   █▀▀ █▀█ █▀▄ █▀▀",
        "█▀█ █▄█ ░█░ █▄█   █▄▄ █▄█ █▄▀ ██▄",
    ];

    private static readonly string[] WordmarkAscii =
    [
        "  _   _   _ _____ ___    ___ ___  ___  ___ ",
        " /_\\ | | | |_   _/ _ \\  / __/ _ \\|   \\| __|",
        "/ _ \\| |_| | | || (_) || (_| (_) | |) | _| ",
    ];

    /// <summary>The robot. Idle by default; the eyes change with what the agent is doing.</summary>
    private static readonly string[] Robot =
    [
        "┌─────┐",
        "│ {0} │",
        "│  {1}  │",
        "└┬───┬┘",
    ];

    private static readonly string[] RobotAscii =
    [
        ".-----.",
        "| {0} |",
        "|  {1}  |",
        "'-+---+'",
    ];

    /// <summary>Draws the full mark: robot on the left, wordmark to its right, credit beneath.</summary>
    public static void Render(Theme theme, string version)
    {
        var unicode = AnsiConsole.Profile.Capabilities.Unicode;

        // Figlet is pure ASCII, so it works in any console — but it is wide. A narrow terminal
        // would wrap it into unreadable rubble, so that case gets the compact mark instead.
        if (TerminalWidth() >= 74 && TryRenderFiglet(theme, version))
            return;

        if (!unicode)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine(
                $"  [{theme.Accent}][[^_^]][/]  [{theme.Strong}]AUTO CODE[/]  [{theme.Faint}]v{version}[/]");
            AnsiConsole.MarkupLine($"          [{theme.Faint}]Gravicode Studios · Kang Fadhil[/]");
            AnsiConsole.WriteLine();
            return;
        }

        var word = Wordmark;
        var robot = BuildRobot(unicode, "◕ ◕", "‿");

        AnsiConsole.WriteLine();

        // Pad the shorter column so both blocks sit on the same baseline.
        var rows = Math.Max(word.Length, robot.Length);
        var wordTop = (rows - word.Length) / 2;
        var robotTop = (rows - robot.Length) / 2;

        for (var i = 0; i < rows; i++)
        {
            var robotLine = i >= robotTop && i - robotTop < robot.Length ? robot[i - robotTop] : new string(' ', 7);
            var wordIndex = i - wordTop;

            var wordLine = wordIndex >= 0 && wordIndex < word.Length
                ? Gradient(theme, word[wordIndex])
                : "";

            AnsiConsole.MarkupLine($"   [{theme.Accent}]{Markup.Escape(robotLine)}[/]   {wordLine}");
        }

        AnsiConsole.MarkupLine(
            $"              [{theme.Faint}]v{version}  ·  Gravicode Studios · Kang Fadhil[/]");
        AnsiConsole.WriteLine();
    }

    /// <summary>
    /// Draws the wordmark with Spectre's Figlet renderer, tinted per column.
    ///
    /// EN: FigletText paints in a single colour, so the art is rendered to a plain string first and
    /// the gradient applied afterwards, character by character. That is the only way to get a sweep
    /// across the letters rather than one flat hue — and it keeps Figlet's font handling rather than
    /// hand-drawing the glyphs.
    /// ID: FigletText hanya mendukung satu warna, jadi artnya digambar ke string polos lebih dulu
    /// lalu gradiennya diterapkan per karakter. Hanya dengan cara itu warnanya menyapu melintasi
    /// huruf, bukan satu warna datar.
    /// </summary>
    private static bool TryRenderFiglet(Theme theme, string version)
    {
        string[] lines;

        try
        {
            var writer = new StringWriter();

            // Render with colour off: the plain glyphs are what get tinted below.
            var plain = AnsiConsole.Create(new AnsiConsoleSettings
            {
                Out = new AnsiConsoleOutput(writer),
                ColorSystem = ColorSystemSupport.NoColors,
                Ansi = AnsiSupport.No,
                Interactive = InteractionSupport.No,
            });

            plain.Profile.Width = 200;
            plain.Write(new FigletText(FigletFont.Default, "AutoCode").LeftJustified());

            lines = writer.ToString()
                .ReplaceLineEndings("\n")
                .Split('\n')
                .Select(l => l.TrimEnd())
                .Where(l => l.Length > 0)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException)
        {
            return false;
        }

        if (lines.Length == 0)
            return false;

        AnsiConsole.WriteLine();

        foreach (var line in lines)
            AnsiConsole.MarkupLine("  " + Gradient(theme, line));

        AnsiConsole.MarkupLine(
            $"   [{theme.Accent}][[^_^]][/]  [{theme.Faint}]v{version}  ·  Gravicode Studios · Kang Fadhil[/]");
        AnsiConsole.WriteLine();

        return true;
    }

    /// <summary>
    /// The usable width, or a sane default when there is no window to measure.
    ///
    /// EN: Console.WindowWidth throws "The handle is invalid" the moment output is redirected,
    /// which is every piped invocation. Spectre's profile width is already resolved safely, so it
    /// is the one to ask.
    /// ID: Console.WindowWidth melempar galat begitu keluaran dialihkan — yaitu pada setiap
    /// pemanggilan lewat pipa. Lebar dari profil Spectre sudah aman untuk ditanyakan.
    /// </summary>
    private static int TerminalWidth()
    {
        try
        {
            return AnsiConsole.Profile.Width;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            return 80;
        }
    }

    private static string[] BuildRobot(bool unicode, string eyes, string mouth)
    {
        var template = unicode ? Robot : RobotAscii;
        var safeEyes = unicode ? eyes : "o o";
        var safeMouth = unicode ? mouth : "u";

        return [.. template.Select(line => string.Format(line, safeEyes, safeMouth))];
    }

    /// <summary>
    /// Tints each column a step further along a short amber ramp.
    ///
    /// EN: applied per column rather than per letter so the ramp reads as one continuous sweep
    /// across the mark. In a palette-free terminal every step collapses to the same style, and the
    /// art still renders — it just stops being a gradient.
    /// ID: diterapkan per kolom, bukan per huruf, agar gradasinya terbaca sebagai satu sapuan utuh.
    /// </summary>
    private static string Gradient(Theme theme, string line)
    {
        // A short ramp: too many stops turns a sweep into a rainbow.
        // Explicit hex rather than named colours. Spectre's names resolve through the 256-colour
        // table, and a console reporting a smaller palette rounds them somewhere unpredictable —
        // which is how a warm gradient ends up rendering cold. Hex pins the intent and lets the
        // terminal do the reducing.
        string[] ramp = theme == Theme.Plain
            ? ["default"]
            : ["#E8437A", "#F0533C", "#F5731F", "#F79A1E", "#F5B31E", "#F2CF3C", "#F5A33C", "#F0703C"];

        var builder = new System.Text.StringBuilder(line.Length * 12);
        var span = Math.Max(1, line.Length / ramp.Length);

        for (var i = 0; i < line.Length; i++)
        {
            var colour = ramp[Math.Min(i / span, ramp.Length - 1)];
            builder.Append('[').Append(colour).Append(']')
                   .Append(Markup.Escape(line[i].ToString()))
                   .Append("[/]");
        }

        return builder.ToString();
    }
}
