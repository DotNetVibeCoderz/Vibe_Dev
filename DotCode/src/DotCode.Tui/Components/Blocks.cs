using System.Text;
using System.Text.Json;
using DotCode.Abstractions;
using DotCode.Engine;
using DotCode.Tui.Rendering;
using DotCode.Tui.Themes;

namespace DotCode.Tui.Components;

/// <summary>Renders transcript blocks (user prompts, assistant text, tool calls with results/diffs, todos, notices)
/// into terminal lines, following Claude Code's visual grammar: "●" for actions, "⎿" for results.</summary>
public sealed class Blocks(Theme theme)
{
    public Theme Theme { get; set; } = theme;
    private Theme T => Theme;

    public List<string> User(string text, int width)
    {
        var lines = new List<string>();
        var wrapped = TextWidth.Wrap(text.TrimEnd(), width - 3);
        for (var i = 0; i < wrapped.Count; i++)
        {
            var prefix = i == 0 ? T.Glyphs.Prompt + " " : "  ";
            var content = TextWidth.Pad(prefix + wrapped[i], width - 1);
            lines.Add(Ansi.Bg(T.UserMessageBg) + Ansi.Fg(T.Secondary) + content + Ansi.Reset);
        }
        return lines;
    }

    public List<string> BashInput(string command, int width)
    {
        var lines = new List<string>();
        foreach (var (l, i) in TextWidth.Wrap(command, width - 3).Select((l, i) => (l, i)))
            lines.Add(Ansi.Bg(T.UserMessageBg) + TextWidth.Pad((i == 0 ? T.C("!", T.Bash) + Ansi.Bg(T.UserMessageBg) + " " : "  ") + l, width - 1) + Ansi.Reset);
        return lines;
    }

    public List<string> Assistant(string markdown, int width)
    {
        var body = Markdown.Render(markdown.Trim(), width - 2, T);
        var lines = new List<string>(body.Count);
        for (var i = 0; i < body.Count; i++)
            lines.Add((i == 0 ? T.C(T.Glyphs.Dot, T.Text) + " " : "  ") + body[i]);
        return lines;
    }

    public List<string> Thinking(string text, int width, bool expanded)
    {
        var lines = new List<string> { T.Dim(Ansi.Italic + T.Glyphs.Star + " Thinking…" + Ansi.Reset) };
        if (expanded && text.Trim().Length > 0)
            foreach (var l in TextWidth.Wrap(text.Trim(), width - 2)) lines.Add("  " + T.Dim(Ansi.Italic + l + Ansi.Reset));
        return lines;
    }

    public string ToolHeader(string displayName, ToolState state, int width, int frame = 0)
    {
        var dotColor = state switch
        {
            ToolState.Success => T.Success,
            ToolState.Error => T.Error,
            ToolState.Rejected => T.Error,
            _ => T.Secondary,
        };
        // Running tools blink (like Claude Code) unless reduced motion is on.
        var dot = state == ToolState.Running && !T.ReducedMotion && frame % 2 == 1 ? " " : T.C(T.Glyphs.Dot, dotColor);
        var paren = displayName.IndexOf('(');
        var header = paren > 0 && displayName.EndsWith(')')
            ? T.B(displayName[..paren]) + "(" + displayName[(paren + 1)..^1] + ")"
            : T.B(displayName);
        return TextWidth.Truncate(dot + " " + header, width);
    }

    public string ResultPrefix(bool first) => first ? "  " + T.Faint(T.Glyphs.Result) + "  " : "     ";

