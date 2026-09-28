using System.Text;
using DotCode.Cli;
using DotCode.Engine;
using DotCode.Engine.Agent;
using DotCode.Engine.Sessions;

Console.OutputEncoding = new UTF8Encoding(false);
if (!Console.IsInputRedirected) Console.InputEncoding = new UTF8Encoding(false);

CliOptions options;
try
{
    options = CliOptions.Parse(args);
}
catch (Exception ex) when (ex is ArgumentException or FormatException or IOException)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 2;
}

if (options.Version || options.Command == "version")
{
    Console.WriteLine($"{AppInfo.Version} (DotCode)");
    Console.WriteLine(AppInfo.Credit);
    return 0;
}
if (options.Help || options.Command == "help")
{
    Console.WriteLine(CliOptions.HelpText);
    return 0;
}

using var cts = new CancellationTokenSource();
if (options.Print || options.Command is not null)
{
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
    };
}

try
{
    if (options.Command is { } command)
        return await Commands.RunAsync(command, options, cts.Token);

    if (options.Print)
    {
        options.Runtime.PermissionMode ??= null;
        await using var runtime = AgentRuntime.Create(options.Runtime);
        await using var session = OpenSession(runtime, options);
        return await HeadlessRunner.RunAsync(runtime, session, options, cts.Token);
    }

    return await DotCode.Tui.TuiApp.RunAsync(options.Runtime, new DotCode.Tui.TuiLaunch
    {
        InitialPrompt = options.Prompt,
        Continue = options.Continue,
        Resume = options.Resume,
        ResumeId = options.ResumeId,
        ForkSession = options.ForkSession,
        Theme = options.Theme,
    });
}
catch (OperationCanceledException)
{
    return 130;
}
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}
finally
{
    await DotCode.Engine.Observability.OtlpExporter.StopAsync();
}

static AgentSession OpenSession(AgentRuntime runtime, CliOptions options)
{
    if (options.Continue)
    {
        var latest = SessionStore.List(runtime.Cwd, 1).FirstOrDefault();
        if (latest is not null) return runtime.ResumeSession(latest.Path, options.ForkSession);
    }
    if (options.Resume && options.ResumeId is { } id)
    {
        var path = SessionStore.FindPath(runtime.Cwd, id) ?? throw new InvalidOperationException($"No session found with id {id}");
        return runtime.ResumeSession(path, options.ForkSession);
    }
    return runtime.CreateSession();
}
