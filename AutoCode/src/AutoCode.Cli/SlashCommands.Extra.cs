// Auto Code — Gravicode Studios (Kang Fadhil)

using System.Diagnostics;
using AutoCode.Cli.Ui;
using AutoCode.Core.Permissions;
using Microsoft.Extensions.AI;
using Spectre.Console;

namespace AutoCode.Cli;

/// <summary>
/// Session, configuration and workflow commands.
///
/// EN: split from the main router only for length. Everything here follows the same rule — a slash
/// command must not need the model to work, so that it still functions when the provider is the
/// thing that is broken. The two exceptions are explicit: the review commands expand into prompts
/// and say so.
/// ID: dipisah dari router utama semata karena panjang. Aturannya sama: perintah garis miring tidak
/// boleh bergantung pada model, agar tetap berfungsi ketika justru provider-nya yang bermasalah.
/// </summary>
public sealed partial class SlashCommandRouter
{
    private readonly Glyphs _glyphs = Glyphs.Detect();

    /// <summary>
    /// Runs work behind a spinner, or plainly when there is no terminal to spin in.
    ///
    /// EN: Spectre's Status needs an interactive console and throws "The handle is invalid" without
    /// one — which is exactly what happens when a command is piped in. Auto Code is meant to compose
    /// with other tools, so every long-running command has to survive having no terminal.
    /// ID: Status milik Spectre memerlukan konsol interaktif dan melempar galat tanpa itu — persis
    /// yang terjadi saat perintah dialirkan lewat pipa. Auto Code dirancang agar bisa dirangkai
    /// dengan alat lain, jadi setiap perintah panjang harus tetap berjalan tanpa terminal.
    /// </summary>
    private static Task<T> WithStatusAsync<T>(string label, Func<Task<T>> work) =>
        AnsiConsole.Profile.Capabilities.Interactive && !Console.IsOutputRedirected
            ? AnsiConsole.Status().StartAsync(label, _ => work())
            : work();

    private void ShowAbout()
    {
        var grid = new Grid().AddColumn(new GridColumn().PadLeft(2).PadRight(3)).AddColumn();

        grid.AddRow($"[{theme.Faint}]Version[/]", Banner.Version);
        grid.AddRow($"[{theme.Faint}]Runtime[/]", $".NET {Environment.Version}");
        grid.AddRow($"[{theme.Faint}]Built by[/]", "Gravicode Studios · Kang Fadhil");
        grid.AddRow($"[{theme.Faint}]Provider[/]", $"{Markup.Escape(session.Profile.Name)} · {Markup.Escape(session.Profile.Model)}");
        grid.AddRow($"[{theme.Faint}]Tools[/]", $"{session.Tools.Tools.Count}");
        grid.AddRow($"[{theme.Faint}]Licence[/]", "MIT");

        AnsiConsole.Write(grid);
        AnsiConsole.WriteLine();
    }

    private void Rename(string? argument)
    {
        if (string.IsNullOrWhiteSpace(argument))
        {
            AnsiConsole.MarkupLine($"  [{theme.Muted}]This session is called “{Markup.Escape(session.Session.Title)}”.[/]");
            AnsiConsole.MarkupLine($"  [{theme.Faint}]Rename it with /rename <title>.[/]");
            return;
        }

        session.Session.Title = argument.Trim();
        AnsiConsole.MarkupLine($"  [{theme.Success}]Renamed to “{Markup.Escape(session.Session.Title)}”.[/]");
    }