    public List<string> ToolResultLines(ToolCompletedEvent e, int width, bool verbose)
    {
        var lines = new List<string>();
        var inner = width - 5;
        if (e.Rejected)
        {
            lines.Add(ResultPrefix(true) + T.C("User rejected " + DescribeRejected(e.Name), T.Error));
            return lines;
        }
        if (e.IsError)
        {
            var msg = e.Output.Trim();
            if (msg.StartsWith("Error: ", StringComparison.Ordinal)) msg = msg[7..];
            var errLines = msg.Split('\n');
            var shown = verbose ? errLines : errLines.Take(6).ToArray();
            for (var i = 0; i < shown.Length; i++)
                foreach (var w in TextWidth.Wrap(i == 0 && !e.Name.StartsWith("Bash", StringComparison.Ordinal) && !e.Name.StartsWith("PowerShell", StringComparison.Ordinal) ? "Error: " + shown[i] : shown[i], inner))
                    lines.Add(ResultPrefix(lines.Count == 0) + T.C(w, T.Error));
            if (errLines.Length > shown.Length) lines.Add(ResultPrefix(false) + T.Dim($"{T.Glyphs.Ellipsis} +{errLines.Length - shown.Length} lines (ctrl+o to expand)"));
            return lines;
        }

        if (e.Diff is { Length: > 0 } diff)
        {
            lines.Add(ResultPrefix(true) + Summary(e.Summary));
            // New files (additions only) get a short preview; real edits show up to 40 diff lines.
            var newFile = !diff.Split('\n').Any(l => l.StartsWith('-') && !l.StartsWith("---", StringComparison.Ordinal));
            lines.AddRange(DiffLines(diff, width - 5, verbose ? int.MaxValue : newFile ? 12 : 40).Select(l => "     " + l));
            return lines;
        }

        var output = e.Output.TrimEnd();
        var isShell = e.Name is "Bash" or "PowerShell" || e.Name.StartsWith("mcp__", StringComparison.Ordinal);
        if (isShell && output.Length > 0 && output != "(no output)")
        {
            var outLines = output.Split('\n');
            var max = verbose ? outLines.Length : 4;
            var shown = outLines.Take(max).ToList();
            for (var i = 0; i < shown.Count; i++)
            {
                var wrapped = TextWidth.Wrap(shown[i].TrimEnd('\r'), inner);
                foreach (var w in wrapped.Take(verbose ? int.MaxValue : 2)) lines.Add(ResultPrefix(lines.Count == 0) + w);
            }
            if (outLines.Length > max) lines.Add(ResultPrefix(false) + T.Dim($"{T.Glyphs.Ellipsis} +{outLines.Length - max} lines (ctrl+o to expand)"));
            return lines;
        }

        var summary = e.Summary.Length > 0 ? e.Summary : output.Length == 0 ? "(No content)" : TextUtilLocal.FirstLine(output, inner);
        lines.Add(ResultPrefix(true) + Summary(summary));
        if (verbose && output.Length > 0 && e.Summary.Length > 0 && e.Name is not ("Read"))
            foreach (var l in output.Split('\n').Take(200)) foreach (var w in TextWidth.Wrap(l, inner)) lines.Add(ResultPrefix(false) + T.Dim(w));
        return lines;
    }

    private static string DescribeRejected(string tool) => tool switch
    {
        "Edit" or "Write" or "NotebookEdit" => "update to file",
        "Bash" or "PowerShell" => "command",
        _ => "tool use",
    };

    /// <summary>Highlights the counts in "Updated X with 3 additions and 1 removal" / "Read 20 lines".</summary>
    private string Summary(string s)
    {
        var sb = new StringBuilder();
        var parts = s.Split(' ');
        for (var i = 0; i < parts.Length; i++)
        {
            if (i > 0) sb.Append(' ');
            var p = parts[i];
            var isNum = p.Length > 0 && p.All(char.IsDigit);
            var next = i + 1 < parts.Length ? parts[i + 1] : "";
            if (isNum && (next.StartsWith("line", StringComparison.Ordinal) || next.StartsWith("addition", StringComparison.Ordinal) || next.StartsWith("removal", StringComparison.Ordinal) || next.StartsWith("file", StringComparison.Ordinal) || next.StartsWith("match", StringComparison.Ordinal)))
                sb.Append(T.B(p));
            else if (i > 0 && (parts[i - 1] is "Updated" or "Wrote" or "Created" || parts[i - 1] == "to" && s.StartsWith("Wrote", StringComparison.Ordinal)))
                sb.Append(T.B(p));
            else sb.Append(T.Dim(p));
        }
        return sb.ToString();
    }

    /// <summary>Claude Code-style diff: line numbers, full-width red/green backgrounds for removed/added lines.</summary>
    public List<string> DiffLines(string diff, int width, int maxLines = 40)
    {
        var lines = new List<string>();
        int oldLine = 0, newLine = 0;
        var numWidth = 3;
        foreach (var l in diff.Split('\n'))
        {
            if (!l.StartsWith("@@", StringComparison.Ordinal)) continue;
            var plus = l.IndexOf('+');
            if (plus > 0 && int.TryParse(new string(l[(plus + 1)..].TakeWhile(char.IsDigit).ToArray()), out var n)) numWidth = Math.Max(numWidth, (n + 50).ToString().Length);
        }
        var firstHunk = true;
        foreach (var raw in diff.Split('\n'))
        {
            if (raw.StartsWith("---", StringComparison.Ordinal) || raw.StartsWith("+++", StringComparison.Ordinal) || raw.Length == 0 && lines.Count == 0) continue;
            if (raw.StartsWith("@@", StringComparison.Ordinal))
            {
                var parts = raw.Split(' ');
                oldLine = int.TryParse(parts[1].TrimStart('-').Split(',')[0], out var o) ? o : 1;
                newLine = int.TryParse(parts[2].TrimStart('+').Split(',')[0], out var nn) ? nn : 1;
                if (!firstHunk) lines.Add(T.Faint(new string(' ', numWidth) + " " + T.Glyphs.Ellipsis));
                firstHunk = false;
                continue;
            }
            if (raw.Length == 0) continue;
            var kind = raw[0];
            var text = raw.Length > 1 ? raw[1..].Replace("\t", "    ") : "";
            string num, body;
            switch (kind)
            {
                case '+':
                    num = newLine++.ToString().PadLeft(numWidth);
                    body = TextWidth.Pad($"{num} +{text}", width);
                    lines.Add(Ansi.Bg(T.DiffAddedBg) + Ansi.Fg(T.Text) + TextWidth.Truncate(body, width) + Ansi.Reset);
                    break;
                case '-':
                    num = oldLine++.ToString().PadLeft(numWidth);
                    body = TextWidth.Pad($"{num} -{text}", width);
                    lines.Add(Ansi.Bg(T.DiffRemovedBg) + Ansi.Fg(T.Text) + TextWidth.Truncate(body, width) + Ansi.Reset);
                    break;
                default:
                    num = newLine.ToString().PadLeft(numWidth);
                    oldLine++;
                    newLine++;
                    lines.Add(T.Dim(num) + "  " + TextWidth.Truncate(text, width - numWidth - 2));
                    break;
            }
            if (lines.Count >= maxLines)
            {
                lines.Add(T.Dim($"{T.Glyphs.Ellipsis} diff truncated (ctrl+o to expand)"));
                break;
            }
        }
        return lines;
    }

