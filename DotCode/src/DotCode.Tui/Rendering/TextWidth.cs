using System.Globalization;
using System.Text;

namespace DotCode.Tui.Rendering;

/// <summary>Terminal display width of text (ANSI-aware): wide East-Asian characters and emoji take two columns,
/// combining marks and zero-width joiners take none. Used for wrapping and box layout.</summary>
public static class TextWidth
{
    public static int Of(string text)
    {
        var width = 0;
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '\u001b') { i = SkipEscape(text, i); continue; }
            if (char.IsHighSurrogate(c) && i + 1 < text.Length)
            {
                width += RuneWidth(char.ConvertToUtf32(c, text[i + 1]));
                i += 2;
                continue;
            }
            width += RuneWidth(c);
            i++;
        }
        return width;
    }

    internal static int SkipEscape(string s, int i)
    {
        if (i + 1 >= s.Length) return i + 1;
        if (s[i + 1] == '[')
        {
            i += 2;
            while (i < s.Length && !(s[i] >= '@' && s[i] <= '~')) i++;
            return i + 1;
        }
        if (s[i + 1] == ']')
        {
            i += 2;
            while (i < s.Length && s[i] != '\u0007' && !(s[i] == '\u001b' && i + 1 < s.Length && s[i + 1] == '\\')) i++;
            return s[i] == '\u001b' ? i + 2 : i + 1;
        }
        return i + 2;
    }

    public static int RuneWidth(int cp)
    {
        if (cp == 0 || cp == 0x200B || cp == 0x200C || cp == 0x200D || cp == 0xFE0F || cp == 0xFE0E) return 0;
        if (cp < 32 || cp >= 0x7F && cp < 0xA0) return 0;
        if (cp < 0x300) return 1;
        var cat = CharUnicodeInfo.GetUnicodeCategory(cp);
        if (cat is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format) return 0;
        if (IsWide(cp)) return 2;
        return 1;
    }

    private static bool IsWide(int cp) =>
        cp >= 0x1100 && cp <= 0x115F ||
        cp >= 0x2E80 && cp <= 0x303E ||
        cp >= 0x3041 && cp <= 0x33FF ||
        cp >= 0x3400 && cp <= 0x4DBF ||
        cp >= 0x4E00 && cp <= 0x9FFF ||
        cp >= 0xA000 && cp <= 0xA4CF ||
        cp >= 0xAC00 && cp <= 0xD7A3 ||
        cp >= 0xF900 && cp <= 0xFAFF ||
        cp >= 0xFE30 && cp <= 0xFE4F ||
        cp >= 0xFF00 && cp <= 0xFF60 ||
        cp >= 0xFFE0 && cp <= 0xFFE6 ||
        cp >= 0x1F300 && cp <= 0x1F64F ||
        cp >= 0x1F680 && cp <= 0x1F6FF ||
        cp >= 0x1F900 && cp <= 0x1F9FF ||
        cp >= 0x1FA70 && cp <= 0x1FAFF ||
        cp >= 0x20000 && cp <= 0x3FFFD ||
        cp is 0x2705 or 0x274C or 0x2728 or 0x26A1 or 0x2B50 or 0x231B or 0x23F3;

    /// <summary>Pads (or truncates) to an exact display width.</summary>
    public static string Pad(string text, int width)
    {
        var w = Of(text);
        if (w == width) return text;
        if (w < width) return text + new string(' ', width - w);
        return Truncate(text, width);
    }

    /// <summary>Truncates to a display width, appending "…" when cut. Keeps ANSI sequences intact.</summary>
    public static string Truncate(string text, int width, string ellipsis = "…")
    {
        if (width <= 0) return "";
        if (Of(text) <= width) return text;
        var sb = new StringBuilder();
        var w = 0;
        var limit = width - Of(ellipsis);
        var i = 0;
        var hasEscape = false;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '\u001b')
            {
                var end = SkipEscape(text, i);
                sb.Append(text, i, end - i);
                hasEscape = true;
                i = end;
                continue;
            }
            int cw, len = 1;
            if (char.IsHighSurrogate(c) && i + 1 < text.Length) { cw = RuneWidth(char.ConvertToUtf32(c, text[i + 1])); len = 2; }
            else cw = RuneWidth(c);
            if (w + cw > limit) break;
            sb.Append(text, i, len);
            w += cw;
            i += len;
        }
        sb.Append(ellipsis);
        if (hasEscape) sb.Append(Ansi.Reset);
        return sb.ToString();
    }

    /// <summary>Word-wraps styled text to a display width. ANSI state is carried across wrapped lines.</summary>
    public static List<string> Wrap(string text, int width)
    {
        var result = new List<string>();
        if (width < 4) width = 4;
        foreach (var rawLine in text.Split('\n'))
        {
            if (Of(rawLine) <= width) { result.Add(rawLine); continue; }
            var line = new StringBuilder();
            var lineWidth = 0;
            var activeStyle = new StringBuilder();
            var i = 0;
            var lastBreakPos = -1;
            var lastBreakWidth = 0;
            var lastBreakStyle = "";
            while (i < rawLine.Length)
            {
                var c = rawLine[i];
                if (c == '\u001b')
                {
                    var end = SkipEscape(rawLine, i);
                    var seq = rawLine[i..end];
                    line.Append(seq);
                    if (seq == Ansi.Reset) activeStyle.Clear(); else if (seq.EndsWith('m')) activeStyle.Append(seq);
                    i = end;
                    continue;
                }
                int cw, len = 1;
                if (char.IsHighSurrogate(c) && i + 1 < rawLine.Length) { cw = RuneWidth(char.ConvertToUtf32(c, rawLine[i + 1])); len = 2; }
                else cw = RuneWidth(c);

                if (lineWidth + cw > width)
                {
                    string emitted, carry;
                    if (lastBreakPos > 0 && lastBreakWidth > width / 3)
                    {
                        emitted = line.ToString(0, lastBreakPos);
                        carry = line.ToString(lastBreakPos, line.Length - lastBreakPos).TrimStart(' ');
                        result.Add(emitted + (activeStyle.Length > 0 || lastBreakStyle.Length > 0 ? Ansi.Reset : ""));
                        line.Clear().Append(lastBreakStyle).Append(carry);
                        lineWidth = Of(carry);
                    }
                    else
                    {
                        result.Add(line + (activeStyle.Length > 0 ? Ansi.Reset : ""));
                        line.Clear().Append(activeStyle);
                        lineWidth = 0;
                    }
                    lastBreakPos = -1;
                }
                line.Append(rawLine, i, len);
                lineWidth += cw;
                if (c == ' ')
                {
                    lastBreakPos = line.Length;
                    lastBreakWidth = lineWidth;
                    lastBreakStyle = activeStyle.ToString();
                }
                i += len;
            }
            result.Add(line.ToString());
        }
        return result;
    }
}
