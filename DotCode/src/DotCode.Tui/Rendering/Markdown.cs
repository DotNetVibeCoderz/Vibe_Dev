using System.Text;
using System.Text.RegularExpressions;
using DotCode.Tui.Themes;

namespace DotCode.Tui.Rendering;

/// <summary>Terminal markdown renderer (GFM subset): headings, emphasis, inline code, links, lists, task lists,
/// block quotes, tables, rules and fenced code blocks with syntax highlighting. Output is pre-wrapped lines.</summary>
public static partial class Markdown
{
    [GeneratedRegex(@"`([^`\n]+)`")]
    private static partial Regex InlineCodeRx();
    [GeneratedRegex(@"\*\*(?=\S)(.+?)(?<=\S)\*\*|__(?=\S)(.+?)(?<=\S)__")]
    private static partial Regex BoldRx();
    [GeneratedRegex(@"(?<![\*\w])\*(?=\S)([^*\n]+?)(?<=\S)\*(?!\*)|(?<![_\w])_(?=\S)([^_\n]+?)(?<=\S)_(?![_\w])")]
    private static partial Regex ItalicRx();
    [GeneratedRegex(@"~~(.+?)~~")]
    private static partial Regex StrikeRx();
    [GeneratedRegex(@"\[([^\]]+)\]\((\S+?)\)")]
    private static partial Regex LinkRx();
    [GeneratedRegex(@"^(\s*)([-*+]|\d+[.)])\s+(\[[ xX]\]\s+)?(.*)$")]
    private static partial Regex ListRx();
    [GeneratedRegex(@"^\s*\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?\s*$")]
    private static partial Regex TableSepRx();

    public static List<string> Render(string markdown, int width, Theme theme)
    {
        var lines = new List<string>();
        var src = markdown.Replace("\r\n", "\n").Split('\n');
        var i = 0;
        while (i < src.Length)
        {
            var line = src[i];
            var trimmed = line.TrimStart();

            // Fenced code block
            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                var fence = trimmed[..3];
                var lang = trimmed[3..].Trim();
                var code = new List<string>();
                i++;
                while (i < src.Length && !src[i].TrimStart().StartsWith(fence, StringComparison.Ordinal)) code.Add(src[i++]);
                i++;
                foreach (var cl in Highlighter.Highlight(string.Join('\n', code), lang, theme).Split('\n'))
                    lines.AddRange(TextWidth.Wrap(cl, width));
                continue;
            }

            // Table
            if (line.Contains('|') && i + 1 < src.Length && TableSepRx().IsMatch(src[i + 1]))
            {
                var rows = new List<string[]> { SplitRow(line) };
                i += 2;
                while (i < src.Length && src[i].Contains('|') && src[i].Trim().Length > 0) rows.Add(SplitRow(src[i++]));
                lines.AddRange(RenderTable(rows, width, theme));
                continue;
            }

            if (trimmed.Length == 0)
            {
                if (lines.Count > 0 && lines[^1].Length > 0) lines.Add("");
                i++;
                continue;
            }

            // Heading
            if (trimmed.StartsWith('#'))
            {
                var level = trimmed.TakeWhile(c => c == '#').Count();
                if (level <= 6 && trimmed.Length > level && trimmed[level] == ' ')
                {
                    var text = Inline(trimmed[(level + 1)..].Trim(), theme);
                    var styled = level == 1 ? Ansi.Bold + Ansi.Underline + text + Ansi.Reset : level == 2 ? Ansi.Bold + text + Ansi.Reset : Ansi.Bold + text + Ansi.Reset;
                    lines.AddRange(TextWidth.Wrap(styled, width));
                    i++;
                    continue;
                }
            }

            // Horizontal rule
            if (trimmed is "---" or "***" or "___" || (trimmed.Length >= 3 && trimmed.All(c => c == '-')))
            {
                lines.Add(theme.Faint(new string(theme.Ascii ? '-' : '─', Math.Min(width, 60))));
                i++;
                continue;
            }

            // Block quote
            if (trimmed.StartsWith('>'))
            {
                var text = Inline(trimmed.TrimStart('>').TrimStart(), theme);
                foreach (var w in TextWidth.Wrap(Ansi.Italic + text + Ansi.Reset, width - 2))
                    lines.Add(theme.Faint(theme.Ascii ? "| " : "▎ ") + theme.Dim(w));
                i++;
                continue;
            }

            // List item
            var lm = ListRx().Match(line);
            if (lm.Success)
            {
                var indent = lm.Groups[1].Value.Length / 2 * 2;
                var marker = lm.Groups[2].Value;
                var bullet = char.IsDigit(marker[0]) ? marker : (indent == 0 ? "-" : "◦");
                if (theme.Ascii && bullet == "◦") bullet = "-";
                var task = lm.Groups[3].Value;
                var content = lm.Groups[4].Value;
                // Continuation lines belong to the item.
                while (i + 1 < src.Length && src[i + 1].StartsWith(new string(' ', indent + 2), StringComparison.Ordinal) && !ListRx().IsMatch(src[i + 1]) && src[i + 1].Trim().Length > 0)
                    content += " " + src[++i].Trim();
                var prefix = new string(' ', indent) + bullet + " ";
                if (task.Length > 0) prefix += task.Contains('x', StringComparison.OrdinalIgnoreCase) ? theme.C(theme.Glyphs.TodoDone, theme.Success) + " " : theme.Glyphs.TodoOpen + " ";
                var pw = TextWidth.Of(prefix);
                var wrapped = TextWidth.Wrap(Inline(content, theme), Math.Max(10, width - pw));
                for (var k = 0; k < wrapped.Count; k++) lines.Add((k == 0 ? prefix : new string(' ', pw)) + wrapped[k]);
                i++;
                continue;
            }

            // Paragraph: join soft-wrapped lines
            var para = new StringBuilder(trimmed);
            while (i + 1 < src.Length && src[i + 1].Trim().Length > 0 && !IsBlockStart(src[i + 1]) && !src[i].EndsWith("  ", StringComparison.Ordinal))
                para.Append(' ').Append(src[++i].Trim());
            lines.AddRange(TextWidth.Wrap(Inline(para.ToString(), theme), width));
            i++;
        }
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    private static bool IsBlockStart(string line)
    {
        var t = line.TrimStart();
        return t.StartsWith('#') || t.StartsWith("```", StringComparison.Ordinal) || t.StartsWith('>') || t.StartsWith('|') || ListRx().IsMatch(line) || t is "---" or "***";
    }

