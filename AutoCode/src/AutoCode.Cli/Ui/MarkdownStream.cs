// Auto Code — Gravicode Studios (Kang Fadhil)

using System.Text;
using Spectre.Console;

namespace AutoCode.Cli.Ui;

/// <summary>
/// Renders streamed assistant markdown to the terminal.
///
/// EN: buffered by line rather than by message. Waiting for a whole response before drawing it
/// turns a fast model into one that appears to hang; writing raw characters as they arrive gives up
/// every bit of structure the model took the trouble to express. A line is short enough that the
/// delay is imperceptible and long enough to style properly, so that is the unit.
///
/// Fenced code blocks are the exception: they buffer to the closing fence, because a frame cannot
/// be drawn around something whose width is not yet known.
///
/// ID: penyangga per baris, bukan per pesan. Menunggu seluruh respons membuat model cepat terasa
/// menggantung; menulis karakter mentah membuang seluruh struktur yang disusun model. Satu baris
/// cukup pendek agar jedanya tak terasa, dan cukup panjang untuk diberi gaya.
/// </summary>
public sealed class MarkdownStream(Theme theme, Glyphs glyphs, string indent = "  ")
{
    private readonly StringBuilder _line = new();
    private readonly List<string> _codeBlock = [];

    private bool _inCode;
    private string _codeLanguage = "";
    private bool _wroteAnything;

    /// <summary>True when at least one line has been emitted for the current message.</summary>
    public bool HasOutput => _wroteAnything;

    /// <summary>Feeds a streamed chunk. Complete lines are rendered; the remainder is held.</summary>
    public void Append(string chunk)
    {
        foreach (var ch in chunk)
        {
            if (ch == '\r')
                continue;

            if (ch == '\n')
            {
                EmitLine(_line.ToString());
                _line.Clear();
                continue;
            }

            _line.Append(ch);
        }
    }

    /// <summary>Flushes the trailing partial line and closes any unterminated code block.</summary>
    public void Flush()
    {
        if (_line.Length > 0)
        {
            EmitLine(_line.ToString());
            _line.Clear();
        }

        if (_inCode)
        {
            // The model stopped mid-block. Draw what arrived rather than swallowing it.
            FlushCodeBlock();
            _inCode = false;
        }
    }

    /// <summary>Resets between messages.</summary>
    public void Reset()
    {
        _line.Clear();
        _codeBlock.Clear();
        _inCode = false;
        _wroteAnything = false;
    }

    private void EmitLine(string raw)
    {
        var trimmed = raw.TrimEnd();

        if (trimmed.TrimStart().StartsWith("```", StringComparison.Ordinal))
        {
            if (_inCode)
            {
                FlushCodeBlock();
                _inCode = false;
            }
            else
            {
                _inCode = true;
                _codeLanguage = trimmed.TrimStart().TrimStart('`').Trim();
                _codeBlock.Clear();
            }

            return;
        }

        if (_inCode)
        {
            _codeBlock.Add(raw);
            return;
        }

        Write(StyleLine(trimmed));
    }

    /// <summary>Draws a fenced block with a left rule, so code is separable from prose at a glance.</summary>
    private void FlushCodeBlock()
    {
        if (_codeBlock.Count == 0)
            return;

        // Trim leading and trailing blank lines the fence usually carries.
        var lines = _codeBlock.SkipWhile(string.IsNullOrWhiteSpace).ToList();
        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1]))
            lines.RemoveAt(lines.Count - 1);

        if (lines.Count == 0)
        {
            _codeBlock.Clear();
            return;
        }

        Write("");

        if (_codeLanguage.Length > 0)
            Write($"[{theme.Faint}]{glyphs.TraceMid} {Markup.Escape(_codeLanguage)}[/]");

        foreach (var line in lines)
            Write($"[{theme.Faint}]{glyphs.TraceMid}[/] [{theme.Code}]{Markup.Escape(line)}[/]");

        Write("");
        _codeBlock.Clear();
    }

    /// <summary>Applies inline structure to one line of prose.</summary>
    private string StyleLine(string line)
    {
        if (line.Length == 0)
            return "";

        var content = line.TrimStart();
        var leading = line[..(line.Length - content.Length)];

        // Headings: the hashes are scaffolding for the writer, not for the reader.
        if (content.StartsWith('#'))
        {
            var text = content.TrimStart('#').TrimStart();
            return $"{leading}[{theme.Accent} bold]{StyleInline(text)}[/]";
        }

        // Horizontal rules read as noise in a stream; a faint break is enough.
        if (content is "---" or "***" or "___")
            return $"[{theme.Faint}]{new string('─', 40)}[/]";

        if (content.StartsWith("> ", StringComparison.Ordinal))
            return $"{leading}[{theme.Faint}]{glyphs.TraceMid}[/] [{theme.Muted}]{StyleInline(content[2..])}[/]";

        // Bullets: replace the markdown marker with a typographic one.
        if (content.StartsWith("- ", StringComparison.Ordinal) || content.StartsWith("* ", StringComparison.Ordinal))
            return $"{leading}[{theme.Faint}]•[/] {StyleInline(content[2..])}";

        return $"{leading}{StyleInline(content)}";
    }

    /// <summary>
    /// Styles inline spans. Everything not recognised is escaped, because model output routinely
    /// contains square brackets that Spectre would otherwise read as markup and refuse to print.
    /// </summary>
    private string StyleInline(string text)
    {
        var output = new StringBuilder(text.Length + 16);
        var i = 0;

        while (i < text.Length)
        {
            if (text[i] == '`')
            {
                var close = text.IndexOf('`', i + 1);
                if (close > i)
                {
                    var code = text[(i + 1)..close];
                    output.Append('[').Append(theme.Code).Append(']')
                          .Append(Markup.Escape(code))
                          .Append("[/]");
                    i = close + 1;
                    continue;
                }
            }

            if (text[i] == '*' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var close = text.IndexOf("**", i + 2, StringComparison.Ordinal);
                if (close > i)
                {
                    var bold = text[(i + 2)..close];
                    output.Append("[bold]").Append(Markup.Escape(bold)).Append("[/]");
                    i = close + 2;
                    continue;
                }
            }

            output.Append(Markup.Escape(text[i].ToString()));
            i++;
        }

        return output.ToString();
    }

    /// <summary>
    /// Writes one styled line, indented.
    ///
    /// EN: the indent is applied with a Padder rather than by prefixing spaces, because a prefixed
    /// line only indents its first row — the moment prose wraps, every continuation snaps back to
    /// column zero and the alignment that separates the agent's output from the user's prompt is
    /// lost. Padder wraps inside the padded box, so the block stays a block.
    /// ID: indentasi memakai Padder, bukan awalan spasi, karena awalan hanya berlaku pada baris
    /// pertama — begitu teks membungkus, baris lanjutannya kembali ke kolom nol dan perataannya
    /// rusak. Padder membungkus di dalam kotak sehingga blok tetap utuh.
    /// </summary>
    private void Write(string markup)
    {
        if (markup.Length == 0)
        {
            AnsiConsole.WriteLine();
        }
        else
        {
            AnsiConsole.Write(new Padder(new Markup(markup), new Padding(indent.Length, 0, 0, 0)));
        }

        _wroteAnything = true;
    }
}