    public List<string> Todos(IReadOnlyList<TodoItem> todos, int width)
    {
        var lines = new List<string> { T.C(T.Glyphs.Dot, T.Success) + " " + T.B("Update Todos") };
        var first = true;
        foreach (var t in todos)
        {
            string line = t.Status switch
            {
                TodoStatus.Completed => T.C(T.Glyphs.TodoDone, T.Success) + " " + T.Dim(Ansi.Strike + t.Content + Ansi.Reset),
                TodoStatus.InProgress => T.C(T.Glyphs.TodoActive, T.Brand) + " " + T.B(t.Content),
                _ => T.Glyphs.TodoOpen + " " + t.Content,
            };
            lines.Add(ResultPrefix(first) + TextWidth.Truncate(line, width - 5));
            first = false;
        }
        if (todos.Count == 0) lines.Add(ResultPrefix(true) + T.Dim("(no todos)"));
        return lines;
    }

    public List<string> Notice(NoticeLevel level, string text, int width)
    {
        var color = level switch { NoticeLevel.Error => T.Error, NoticeLevel.Warning => T.Warning, _ => T.Secondary };
        var lines = new List<string>();
        foreach (var (w, i) in TextWidth.Wrap(text, width - 5).Select((w, i) => (w, i)))
            lines.Add(ResultPrefix(i == 0) + T.C(w, color));
        return lines;
    }

    public List<string> ErrorBlock(string text, int width)
    {
        var lines = new List<string>();
        foreach (var (w, i) in TextWidth.Wrap(text, width - 5).Select((w, i) => (w, i)))
            lines.Add(ResultPrefix(i == 0) + T.C(w, T.Error));
        return lines;
    }

    public List<string> Box(IReadOnlyList<string> content, int width, Rgb borderColor, string? title = null)
    {
        var b = T.Border;
        var inner = width - 4;
        var lines = new List<string>();
        var h = b.Horizontal;
        if (title is null) lines.Add(T.C(b.TopLeft + Repeat(h, width - 2) + b.TopRight, borderColor));
        else
        {
            var rest = Math.Max(0, width - 5 - TextWidth.Of(title));
            lines.Add(T.C(b.TopLeft + h + " ", borderColor) + title + T.C(" " + Repeat(h, rest) + b.TopRight, borderColor));
        }
        foreach (var c in content)
            foreach (var w in TextWidth.Wrap(c, inner))
                lines.Add(T.C(b.Vertical, borderColor) + " " + TextWidth.Pad(w, inner) + " " + T.C(b.Vertical, borderColor));
        lines.Add(T.C(b.BottomLeft + Repeat(h, width - 2) + b.BottomRight, borderColor));
        return lines;
    }

    public static string Repeat(string s, int n) => n <= 0 ? "" : s.Length == 1 ? new string(s[0], n) : string.Concat(Enumerable.Repeat(s, n));

    public static string ShortInput(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object) return "";
        return string.Join(", ", input.EnumerateObject().Take(2).Select(p => p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.GetRawText()));
    }
}

public enum ToolState { Running, Success, Error, Rejected }

internal static class TextUtilLocal
{
    public static string FirstLine(string text, int max)
    {
        var nl = text.IndexOf('\n');
        var line = nl >= 0 ? text[..nl] : text;
        return line.Length > max ? line[..Math.Max(1, max - 1)] + "…" : line;
    }
}