    public static string Inline(string text, Theme theme)
    {
        // Protect inline code spans from further formatting.
        var codes = new List<string>();
        text = InlineCodeRx().Replace(text, m =>
        {
            codes.Add(theme.C(m.Groups[1].Value, theme.InlineCode));
            return $"\u0001{codes.Count - 1}\u0002";
        });
        text = LinkRx().Replace(text, m => m.Groups[1].Value == m.Groups[2].Value
            ? Ansi.Link(theme.C(m.Groups[2].Value, theme.Link), m.Groups[2].Value)
            : Ansi.Link(Ansi.Underline + m.Groups[1].Value + Ansi.Reset, m.Groups[2].Value) + " " + theme.Faint("(" + m.Groups[2].Value + ")"));
        text = BoldRx().Replace(text, m => Ansi.Bold + (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value) + Ansi.Reset);
        text = ItalicRx().Replace(text, m => Ansi.Italic + (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value) + Ansi.Reset);
        text = StrikeRx().Replace(text, m => Ansi.Strike + m.Groups[1].Value + Ansi.Reset);
        if (codes.Count > 0)
            text = Regex.Replace(text, "\u0001(\\d+)\u0002", m => codes[int.Parse(m.Groups[1].Value)]);
        return text;
    }

    private static string[] SplitRow(string line)
    {
        var t = line.Trim();
        if (t.StartsWith('|')) t = t[1..];
        if (t.EndsWith('|')) t = t[..^1];
        return t.Split('|').Select(c => c.Trim()).ToArray();
    }

