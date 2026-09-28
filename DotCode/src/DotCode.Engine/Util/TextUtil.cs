using System.Text;
using DotCode.Abstractions;

namespace DotCode.Engine.Util;

public static class TextUtil
{
    /// <summary>Rough token estimate (≈4 chars/token for code and English; images ≈1.6k tokens).</summary>
    public static long EstimateTokens(string text) => (text.Length + 3) / 4;

    public static long EstimateTokens(Message m) => m.Content.Sum(EstimateTokens) + 4;

    public static long EstimateTokens(ContentPart p) => p switch
    {
        TextPart t => EstimateTokens(t.Text),
        ThinkingPart th => EstimateTokens(th.Text),
        ToolUsePart tu => EstimateTokens(tu.Input.GetRawText()) + 10,
        ToolResultPart tr => tr.Content.Sum(EstimateTokens) + 10,
        ImagePart => 1600,
        DocumentPart d => d.Base64Data.Length / 6,
        _ => 10,
    };

    /// <summary>Keeps head and tail of long output with a marker in the middle.</summary>
    public static string Truncate(string text, int maxChars)
    {
        if (text.Length <= maxChars) return text;
        var head = maxChars * 2 / 3;
        var tail = maxChars - head;
        var omittedLines = text.AsSpan(head, text.Length - head - tail).Count('\n');
        return $"{text[..head]}\n\n... [{text.Length - maxChars:N0} characters / {omittedLines} lines truncated] ...\n\n{text[^tail..]}";
    }

    public static string FirstLine(string text, int max = 80)
    {
        var nl = text.IndexOfAny(['\r', '\n']);
        var line = nl >= 0 ? text[..nl] : text;
        return line.Length > max ? line[..(max - 1)] + "…" : line;
    }

    public static string Plural(long n, string singular, string? plural = null) =>
        $"{n} {(n == 1 ? singular : plural ?? singular + "s")}";

    public static string FormatTokens(long n) => n switch
    {
        >= 1_000_000 => $"{n / 1_000_000.0:0.#}M",
        >= 1_000 => $"{n / 1000.0:0.#}k",
        _ => n.ToString(),
    };

    public static string FormatDuration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m" :
        t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}m {t.Seconds}s" :
        t.TotalSeconds >= 10 ? $"{(int)t.TotalSeconds}s" : $"{t.TotalSeconds:0.0}s";

    public static bool LooksBinary(ReadOnlySpan<byte> bytes)
    {
        var sample = bytes[..Math.Min(bytes.Length, 8000)];
        if (sample.Length >= 3 && sample[0] == 0xEF && sample[1] == 0xBB && sample[2] == 0xBF) return false;
        if (sample.Length >= 2 && (sample[0] == 0xFF && sample[1] == 0xFE || sample[0] == 0xFE && sample[1] == 0xFF)) return false;
        return sample.IndexOf((byte)0) >= 0;
    }

    public static string DetectNewline(string text)
    {
        var crlf = text.IndexOf("\r\n", StringComparison.Ordinal);
        var lf = text.IndexOf('\n');
        return crlf >= 0 && crlf < lf + 1 ? "\r\n" : "\n";
    }

    public static Encoding DetectEncoding(byte[] bytes, out bool hasBom)
    {
        hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) { hasBom = true; return Encoding.Unicode; }
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) { hasBom = true; return Encoding.BigEndianUnicode; }
        return new UTF8Encoding(hasBom);
    }
}
