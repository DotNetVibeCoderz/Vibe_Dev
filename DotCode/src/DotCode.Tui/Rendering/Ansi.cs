using System.Globalization;
using System.Text;

namespace DotCode.Tui.Rendering;

/// <summary>24-bit RGB color with graceful downgrade to 256/16 colors.</summary>
public readonly record struct Rgb(byte R, byte G, byte B)
{
    public static Rgb Hex(string hex)
    {
        hex = hex.TrimStart('#');
        if (hex.Length == 3) hex = string.Concat(hex.Select(c => new string(c, 2)));
        var v = uint.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return new Rgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    public Rgb Lerp(Rgb to, double t) => new(
        (byte)(R + (to.R - R) * t), (byte)(G + (to.G - G) * t), (byte)(B + (to.B - B) * t));

    public override string ToString() => $"#{R:X2}{G:X2}{B:X2}";
}

public enum ColorDepth { TrueColor, Ansi256, Ansi16, None }

/// <summary>ANSI escape helpers. Styles are emitted as SGR sequences and always reset explicitly, so a styled span
/// never bleeds into the next one.</summary>
public static class Ansi
{
    public const string Esc = "\u001b[";
    public const string Reset = "\u001b[0m";
    public const string Bold = "\u001b[1m";
    public const string Dim = "\u001b[2m";
    public const string Italic = "\u001b[3m";
    public const string Underline = "\u001b[4m";
    public const string Inverse = "\u001b[7m";
    public const string Strike = "\u001b[9m";
    public const string HideCursor = "\u001b[?25l";
    public const string ShowCursor = "\u001b[?25h";
    public const string ClearLine = "\u001b[2K";
    public const string ClearToEnd = "\u001b[J";
    public const string ClearScreen = "\u001b[2J\u001b[3J\u001b[H";
    public const string SyncStart = "\u001b[?2026h";
    public const string SyncEnd = "\u001b[?2026l";
    public const string BracketedPasteOn = "\u001b[?2004h";
    public const string BracketedPasteOff = "\u001b[?2004l";

    /// <summary>Color depth supported by the terminal (detected once).</summary>
    public static ColorDepth DetectedDepth { get; } = DetectDepth();
    public static ColorDepth Depth { get; set; } = DetectedDepth;

    private static ColorDepth DetectDepth()
    {
        if (Environment.GetEnvironmentVariable("NO_COLOR") is { Length: > 0 }) return ColorDepth.None;
        var colorterm = Environment.GetEnvironmentVariable("COLORTERM") ?? "";
        if (colorterm.Contains("truecolor", StringComparison.OrdinalIgnoreCase) || colorterm.Contains("24bit", StringComparison.OrdinalIgnoreCase)) return ColorDepth.TrueColor;
        // Windows Terminal, VS Code, conhost (Win10+) and most modern terminals support 24-bit color.
        if (OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("WT_SESSION") is not null || Environment.GetEnvironmentVariable("TERM_PROGRAM") is not null) return ColorDepth.TrueColor;
        var term = Environment.GetEnvironmentVariable("TERM") ?? "";
        if (term.Contains("256")) return ColorDepth.Ansi256;
        return term.Length == 0 || term == "dumb" ? ColorDepth.Ansi16 : ColorDepth.TrueColor;
    }

    public static string Fg(Rgb c) => Depth switch
    {
        ColorDepth.TrueColor => $"\u001b[38;2;{c.R};{c.G};{c.B}m",
        ColorDepth.Ansi256 => $"\u001b[38;5;{To256(c)}m",
        ColorDepth.Ansi16 => $"\u001b[{30 + To16(c) % 8 + (To16(c) >= 8 ? 60 : 0)}m",
        _ => "",
    };

    public static string Bg(Rgb c) => Depth switch
    {
        ColorDepth.TrueColor => $"\u001b[48;2;{c.R};{c.G};{c.B}m",
        ColorDepth.Ansi256 => $"\u001b[48;5;{To256(c)}m",
        ColorDepth.Ansi16 => $"\u001b[{40 + To16(c) % 8 + (To16(c) >= 8 ? 60 : 0)}m",
        _ => "",
    };

    public static string Color(string text, Rgb c) => Depth == ColorDepth.None ? text : Fg(c) + text + Reset;
    public static string Styled(string text, string style) => Depth == ColorDepth.None && style.Contains("38;") ? text : style + text + Reset;

    private static int To256(Rgb c)
    {
        if (c.R == c.G && c.G == c.B)
        {
            if (c.R < 8) return 16;
            if (c.R > 248) return 231;
            return (int)Math.Round((c.R - 8) / 247.0 * 24) + 232;
        }
        int Q(byte v) => v < 48 ? 0 : v < 115 ? 1 : (v - 35) / 40;
        return 16 + 36 * Q(c.R) + 6 * Q(c.G) + Q(c.B);
    }

    private static int To16(Rgb c)
    {
        var bright = (c.R + c.G + c.B) / 3 > 140;
        var idx = (c.B > 110 ? 4 : 0) | (c.G > 110 ? 2 : 0) | (c.R > 110 ? 1 : 0);
        return idx + (bright && idx != 0 ? 8 : 0);
    }

    public static string CursorUp(int n) => n > 0 ? $"\u001b[{n}A" : "";
    public static string CursorDown(int n) => n > 0 ? $"\u001b[{n}B" : "";
    public static string CursorColumn(int col) => $"\u001b[{col + 1}G";

    /// <summary>OSC 8 hyperlink.</summary>
    public static string Link(string text, string url) => $"\u001b]8;;{url}\u001b\\{text}\u001b]8;;\u001b\\";

    /// <summary>Removes ANSI escape sequences (CSI and OSC).</summary>
    public static string Strip(string s)
    {
        if (s.IndexOf('\u001b') < 0) return s;
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] != '\u001b') { sb.Append(s[i]); continue; }
            if (i + 1 >= s.Length) break;
            if (s[i + 1] == '[')
            {
                i += 2;
                while (i < s.Length && !(s[i] >= '@' && s[i] <= '~')) i++;
            }
            else if (s[i + 1] == ']')
            {
                i += 2;
                while (i < s.Length && s[i] != '\u0007' && !(s[i] == '\u001b' && i + 1 < s.Length && s[i + 1] == '\\')) i++;
                if (i < s.Length && s[i] == '\u001b') i++;
            }
            else i++;
        }
        return sb.ToString();
    }
}