    private static IEnumerable<string> RenderTable(List<string[]> rows, int width, Theme theme)
    {
        var cols = rows.Max(r => r.Length);
        var rendered = rows.Select(r => Enumerable.Range(0, cols).Select(c => Inline(c < r.Length ? r[c] : "", theme)).ToArray()).ToList();
        var widths = Enumerable.Range(0, cols).Select(c => rendered.Max(r => TextWidth.Of(r[c]))).ToArray();
        var total = widths.Sum() + cols * 3 + 1;
        if (total > width)
        {
            // Shrink the widest columns until the table fits.
            while (widths.Sum() + cols * 3 + 1 > width && widths.Max() > 6)
                widths[Array.IndexOf(widths, widths.Max())]--;
        }
        var b = theme.Ascii ? BorderSet.AsciiBox : BorderSet.Single;
        string Rule(string l, string m, string r) => theme.Faint(l + string.Join(m, widths.Select(w => new string(b.Horizontal[0], w + 2))) + r);
        yield return Rule(theme.Ascii ? "+" : "┌", theme.Ascii ? "+" : "┬", theme.Ascii ? "+" : "┐");
        for (var ri = 0; ri < rendered.Count; ri++)
        {
            var row = rendered[ri];
            var cells = row.Select((c, ci) => " " + TextWidth.Pad(ri == 0 ? Ansi.Bold + c + Ansi.Reset : c, widths[ci]) + " ");
            yield return theme.Faint(b.Vertical) + string.Join(theme.Faint(b.Vertical), cells) + theme.Faint(b.Vertical);
            if (ri == 0) yield return Rule(theme.Ascii ? "+" : "├", theme.Ascii ? "+" : "┼", theme.Ascii ? "+" : "┤");
        }
        yield return Rule(theme.Ascii ? "+" : "└", theme.Ascii ? "+" : "┴", theme.Ascii ? "+" : "┘");
    }
}

