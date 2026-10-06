
namespace Marbots.Cli;

/// <summary>Configurable CLI palette. Stored in ~/.marbots/cli-theme.</summary>
internal sealed record Theme(string Name, string Accent, string Ok, string Warn, string Error, string Dim, string Bot, bool Unicode)
{
    public static readonly string[] Names = ["default", "aurora", "matrix", "mono", "high-contrast"];

    /// <summary>No escape codes at all (NO_COLOR or redirected output).</summary>
    public static readonly Theme Plain = new("plain", "", "", "", "", "", "", false);

    private static string Rgb(int r, int g, int b) => $"\u001b[38;2;{r};{g};{b}m";

    public static Theme Get(string name) => name switch
    {
        "aurora" => new("aurora", Rgb(143, 157, 255), Rgb(60, 199, 166), Rgb(242, 180, 60), Rgb(255, 123, 111), Rgb(140, 150, 190), Rgb(196, 160, 255), true),
        "matrix" => new("matrix", Rgb(0, 255, 120), Rgb(0, 220, 100), Rgb(180, 255, 0), Rgb(255, 80, 80), Rgb(0, 140, 70), Rgb(0, 255, 160), true),
        "mono" => new("mono", "\u001b[1m", "", "\u001b[1m", "\u001b[1m", "\u001b[2m", "\u001b[1m", false),
        "high-contrast" => new("high-contrast", "\u001b[97;1m", "\u001b[92;1m", "\u001b[93;1m", "\u001b[91;1m", "\u001b[37m", "\u001b[96;1m", true),
        _ => new("default", Rgb(110, 125, 230), Rgb(40, 170, 140), Rgb(230, 165, 30), Rgb(230, 90, 80), Rgb(130, 138, 165), Rgb(150, 160, 255), true),
    };

    private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".marbots", "cli-theme");

    public static Theme Load()
    {
        if (Environment.GetEnvironmentVariable("NO_COLOR") is not null || Console.IsOutputRedirected) return Plain;
        try { return Get(File.Exists(FilePath) ? File.ReadAllText(FilePath).Trim() : "default"); }
        catch (IOException) { return Get("default"); }
    }

    public static void Save(string name)
    {
        if (!Names.Contains(name)) throw new ArgumentException($"Unknown theme '{name}'. Choose: {string.Join(", ", Names)}");
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, name);
    }
}

internal sealed class Ui(Theme t)
{
    private readonly string Reset = t.Name == "plain" ? "" : "\u001b[0m";
    private readonly string Bold = t.Name == "plain" ? "" : "\u001b[1m";

    public void Title(string s) => Console.WriteLine($"{t.Accent}{Bold}{s}{Reset}");
    public void Line(string s) => Console.WriteLine(s);
    public void Dim(string s) => Console.WriteLine($"{t.Dim}{s}{Reset}");
    public void Ok(string s) => Console.WriteLine($"{t.Ok}{(t.Unicode ? "✓" : "OK")} {s}{Reset}");
    public void Warn(string s) => Console.WriteLine($"{t.Warn}{(t.Unicode ? "⏸" : "!")} {s}{Reset}");
    public void Error(string s) => Console.Error.WriteLine($"{t.Error}{(t.Unicode ? "✖" : "ERR")} {s}{Reset}");
    public string Prompt(string s) => $"{t.Accent}{s}{Reset}";
    public string Bot(string name, string _) => $"{t.Bot}{(t.Unicode ? "● " : "* ")}{name}{Reset}";

    public void Row(params string[] cols) => Console.WriteLine(string.Join("  ", cols.Select((c, i) => i == 0 ? Pad(c, 28) : c)));

    public void Event(string glyph, string time, string bot, string type, string message) =>
        Console.WriteLine($"{t.Dim}{time}{Reset} {t.Accent}{glyph}{Reset} {t.Bot}{bot}{Reset} {t.Dim}{type}{Reset} {message}");

    public void Reply(string markdown) => Console.WriteLine(markdown);

    public SpinnerFrames Spinner() => new(t.Unicode ? ["◐", "◓", "◑", "◒"] : ["|", "/", "-", "\\"]);

    private static string Pad(string s, int width)
    {
        var visible = System.Text.RegularExpressions.Regex.Replace(s, "\u001b\\[[0-9;]*m", "").Length;
        return visible >= width ? s : s + new string(' ', width - visible);
    }
}

internal sealed class SpinnerFrames(string[] frames)
{
    private int _i;
    public string Next() => frames[_i++ % frames.Length];
}
