// Auto Code — Gravicode Studios (Kang Fadhil)

using System.Text;
using Microsoft.Extensions.AI;
using Spectre.Console;

namespace AutoCode.Cli.Ui;

/// <summary>The start-up header and the transcript exporter.</summary>
public static class Banner
{
    /// <summary>
    /// The start-up header.
    ///
    /// EN: three quiet lines rather than a bordered panel. A box around a splash screen is the
    /// reflexive answer for a CLI, and it spends the user's first screenful of attention on a
    /// rectangle. What actually matters on launch is which workspace and which model — so those get
    /// the space, and the mark alone carries the identity.
    /// ID: tiga baris tenang, bukan panel berbingkai. Kotak di layar pembuka adalah jawaban refleks
    /// sebuah CLI, dan ia menghabiskan perhatian pertama pengguna untuk sebuah persegi. Yang benar-
    /// benar penting saat start adalah workspace dan model mana yang aktif.
    /// </summary>
    public static void Render(AutoCodeSession session, Theme theme)
    {
        var glyphs = Glyphs.Detect();

        Mascot.Render(theme, Version);

        AnsiConsole.MarkupLine(
            $"     [{theme.Muted}]{Markup.Escape(Shorten(session.WorkspaceRoot))}[/]" +
            $"  [{theme.Faint}]{glyphs.TraceMid}[/]  " +
            $"[{theme.Muted}]{Markup.Escape(session.Profile.Name)} · {Markup.Escape(session.Profile.Model)}[/]");

        var hints = new List<string>();

        if (session.ContextFiles.Count > 0)
            hints.Add($"{session.ContextFiles.Count} context file{(session.ContextFiles.Count == 1 ? "" : "s")}");

        // The permission mode is only worth a line when it is not the safe default — a warning
        // shown permanently stops reading as a warning.
        if (session.Permissions.Mode != Core.Permissions.PermissionMode.Ask)
            hints.Add($"[{theme.Warning}]{session.Permissions.Mode}[/]");

        hints.Add("/help");
        hints.Add("Ctrl+C to interrupt");

        AnsiConsole.MarkupLine($"     [{theme.Faint}]{string.Join("  ·  ", hints)}[/]");
        AnsiConsole.WriteLine();
    }

    public static string Version =>
        typeof(Banner).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "1.0.0";

    /// <summary>
    /// Shortens a workspace path for the header.
    ///
    /// EN: elided from the middle rather than the end. What identifies a workspace is its last
    /// segment — the directory you actually think of it by — and truncating from the right throws
    /// exactly that away. A deep temp or build path would otherwise wrap and take over the header.
    /// ID: dipangkas di tengah, bukan di ujung. Yang mengidentifikasi sebuah workspace adalah segmen
    /// terakhirnya, dan memotong dari kanan justru membuang bagian itu.
    /// </summary>
    private static string Shorten(string path, int max = 46)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify);

        var display = path.StartsWith(home, Core.Utilities.WorkspacePath.PathComparison)
            ? "~" + path[home.Length..].Replace('\\', '/')
            : path.Replace('\\', '/');

        if (display.Length <= max)
            return display;

        var segments = display.Split('/');
        var tail = segments[^1];

        // Always keep the final segment whole, even when it is itself over budget.
        if (tail.Length + 6 >= max)
            return "…/" + tail;

        var head = display[..Math.Max(1, max - tail.Length - 4)];
        return head + "…/" + tail;
    }
}

/// <summary>Renders a session transcript as markdown for <c>/export</c>.</summary>
public static class Transcript
{
    public static string ToMarkdown(AutoCodeSession session)
    {
        var builder = new StringBuilder();

        builder.Append("# Auto Code session ").Append(session.Session.Id).Append("\n\n")
               .Append("- Workspace: `").Append(session.WorkspaceRoot).Append("`\n")
               .Append("- Provider: ").Append(session.Profile.Name).Append(" · ").Append(session.Profile.Model).Append('\n')
               .Append("- Started: ").Append(session.Session.CreatedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm")).Append('\n')
               .Append("- Tokens: ").Append(session.Cost.InputTokens).Append(" in / ").Append(session.Cost.OutputTokens).Append(" out\n\n")
               .Append("---\n\n");

        // The side thread is part of the record even though it never reached the main model.
        if (session.Session.SideThread.Count > 0)
        {
            builder.Append("## Side thread\n\n");

            foreach (var message in session.Session.SideThread)
                builder.Append("- **").Append(message.Role).Append("** ").Append(message.Text.ReplaceLineEndings(" ")).Append('\n');

            builder.Append('\n');
        }

        foreach (var summary in session.Session.CompactionSummaries)
            builder.Append("> **Compacted earlier context**\n>\n> ").Append(summary.ReplaceLineEndings("\n> ")).Append("\n\n");

        foreach (var message in session.Loop.Messages)
        {
            if (message.Role == ChatRole.User)
            {
                builder.Append("## User\n\n").Append(message.Text.Trim()).Append("\n\n");
                continue;
            }

            if (message.Role == ChatRole.Assistant)
            {
                var text = message.Text.Trim();
                if (text.Length > 0)
                    builder.Append("## Assistant\n\n").Append(text).Append("\n\n");

                foreach (var call in message.Contents.OfType<FunctionCallContent>())
                    builder.Append("- 🔧 `").Append(call.Name).Append("`\n");

                if (message.Contents.OfType<FunctionCallContent>().Any())
                    builder.Append('\n');
            }
        }

        builder.Append("\n---\n\n_Generated by Auto Code — Gravicode Studios (Kang Fadhil)._\n");

        return builder.ToString();
    }
}