/// <summary>Lightweight lexical highlighter (keywords, strings, comments, numbers, types) for common languages.</summary>
public static partial class Highlighter
{
    private static readonly Dictionary<string, HashSet<string>> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cs"] = ["using", "namespace", "class", "record", "struct", "interface", "enum", "public", "private", "protected", "internal", "static", "readonly", "sealed", "abstract", "virtual", "override", "async", "await", "return", "if", "else", "for", "foreach", "while", "do", "switch", "case", "default", "break", "continue", "new", "var", "void", "null", "true", "false", "this", "base", "try", "catch", "finally", "throw", "in", "out", "ref", "is", "as", "get", "set", "init", "required", "const", "yield", "partial", "where", "string", "int", "long", "bool", "double", "decimal", "object", "char", "byte", "float", "when", "with", "not", "and", "or", "field", "extension"],
        ["js"] = ["const", "let", "var", "function", "return", "if", "else", "for", "while", "do", "switch", "case", "default", "break", "continue", "new", "class", "extends", "import", "export", "from", "async", "await", "try", "catch", "finally", "throw", "typeof", "instanceof", "null", "undefined", "true", "false", "this", "of", "in", "yield", "static", "get", "set", "interface", "type", "enum", "implements", "public", "private", "readonly", "as", "declare", "namespace", "keyof", "satisfies"],
        ["py"] = ["def", "class", "return", "if", "elif", "else", "for", "while", "in", "not", "and", "or", "is", "None", "True", "False", "import", "from", "as", "with", "try", "except", "finally", "raise", "lambda", "yield", "async", "await", "pass", "break", "continue", "global", "nonlocal", "self", "print"],
        ["go"] = ["package", "import", "func", "return", "if", "else", "for", "range", "switch", "case", "default", "break", "continue", "go", "defer", "chan", "select", "struct", "interface", "type", "var", "const", "map", "nil", "true", "false", "make", "new", "err"],
        ["rust"] = ["fn", "let", "mut", "pub", "struct", "enum", "impl", "trait", "use", "mod", "match", "if", "else", "for", "while", "loop", "return", "self", "Self", "crate", "super", "async", "await", "move", "ref", "true", "false", "Some", "None", "Ok", "Err", "where", "dyn", "const", "static", "unsafe"],
        ["java"] = ["package", "import", "class", "interface", "enum", "public", "private", "protected", "static", "final", "abstract", "void", "return", "if", "else", "for", "while", "do", "switch", "case", "default", "break", "continue", "new", "null", "true", "false", "this", "super", "try", "catch", "finally", "throw", "throws", "extends", "implements", "var", "record"],
        ["sh"] = ["if", "then", "else", "elif", "fi", "for", "while", "do", "done", "case", "esac", "in", "function", "return", "export", "local", "echo", "cd", "exit", "set", "source", "sudo"],
        ["ps"] = ["function", "param", "if", "else", "elseif", "foreach", "for", "while", "switch", "return", "try", "catch", "finally", "throw", "$true", "$false", "$null", "begin", "process", "end"],
        ["sql"] = ["select", "from", "where", "insert", "into", "values", "update", "set", "delete", "create", "table", "drop", "alter", "join", "left", "right", "inner", "outer", "on", "group", "by", "order", "having", "limit", "and", "or", "not", "null", "as", "distinct", "primary", "key", "index"],
    };

    private static string? Family(string lang) => lang.ToLowerInvariant() switch
    {
        "cs" or "csharp" or "c#" => "cs",
        "js" or "javascript" or "ts" or "typescript" or "tsx" or "jsx" or "mjs" => "js",
        "py" or "python" => "py",
        "go" or "golang" => "go",
        "rs" or "rust" => "rust",
        "java" or "kotlin" or "kt" => "java",
        "sh" or "bash" or "shell" or "zsh" or "console" => "sh",
        "ps1" or "powershell" or "pwsh" => "ps",
        "sql" => "sql",
        "json" or "jsonc" => "json",
        "html" or "xml" or "csproj" or "xaml" or "svg" => "xml",
        "css" or "scss" => "css",
        "yaml" or "yml" or "toml" or "ini" => "yaml",
        _ => null,
    };

    [GeneratedRegex(@"(?<comment>//[^\n]*|#(?![{!])[^\n]*|/\*.*?\*/|--[^\n]*)|(?<string>""(?:[^""\\\n]|\\.)*""|'(?:[^'\\\n]|\\.)*'|`[^`]*`)|(?<number>\b\d+(?:\.\d+)?[fFdDmMlL]?\b)|(?<word>[A-Za-z_$][A-Za-z0-9_$]*)", RegexOptions.Singleline)]
    private static partial Regex TokenRx();

    [GeneratedRegex(@"(?<tag></?[A-Za-z][\w:.-]*)|(?<attr>\s[\w:-]+(?==))|(?<string>""[^""]*""|'[^']*')|(?<comment><!--.*?-->)", RegexOptions.Singleline)]
    private static partial Regex XmlRx();

    public static string Highlight(string code, string lang, Theme theme)
    {
        var family = Family(lang);
        if (family is null || code.Length > 50_000) return code;
        if (family == "xml")
        {
            return XmlRx().Replace(code, m =>
                m.Groups["comment"].Success ? theme.C(m.Value, theme.CodeComment) :
                m.Groups["tag"].Success ? theme.C(m.Value, theme.CodeKeyword) :
                m.Groups["attr"].Success ? theme.C(m.Value, theme.CodeType) :
                theme.C(m.Value, theme.CodeString));
        }
        var keywords = Keywords.GetValueOrDefault(family) ?? [];
        var hashComments = family is "py" or "sh" or "ps" or "yaml";
        var dashComments = family == "sql";
        var sb = new StringBuilder(code.Length * 2);
        var last = 0;
        foreach (Match m in TokenRx().Matches(code))
        {
            sb.Append(code, last, m.Index - last);
            last = m.Index + m.Length;
            if (m.Groups["comment"].Success)
            {
                var v = m.Value;
                var isComment = v.StartsWith("//", StringComparison.Ordinal) && family is not ("py" or "sh" or "yaml" or "sql") ||
                                v.StartsWith("/*", StringComparison.Ordinal) && family is not ("py" or "sh") ||
                                v.StartsWith('#') && hashComments || v.StartsWith("--", StringComparison.Ordinal) && dashComments;
                sb.Append(isComment ? theme.C(v, theme.CodeComment) : v);
            }
            else if (m.Groups["string"].Success) sb.Append(theme.C(m.Value, theme.CodeString));
            else if (m.Groups["number"].Success) sb.Append(theme.C(m.Value, theme.CodeNumber));
            else if (keywords.Contains(m.Value) || family == "sql" && keywords.Contains(m.Value.ToLowerInvariant())) sb.Append(theme.C(m.Value, theme.CodeKeyword));
            else if (family is "json" or "yaml") sb.Append(m.Value is "true" or "false" or "null" ? theme.C(m.Value, theme.CodeKeyword) : m.Value);
            else if (char.IsUpper(m.Value[0]) && family is "cs" or "java" or "js" or "rust" or "go") sb.Append(theme.C(m.Value, theme.CodeType));
            else sb.Append(m.Value);
        }
        sb.Append(code, last, code.Length - last);
        // Colors must not span lines (each line is rendered independently).
        return string.Join('\n', sb.ToString().Split('\n').Select(l => l.Contains('\u001b') && !l.EndsWith(Ansi.Reset, StringComparison.Ordinal) ? l + Ansi.Reset : l));
    }
}
