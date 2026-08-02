// Auto Code — Gravicode Studios (Kang Fadhil)
//
// EN: Auto Code is an agentic coding assistant for the terminal, built on .NET 10 and
//     Microsoft.Extensions.AI, Microsoft Agent Framework, Microsoft.Extensions.VectorData
//     and Semantic Kernel. It talks to any configurable LLM endpoint.
// ID: Auto Code adalah asisten koding agentik untuk terminal, dibangun di atas .NET 10 dan
//     pustaka AI resmi .NET. Mendukung endpoint LLM apa pun yang dapat dikonfigurasi.

using System.Text;
using System.Text.Json;
using AutoCode.Cli;
using AutoCode.Cli.Ui;
using AutoCode.Core.Agents;
using Spectre.Console;

// The terminal has to be UTF-8 before anything is written, or the box drawing and glyphs
// arrive as mojibake on a default Windows console.
if (OperatingSystem.IsWindows())
{
    try
    {
        Console.OutputEncoding = Encoding.UTF8;
    }
    catch (IOException)
    {
        // Redirected output that rejects the change is fine; the glyphs simply degrade.
    }

    TerminalCapabilities.EnableVirtualTerminal();
}

var cli = CommandLineOptions.Parse(args);
var theme = Theme.Detect();

if (cli.Errors.Count > 0)
{
    foreach (var error in cli.Errors)
        AnsiConsole.MarkupLine($"[{theme.Error}]{Markup.Escape(error)}[/]");

    AnsiConsole.MarkupLine($"[{theme.Muted}]Run 'autocode --help' for usage.[/]");
    return 2;
}

if (cli.ShowHelp)
{
    AnsiConsole.WriteLine(CommandLineOptions.HelpText);
    return 0;
}

if (cli.ShowVersion)
{
    AnsiConsole.WriteLine($"Auto Code {Banner.Version} — Gravicode Studios (Kang Fadhil)");
    return 0;
}

using var lifetime = new CancellationTokenSource();

if (cli.Subcommand is not null)
    return await Subcommands.RunAsync(cli, theme, lifetime.Token).ConfigureAwait(false);

var headless = cli.Print || cli.OutputFormat != OutputFormat.Text;

try
{
    return headless
        ? await RunHeadlessAsync(cli, lifetime.Token).ConfigureAwait(false)
        : await RunInteractiveAsync(cli, theme, lifetime.Token).ConfigureAwait(false);
}
catch (OperationCanceledException)
{
    return 130;
}
catch (Exception ex)
{
    AnsiConsole.MarkupLine($"[{theme.Error}]✗ {Markup.Escape(ex.Message)}[/]");

    if (Environment.GetEnvironmentVariable("AUTOCODE_DEBUG") == "1")
        AnsiConsole.WriteException(ex);

    return 1;
}

static async Task<int> RunInteractiveAsync(CommandLineOptions cli, Theme theme, CancellationToken cancellationToken)
{
    var ui = new ConsoleUserInterface(theme, showThinking: true);

    await using var session = await AutoCodeSession.CreateAsync(cli, ui, cancellationToken).ConfigureAwait(false);

    var repl = new Repl(session, theme);
    return await repl.RunAsync(cli.InitialPrompt, cancellationToken).ConfigureAwait(false);
}

static async Task<int> RunHeadlessAsync(CommandLineOptions cli, CancellationToken cancellationToken)
{
    if (string.IsNullOrWhiteSpace(cli.InitialPrompt))
    {
        // Accept a prompt on stdin so Auto Code composes with other tools:
        //   git diff | autocode -p "review this change"
        cli.SetPromptFromStdin(await Console.In.ReadToEndAsync(cancellationToken).ConfigureAwait(false));
    }

    if (string.IsNullOrWhiteSpace(cli.InitialPrompt))
    {
        Console.Error.WriteLine("A prompt is required with --print. Pass it as an argument or on stdin.");
        return 2;
    }

    var autoApprove = cli.PermissionMode is
        AutoCode.Core.Permissions.PermissionMode.AcceptEdits or
        AutoCode.Core.Permissions.PermissionMode.BypassPermissions;

    var ui = new HeadlessUserInterface(cli.OutputFormat, autoApprove);

    await using var session = await AutoCodeSession.CreateAsync(cli, ui, cancellationToken).ConfigureAwait(false);

    var exitCode = 0;

    try
    {
        await session.Loop.RunTurnAsync(cli.InitialPrompt!, cancellationToken).ConfigureAwait(false);
    }
    catch (TurnAbortedException)
    {
        exitCode = 1;
    }

    await session.SaveAsync(cancellationToken).ConfigureAwait(false);

    if (cli.OutputFormat == OutputFormat.Json)
    {
        var envelope = new
        {
            session_id = session.Session.Id,
            model = session.Profile.Model,
            provider = session.Profile.Name,
            result = ui.AssistantText,
            denials = ui.Denials,
            usage = new
            {
                input_tokens = session.Cost.InputTokens,
                output_tokens = session.Cost.OutputTokens,
                requests = session.Cost.RequestCount,
                cost_usd = session.Cost.TotalCostUsd,
            },
        };

        Console.WriteLine(JsonSerializer.Serialize(envelope, new JsonSerializerOptions { WriteIndented = true }));
    }
    else if (cli.OutputFormat == OutputFormat.Text)
    {
        Console.WriteLine();
    }

    return exitCode;
}
