// Auto Code — Gravicode Studios (Kang Fadhil)

using AutoCode.Cli.Ui;
using AutoCode.Core.Abstractions;
using AutoCode.Core.Agents;
using Spectre.Console;

namespace AutoCode.Cli;

/// <summary>
/// The interactive loop.
///
/// EN: Ctrl+C cancels the turn in progress rather than killing the process, because a long agent run
/// that cannot be interrupted is one the user has to kill — losing the session with it. The second
/// Ctrl+C, with nothing running, exits.
/// ID: Ctrl+C membatalkan giliran yang sedang berjalan, bukan mematikan proses, agar sesi tidak
/// hilang. Ctrl+C kedua saat tidak ada proses berjalan akan keluar dari aplikasi.
/// </summary>
public sealed class Repl(AutoCodeSession session, Theme theme)
{
    private readonly SlashCommandRouter _commands = new(session, theme);

    public async Task<int> RunAsync(string? initialPrompt, CancellationToken applicationToken)
    {
        Banner.Render(session, theme);

        if (session.Session.Messages.Count > 0)
        {
            AnsiConsole.MarkupLine(
                $"[{theme.Muted}]Resumed session {Markup.Escape(session.Session.Id)} — {session.Session.UserTurnCount} previous turns.[/]\n");
        }

        var pending = initialPrompt;

        while (!applicationToken.IsCancellationRequested)
        {
            string input;

            if (pending is { Length: > 0 })
            {
                input = pending;
                pending = null;
                AnsiConsole.MarkupLine($"[{theme.Prompt}]›[/] {Markup.Escape(Collapse(input))}");
            }
            else
            {
                input = ReadInput();

                if (input.Length == 0)
                    continue;
            }

            var result = await _commands.HandleAsync(input, applicationToken).ConfigureAwait(false);

            switch (result)
            {
                case SlashResult.Exit:
                    await FinishAsync().ConfigureAwait(false);
                    return 0;

                case SlashResult.Handled:
                    AnsiConsole.WriteLine();
                    continue;

                case SlashResult.SendPrompt:
                    input = _commands.PendingPrompt ?? input;
                    break;
            }

            await RunTurnAsync(input, applicationToken).ConfigureAwait(false);
        }

        await FinishAsync().ConfigureAwait(false);
        return 0;
    }

    private async Task RunTurnAsync(string input, CancellationToken applicationToken)
    {
        using var turn = CancellationTokenSource.CreateLinkedTokenSource(applicationToken);

        void OnCancel(object? sender, ConsoleCancelEventArgs e)
        {
            // Interrupt the turn, not the process.
            e.Cancel = true;
            turn.Cancel();
        }

        Console.CancelKeyPress += OnCancel;

        try
        {
            await session.Loop.RunTurnAsync(input, turn.Token).ConfigureAwait(false);
            await session.SaveAsync(applicationToken).ConfigureAwait(false);
            RenderStatusLine();
        }
        catch (TurnAbortedException ex)
        {
            AnsiConsole.MarkupLine($"\n[{theme.Warning}]Stopped: {Markup.Escape(ex.Message)}[/]");
        }
        catch (OperationCanceledException) when (turn.IsCancellationRequested && !applicationToken.IsCancellationRequested)
        {
            AnsiConsole.MarkupLine($"\n[{theme.Warning}]Interrupted.[/]");
            await session.SaveAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"\n[{theme.Error}]✗ {Markup.Escape(ex.Message)}[/]");

            if (ex.InnerException is { } inner)
                AnsiConsole.MarkupLine($"[{theme.Muted}]{Markup.Escape(inner.Message)}[/]");
        }
        finally
        {
            Console.CancelKeyPress -= OnCancel;
            AnsiConsole.WriteLine();
        }
    }

    /// <summary>
    /// Reads a line, supporting a trailing backslash to continue onto the next — which is how a user
    /// pastes a multi-line prompt without the REPL submitting on the first newline.
    /// </summary>
    private string ReadInput()
    {
        AnsiConsole.Markup($"[{theme.Prompt}]›[/] ");

        var line = Console.ReadLine();

        if (line is null)
            return "/exit";

        line = line.Trim();

        while (line.EndsWith('\\'))
        {
            AnsiConsole.Markup($"[{theme.Muted}]…[/] ");
            var continuation = Console.ReadLine();

            if (continuation is null)
                break;

            line = line[..^1] + "\n" + continuation.TrimEnd();
        }

        return line;
    }

    private void RenderStatusLine()
    {
        if (!session.Options.ShowCost)
            return;

        var context = session.Cost.ContextUtilization;
        var contextNote = context > 0.5
            ? $"  ·  context {context:P0}"
            : "";

        AnsiConsole.MarkupLine(
            $"[{theme.Muted}]{Markup.Escape(session.Profile.Model)}  ·  {Markup.Escape(session.Cost.Format())}{contextNote}[/]");
    }

    private async Task FinishAsync()
    {
        await session.SaveAsync(CancellationToken.None).ConfigureAwait(false);

        if (session.Session.Messages.Count > 0)
        {
            AnsiConsole.MarkupLine(
                $"[{theme.Muted}]Session {Markup.Escape(session.Session.Id)} saved — resume with: autocode --resume {Markup.Escape(session.Session.Id)}[/]");
        }
    }

    private static string Collapse(string value)
    {
        var single = value.ReplaceLineEndings(" ").Trim();
        return single.Length <= 120 ? single : single[..120] + "…";
    }
}
