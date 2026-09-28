using DotCode.Engine;
using DotCode.Engine.Agent;
using DotCode.Engine.Sessions;
using DotCode.Tui.Rendering;
using DotCode.Tui.Themes;

namespace DotCode.Tui.Components;

/// <summary>Startup welcome panel (two columns like Claude Code v2: greeting + mascot + model/cwd on the left,
/// tips and recent activity on the right; single column on narrow terminals).</summary>
public static class Banner
{
    public static string[] Mascot(Theme t) => t.Ascii
        ?
        [
            " .-----. ",
            " | o o | ",
            " '-----' ",
        ]
        :
        [
            T(t, "▗▛▀▀▀▀▀▜▖"),
            T(t, "▐ ") + t.C("●", t.Text) + T(t, "   ") + t.C("●", t.Text) + T(t, " ▌"),
            T(t, "▝▙▄▄▄▄▄▟▘"),
        ];

    private static string T(Theme t, string s) => t.C(s, t.Brand);

    public static List<string> Render(Theme t, AgentSession session, int width)
    {
        var runtime = session.Runtime;
        var title = t.C($"DotCode v{AppInfo.Version}", t.Brand);
        var model = session.Model.Qualified;
        var cwd = DotCodePaths.IsUnder(runtime.Cwd, DotCodePaths.Home) ? "~" + Path.DirectorySeparatorChar + Path.GetRelativePath(DotCodePaths.Home, runtime.Cwd) : runtime.Cwd;
        var name = Environment.UserName;

        var tips = new List<string>
        {
            t.C("Tips for getting started", t.Brand),
            runtime.Memory.Any(m => m.Scope is Engine.Context.MemoryScope.Project)
                ? $"Run {t.B("/help")} to see commands and shortcuts"
                : $"Run {t.B("/init")} to create a DOTCODE.md file with instructions",
            $"Use {t.B("/model")} to switch between any configured LLM",
            $"{t.B("shift+tab")} cycles permission modes · {t.B("/theme")} restyles",
        };
        var recent = SessionStore.List(runtime.Cwd, 3);
        var activity = new List<string> { t.C("Recent activity", t.Brand) };
        if (recent.Count == 0) activity.Add(t.Dim("No recent activity"));
        foreach (var r in recent)
            activity.Add(t.Dim(Ago(r.Modified)) + " " + TextWidth.Truncate(r.Title ?? r.FirstPrompt.Replace('\n', ' '), 38));
        activity.Add(t.Dim("/resume for more"));

        var left = new List<string>
        {
            "",
            t.B($"Welcome back {name}!"),
            "",
        };
        left.AddRange(Mascot(t));
        left.Add("");
        left.Add(t.Dim(model));
        left.Add(t.Dim(TruncateStart(cwd, width >= 80 ? Math.Min(44, (width - 2) / 2) - 2 : width - 6)));
        left.Add(t.Faint(AppInfo.Credit));

        var lines = new List<string>();
        var b = t.Border;
        var inner = width - 2;
        var rest = Math.Max(0, width - 7 - TextWidth.Of(title));
        lines.Add(t.C(b.TopLeft + Blocks.Repeat(b.Horizontal, 3) + " ", t.Brand) + title + t.C(" " + Blocks.Repeat(b.Horizontal, rest) + b.TopRight, t.Brand));

        if (width >= 80)
        {
            var leftWidth = Math.Min(44, inner / 2);
            var rightWidth = inner - leftWidth - 4;
            var right = new List<string>();
            right.AddRange(tips.Select(x => TextWidth.Truncate(x, rightWidth)));
            right.Add(t.Faint(Blocks.Repeat(t.Ascii ? "-" : "─", rightWidth)));
            right.AddRange(activity.Select(x => TextWidth.Truncate(x, rightWidth)));
            var rows = Math.Max(left.Count, right.Count);
            for (var i = 0; i < rows; i++)
            {
                var l = i < left.Count ? Center(left[i], leftWidth) : new string(' ', leftWidth);
                var r = i < right.Count ? TextWidth.Pad(right[i], rightWidth) : new string(' ', rightWidth);
                lines.Add(t.C(b.Vertical, t.Brand) + l + " " + t.C(b.Vertical, t.Brand) + " " + r + " " + t.C(b.Vertical, t.Brand));
            }
        }
        else
        {
            foreach (var l in left.Concat([""]).Concat(tips))
                lines.Add(t.C(b.Vertical, t.Brand) + " " + TextWidth.Pad(TextWidth.Truncate(l, inner - 2), inner - 2) + " " + t.C(b.Vertical, t.Brand));
        }
        lines.Add(t.C(b.BottomLeft + Blocks.Repeat(b.Horizontal, width - 2) + b.BottomRight, t.Brand));
        return lines;
    }