    /// <summary>
    /// Forks the conversation.
    ///
    /// EN: the current transcript is saved under a new id and the original is left untouched, so an
    /// experiment can be abandoned by simply resuming the session it came from. Copying rather than
    /// moving is the whole point — a branch you cannot walk back from is not a branch.
    /// ID: transkrip disalin ke id baru dan yang asli dibiarkan utuh, sehingga eksperimen bisa
    /// ditinggalkan hanya dengan melanjutkan sesi asalnya.
    /// </summary>
    private async Task BranchAsync(CancellationToken cancellationToken)
    {
        var original = session.Session.Id;

        await session.SaveAsync(cancellationToken).ConfigureAwait(false);

        session.Session.Id = Guid.NewGuid().ToString("n")[..12];
        session.Session.Title = $"{session.Session.Title} (branch)";
        session.Session.CreatedAt = DateTimeOffset.UtcNow;

        await session.SaveAsync(cancellationToken).ConfigureAwait(false);

        AnsiConsole.MarkupLine($"  [{theme.Success}]Branched to {Markup.Escape(session.Session.Id)}.[/]");
        AnsiConsole.MarkupLine($"  [{theme.Faint}]The original is intact: autocode --resume {Markup.Escape(original)}[/]");
    }

    /// <summary>
    /// Drops the last n exchanges.
    ///
    /// EN: an exchange is a user message and everything the agent did in response, so undoing one
    /// removes a whole round rather than leaving a question with no answer. Files already written
    /// are not reverted — this rewinds the conversation, not the working tree, and saying so plainly
    /// matters more than the feature.
    /// ID: satu pertukaran adalah pesan pengguna beserta seluruh respons agent. Berkas yang sudah
    /// ditulis tidak dikembalikan — ini memundurkan percakapan, bukan direktori kerja.
    /// </summary>
    private void Undo(string? argument)
    {
        var count = int.TryParse(argument, out var parsed) ? Math.Max(1, parsed) : 1;
        var messages = session.Loop.Messages.ToList();

        var boundaries = new List<int>();
        for (var i = 0; i < messages.Count; i++)
        {
            if (messages[i].Role == ChatRole.User)
                boundaries.Add(i);
        }

        if (boundaries.Count == 0)
        {
            AnsiConsole.MarkupLine($"  [{theme.Muted}]Nothing to undo.[/]");
            return;
        }

        var target = boundaries[Math.Max(0, boundaries.Count - count)];
        var removed = messages.Count - target;

        session.Loop.ResetMessages(messages.Take(target));

        AnsiConsole.MarkupLine(
            $"  [{theme.Success}]Rewound {Math.Min(count, boundaries.Count)} exchange(s) — {removed} messages dropped.[/]");
        AnsiConsole.MarkupLine($"  [{theme.Warning}]Files already written were not reverted.[/]");
    }

    /// <summary>Asks the model for a recap. One of the two commands that legitimately needs it.</summary>
    private SlashResult Recap()
    {
        if (session.Loop.Messages.Count == 0)
        {
            AnsiConsole.MarkupLine($"  [{theme.Muted}]Nothing has happened yet.[/]");
            return SlashResult.Handled;
        }

        PendingPrompt =
            "Recap this session in one short paragraph: what was asked, what changed on disk, what " +
            "was verified, and what is still outstanding. Be specific about file paths. Do not use tools.";

        return SlashResult.SendPrompt;
    }