    /// <summary>Keeps the end of long paths ("…\project\src").</summary>
    public static string TruncateStart(string s, int width)
    {
        if (TextWidth.Of(s) <= width || width < 4) return s;
        return "…" + s[^(width - 1)..];
    }

    private static string Center(string s, int width)
    {
        var w = TextWidth.Of(s);
        if (w >= width) return TextWidth.Truncate(s, width);
        var left = (width - w) / 2;
        return new string(' ', left) + s + new string(' ', width - w - left);
    }

    public static string Ago(DateTimeOffset t)
    {
        var d = DateTimeOffset.UtcNow - t;
        return d.TotalMinutes < 1 ? "just now" : d.TotalHours < 1 ? $"{(int)d.TotalMinutes}m ago" : d.TotalDays < 1 ? $"{(int)d.TotalHours}h ago" : $"{(int)d.TotalDays}d ago";
    }
}

/// <summary>The animated "working" line: spinner glyph + shimmering verb + elapsed/tokens/interrupt hint.</summary>
public static class SpinnerLine
{
    public static readonly string[] Verbs =
    [
        "Accomplishing", "Actualizing", "Baking", "Brewing", "Calculating", "Cerebrating", "Churning", "Coalescing", "Cogitating",
        "Compiling", "Computing", "Conjuring", "Considering", "Cooking", "Crafting", "Creating", "Crunching", "Deliberating",
        "Determining", "Dotting", "Effecting", "Finagling", "Forging", "Generating", "Hatching", "Herding", "Honking", "Hustling",
        "Ideating", "Inferring", "Jitting", "Manifesting", "Marinating", "Moseying", "Mulling", "Mustering", "Musing", "Noodling",
        "Percolating", "Pondering", "Processing", "Puttering", "Refactoring", "Reticulating", "Ruminating", "Schlepping", "Shucking",
        "Simmering", "Smooshing", "Spinning", "Stewing", "Synthesizing", "Thinking", "Tinkering", "Transmuting", "Vibing", "Whirring", "Working",
    ];

    public static string Render(Theme t, int frame, string verb, TimeSpan elapsed, long tokens, bool receiving, bool thinking, string? detail, int width)
    {
        var frames = t.SpinnerFrames;
        var glyph = t.ReducedMotion ? frames[frames.Length / 2] : frames[frame % frames.Length];
        var text = verb + t.Glyphs.Ellipsis;
        string shimmer;
        if (t.ReducedMotion) shimmer = t.C(text, t.Brand);
        else
        {
            // A bright band sweeps across the verb.
            var pos = frame * 1 % (text.Length + 10) - 5;
            var sb = new System.Text.StringBuilder();
            for (var i = 0; i < text.Length; i++)
            {
                var dist = Math.Abs(i - pos);
                var intensity = dist switch { 0 => 1.0, 1 => 0.6, 2 => 0.3, _ => 0.0 };
                sb.Append(Ansi.Fg(t.Brand.Lerp(t.BrandShimmer, intensity))).Append(text[i]);
            }
            sb.Append(Ansi.Reset);
            shimmer = sb.ToString();
        }
        var stats = new List<string> { Util(elapsed) };
        if (tokens > 0) stats.Add($"{(receiving ? t.Glyphs.Down : t.Glyphs.Up)} {Engine.Util.TextUtil.FormatTokens(tokens)} tokens");
        if (thinking) stats.Add("thinking");
        if (detail is not null) stats.Add(detail);
        stats.Add(t.B("esc") + t.Dim(" to interrupt"));
        var line = t.C(glyph, t.Brand) + " " + shimmer + " " + t.Dim("(" + string.Join(t.Dim(" · "), stats.Select(s => s.Contains('\u001b') ? s : t.Dim(s))) + t.Dim(")"));
        return TextWidth.Truncate(line, width);
    }

    private static string Util(TimeSpan e) =>
        e.TotalHours >= 1 ? $"{(int)e.TotalHours}h {e.Minutes}m {e.Seconds}s" : e.TotalSeconds >= 60 ? $"{(int)e.TotalMinutes}m {e.Seconds}s" : $"{(int)e.TotalSeconds}s";

    public static readonly string[] Tips =
    [
        "Press shift+tab to switch between default, accept-edits and plan mode",
        "Use /model to switch to any configured LLM (Anthropic, OpenAI, Gemini, DeepSeek, Ollama…)",
        "Start a line with ! to run a shell command directly",
        "Type @ to mention files — their contents are attached automatically",
        "Use /compact to summarize long conversations and free context",
        "Press esc twice to rewind the conversation (and your code)",
        "Hit ctrl+o to toggle verbose output for tool results",
        "Create reusable prompts in .dotcode/commands/*.md",
        "Add MCP servers with: dotcode mcp add <name> <command>",
        "Queue follow-up messages while DotCode is working — just type and press enter",
        "Try /theme to pick colors, glyphs and spinner styles",
        "Use /agents to see available subagents; the model delegates big searches to them",
    ];
}