    /// <summary>
    /// Reasoning depth.
    ///
    /// EN: mapped onto the provider's thinking budget rather than invented as a separate dial,
    /// because that is the only knob the models actually expose. Providers without extended
    /// thinking are told so instead of silently accepting the setting.
    /// ID: dipetakan ke anggaran thinking milik provider, bukan tombol baru, karena hanya itu yang
    /// benar-benar tersedia pada modelnya.
    /// </summary>
    private void ShowOrSetEffort(string? argument)
    {
        var profile = session.Profile;

        if (string.IsNullOrWhiteSpace(argument))
        {
            var current = !profile.EnableExtendedThinking
                ? "off"
                : profile.ThinkingBudgetTokens switch
                {
                    <= 4_000 => "low",
                    <= 8_000 => "medium",
                    <= 16_000 => "high",
                    _ => "max",
                };

            AnsiConsole.MarkupLine($"  Reasoning effort: [{theme.Accent}]{current}[/]");
            AnsiConsole.MarkupLine($"  [{theme.Faint}]Set it with /effort off | low | medium | high | max[/]");
            return;
        }

        var (enabled, budget) = argument.Trim().ToLowerInvariant() switch
        {
            "off" or "none" => (false, 0),
            "low" => (true, 4_000),
            "medium" or "mid" => (true, 8_000),
            "high" => (true, 16_000),
            "max" => (true, 32_000),
            _ => (profile.EnableExtendedThinking, -1),
        };

        if (budget < 0)
        {
            AnsiConsole.MarkupLine($"  [{theme.Error}]Unknown level. Use off, low, medium, high or max.[/]");
            return;
        }

        profile.EnableExtendedThinking = enabled;
        if (enabled)
            profile.ThinkingBudgetTokens = budget;

        session.RebuildLoop();

        AnsiConsole.MarkupLine($"  [{theme.Success}]Reasoning effort set to {Markup.Escape(argument.Trim())}.[/]");

        if (enabled && profile.Kind == Core.Configuration.ProviderKind.OpenAICompatible)
        {
            AnsiConsole.MarkupLine(
                $"  [{theme.Warning}]This provider uses the OpenAI wire format, which has no thinking budget. " +
                "The setting will have no effect unless the endpoint supports one.[/]");
        }
    }

    private void ShowConfig()
    {
        var grid = new Grid().AddColumn(new GridColumn().PadLeft(2).PadRight(3)).AddColumn();

        grid.AddRow($"[{theme.Faint}]User[/]",
            Markup.Escape(Path.Combine(Core.Configuration.ConfigurationLoader.UserHome, "settings.json")));
        grid.AddRow($"[{theme.Faint}]Project[/]",
            Markup.Escape(Path.Combine(Core.Configuration.ConfigurationLoader.WorkspaceHome(session.WorkspaceRoot), "settings.json")));
        grid.AddRow($"[{theme.Faint}]Local[/]",
            Markup.Escape(Path.Combine(Core.Configuration.ConfigurationLoader.WorkspaceHome(session.WorkspaceRoot), "settings.local.json")));

        AnsiConsole.Write(grid);
        AnsiConsole.MarkupLine(
            $"\n  [{theme.Faint}]Edit them by hand, run `autocode config init`, or use Auto Code Studio for a GUI.[/]");
    }

    private void ShowTheme(string? argument)
    {
        if (string.IsNullOrWhiteSpace(argument))
        {
            AnsiConsole.MarkupLine($"  Theme: [{theme.Accent}]{(theme == Theme.Plain ? "plain" : "auto")}[/]");
            AnsiConsole.MarkupLine($"  [{theme.Faint}]Available: auto, plain. Set NO_COLOR to force plain everywhere.[/]");
            return;
        }

        // Themes are resolved once at start-up and threaded through every renderer, so switching
        // mid-session would leave half the interface on the old palette. Restarting is honest.
        AnsiConsole.MarkupLine(
            $"  [{theme.Muted}]Themes are chosen at start-up. Set NO_COLOR=1 for the plain palette, then restart.[/]");
    }

    private void EnterPlanMode()
    {
        session.Permissions.Mode = PermissionMode.Plan;
        session.RebuildLoop();

        AnsiConsole.MarkupLine($"  [{theme.Success}]Plan mode.[/] [{theme.Muted}]Reads and searches only — every change is refused.[/]");
        AnsiConsole.MarkupLine($"  [{theme.Faint}]Leave it with /permissions ask.[/]");
    }

    /// <summary>Shows the working diff by shelling out to git, which is where the truth lives.</summary>
    private async Task ShowDiffAsync(string? argument, CancellationToken cancellationToken)
    {
        var arguments = string.IsNullOrWhiteSpace(argument)
            ? "diff --stat HEAD"
            : $"diff HEAD -- {argument}";

        var (ok, output) = await RunGitAsync(arguments, cancellationToken).ConfigureAwait(false);

        if (!ok)
        {
            AnsiConsole.MarkupLine($"  [{theme.Error}]{Markup.Escape(output.Trim())}[/]");
            return;
        }

        if (output.Trim().Length == 0)
        {
            AnsiConsole.MarkupLine($"  [{theme.Muted}]No uncommitted changes.[/]");
            return;
        }

        foreach (var line in output.ReplaceLineEndings("\n").Split('\n'))
        {
            var colour = line.StartsWith('+') && !line.StartsWith("+++") ? theme.DiffAdd
                       : line.StartsWith('-') && !line.StartsWith("---") ? theme.DiffRemove
                       : theme.Muted;

            AnsiConsole.MarkupLine($"  [{colour}]{Markup.Escape(line)}[/]");
        }
    }

    private async Task<(bool Ok, string Output)> RunGitAsync(string arguments, CancellationToken cancellationToken)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = arguments,
                    WorkingDirectory = session.WorkspaceRoot,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };

            process.Start();

            var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            var stderr = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            return process.ExitCode == 0 ? (true, stdout) : (false, stderr.Length > 0 ? stderr : stdout);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (false, "git is not available on PATH.");
        }
    }

    /// <summary>Expands into a review prompt. Declared as a prompt, not pretended to be analysis.</summary>
    private SlashResult Review(bool security)
    {
        PendingPrompt = security
            ? """
              Review the uncommitted changes in this repository for security defects.

              Run `git diff HEAD` first and read every changed file in full — the diff alone hides
              the context a vulnerability usually depends on.

              Look for: injection through unvalidated input, secrets committed or logged, missing
              authorisation checks, unsafe deserialisation, path traversal, and weak or misused
              cryptography.

              Report only findings you can justify with a concrete attack: the input, the path it
              takes, and what the attacker gets. Say plainly when a change is sound.
              """
            : """
              Review the uncommitted changes in this repository.

              Run `git diff HEAD` first and read every changed file in full — the diff alone hides
              the context a defect usually depends on.

              Look for logic that is wrong on some input, unhandled error paths, resource leaks,
              race conditions, and off-by-one errors.

              Report only findings you can justify with a concrete failure scenario: specific inputs
              or state that produce a wrong result. Style opinions are noise. If the change is sound,
              say so plainly.
              """;

        return SlashResult.SendPrompt;
    }

    /// <summary>
    /// Background work.
    ///
    /// EN: Auto Code runs subagents inline, inside the turn that dispatched them, so there is never
    /// anything detached to list. Reporting that honestly is better than showing an empty table that
    /// implies a feature exists.
    /// ID: Auto Code menjalankan subagent secara inline di dalam giliran yang mengirimnya, sehingga
    /// tidak pernah ada pekerjaan terpisah untuk didaftar.
    /// </summary>
    private void ShowTasks()
    {
        AnsiConsole.MarkupLine(
            $"  [{theme.Muted}]Subagents run inline, inside the turn that dispatched them, so nothing detaches.[/]");
        AnsiConsole.MarkupLine(
            $"  [{theme.Faint}]Available subagents: {Markup.Escape(string.Join(", ", session.Agents.Keys))}[/]");
    }

    /// <summary>
    /// Project memory: the AUTOCODE.md files that shape every session here.
    ///
    /// EN: with no argument it reports what is currently in effect, because you cannot refine
    /// instructions you cannot see. With one it hands the note to the agent to fold into the file
    /// properly — merging into prose that already exists is editing, not appending, and the model
    /// is better at that than a string concatenation would be.
    /// ID: tanpa argumen ia melaporkan apa yang sedang berlaku, karena instruksi tidak bisa
    /// diperbaiki bila tidak terlihat. Dengan argumen, catatannya diserahkan ke agent untuk
    /// disatukan ke dalam berkas — menggabungkan ke prosa yang sudah ada adalah menyunting.
    /// </summary>
    private SlashResult Memory(string? argument)
    {
        if (string.IsNullOrWhiteSpace(argument))
        {
            if (session.ContextFiles.Count == 0)
            {
                AnsiConsole.MarkupLine($"  [{theme.Muted}]This project has no AUTOCODE.md yet.[/]");
                AnsiConsole.MarkupLine($"  [{theme.Faint}]Run /init to generate one, or /memory <note> to start it.[/]");
                return SlashResult.Handled;
            }

            foreach (var file in session.ContextFiles)
            {
                AnsiConsole.MarkupLine(
                    $"  [{theme.Accent}]{Markup.Escape(file.DisplayPath)}[/] " +
                    $"[{theme.Faint}]{file.Scope} · {file.Content.Length:N0} chars[/]");
            }

            AnsiConsole.MarkupLine($"\n  [{theme.Faint}]Add to it with /memory <note>.[/]");
            return SlashResult.Handled;
        }

        PendingPrompt =
            $"""
            Fold this into the project's AUTOCODE.md as a standing instruction:

            {argument.Trim()}

            Read the existing file first. Merge it into the section where it belongs rather than
            appending a new one, keep the file's voice, and do not restate anything already there.
            If no AUTOCODE.md exists, create a minimal one containing just this.
            """;

        return SlashResult.SendPrompt;
    }

    /// <summary>
    /// The side thread.
    ///
    /// EN: a parallel conversation for the question that is not the task. It answers from its own
    /// history only — the main transcript is never sent — so a clarification costs a fraction of
    /// what the same question would cost inline, and, more importantly, the answer never becomes
    /// part of the context the main task keeps replaying. The main thread does not move.
    ///
    /// It runs on <c>smallModel</c> for the same reason: a side question rarely needs the model you
    /// reserved for the work.
    ///
    /// ID: percakapan paralel untuk pertanyaan yang bukan bagian dari tugas. Ia menjawab hanya dari
    /// riwayatnya sendiri — transkrip utama tidak pernah dikirim — sehingga biayanya jauh lebih
    /// kecil, dan yang lebih penting, jawabannya tidak pernah menjadi bagian dari konteks yang terus
    /// diulang oleh tugas utama. Alur utama tidak bergeser.
    /// </summary>
    private async Task SideThreadAsync(string? argument, CancellationToken cancellationToken)
    {
        var thread = session.Session.SideThread;

        if (string.Equals(argument?.Trim(), "clear", StringComparison.OrdinalIgnoreCase))
        {
            thread.Clear();
            AnsiConsole.MarkupLine($"  [{theme.Success}]Side thread cleared.[/]");
            return;
        }

        if (string.IsNullOrWhiteSpace(argument))
        {
            if (thread.Count == 0)
            {
                AnsiConsole.MarkupLine($"  [{theme.Muted}]The side thread is empty.[/]");
                AnsiConsole.MarkupLine(
                    $"  [{theme.Faint}]Ask something with /btw <question>. It is answered separately, " +
                    "and the main conversation does not move.[/]");
                return;
            }

            foreach (var message in thread)
            {
                var isUser = message.Role == ChatRole.User;
                AnsiConsole.MarkupLine(
                    $"  [{(isUser ? theme.Accent : theme.Faint)}]{(isUser ? _glyphs.Prompt : _glyphs.TraceMid)}[/] " +
                    $"[{(isUser ? theme.Muted : theme.Muted)}]{Markup.Escape(Fit(message.Text))}[/]");
            }

            AnsiConsole.MarkupLine($"\n  [{theme.Faint}]{thread.Count} message(s) · /btw clear to reset[/]");
            return;
        }

        thread.Add(new ChatMessage(ChatRole.User, argument.Trim()));

        // Only the side thread goes on the wire. That is what makes this cheap, and what keeps the
        // main conversation from acquiring a topic it never asked about.
        var request = new List<ChatMessage>
        {
            new(ChatRole.System,
                $"""
                You are answering a side question during a coding session in {session.WorkspaceRoot}.

                This is deliberately outside the main task, so keep it short — a few sentences, or a
                small code sample if that is genuinely the clearest answer. You cannot see the main
                conversation and you have no tools; if answering would require either, say so rather
                than guessing.
                """),
        };

        request.AddRange(thread);

        using var client = Providers.ProviderFactory.CreateSmallChatClient(session.Profile);

        string answer;
        try
        {
            var response = await WithStatusAsync("Aside…", () => client.GetResponseAsync(
                request,
                new ChatOptions { MaxOutputTokens = 800 },
                cancellationToken)).ConfigureAwait(false);

            answer = response.Text.Trim();
            session.Cost.Record(response.Usage);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            thread.RemoveAt(thread.Count - 1);
            AnsiConsole.MarkupLine($"  [{theme.Error}]{Markup.Escape(ex.Message)}[/]");
            return;
        }

        thread.Add(new ChatMessage(ChatRole.Assistant, answer));

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Padder(new Markup($"[{theme.Muted}]{Markup.Escape(answer)}[/]"), new Padding(2, 0, 0, 0)));
        AnsiConsole.MarkupLine(
            $"\n  [{theme.Faint}]Side thread ({thread.Count / 2} exchange(s)) — the main conversation is untouched.[/]");
    }

    private static string Fit(string value)
    {
        var line = value.ReplaceLineEndings(" ").Trim();
        return line.Length <= 96 ? line : line[..96] + "…";
    }

    /// <summary>
    /// Hands a side-task to a subagent.
    ///
    /// EN: the subagent's report is printed and deliberately not appended to the conversation.
    /// That is the difference between /fork and asking in the main thread: the point of forking a
    /// side-task is that its output does not have to live in the context you are still working in.
    /// ID: laporan subagent dicetak dan sengaja tidak ditambahkan ke percakapan. Itulah bedanya
    /// dengan bertanya di alur utama — gunanya memisahkan tugas sampingan justru agar keluarannya
    /// tidak harus menetap di konteks yang sedang Anda pakai.
    /// </summary>
    private async Task ForkAsync(string? argument, CancellationToken cancellationToken)
    {
        var dispatcher = session.Services.Subagents;

        if (dispatcher is null || dispatcher.AvailableAgents.Count == 0)
        {
            AnsiConsole.MarkupLine($"  [{theme.Muted}]No subagents are available in this session.[/]");
            return;
        }

        var parts = argument?.Split(' ', 2, StringSplitOptions.TrimEntries) ?? [];

        if (parts.Length < 2)
        {
            AnsiConsole.MarkupLine($"  [{theme.Faint}]Usage: /fork <agent> <brief>[/]");
            AnsiConsole.MarkupLine(
                $"  [{theme.Faint}]Agents: {Markup.Escape(string.Join(", ", dispatcher.AvailableAgents))}[/]");
            return;
        }

        var agent = parts[0];

        if (!dispatcher.AvailableAgents.Contains(agent, StringComparer.OrdinalIgnoreCase))
        {
            AnsiConsole.MarkupLine($"  [{theme.Error}]No subagent called '{Markup.Escape(agent)}'.[/]");
            AnsiConsole.MarkupLine(
                $"  [{theme.Faint}]Agents: {Markup.Escape(string.Join(", ", dispatcher.AvailableAgents))}[/]");
            return;
        }

        var report = await dispatcher
            .RunAsync(agent, parts[1], "side-task", cancellationToken)
            .ConfigureAwait(false);

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Padder(new Markup($"[{theme.Muted}]{Markup.Escape(report)}[/]"), new Padding(2, 0, 0, 0)));
        AnsiConsole.MarkupLine($"\n  [{theme.Faint}]Not added to the conversation — paste anything you want kept.[/]");
    }
}
