// Auto Code — Gravicode Studios (Kang Fadhil)

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoCode.Core.Configuration;
using AutoCode.Core.Utilities;

namespace AutoCode.Core.Hooks;

/// <summary>Lifecycle points a hook can bind to.</summary>
public static class HookEvents
{
    public const string SessionStart = "SessionStart";
    public const string UserPromptSubmit = "UserPromptSubmit";
    public const string PreToolUse = "PreToolUse";
    public const string PostToolUse = "PostToolUse";
    public const string PreCompact = "PreCompact";
    public const string SubagentStop = "SubagentStop";
    public const string Stop = "Stop";
}

/// <summary>What a hook decided.</summary>
public readonly record struct HookOutcome(bool Allowed, string? Reason, string? AdditionalContext)
{
    public static HookOutcome Allow { get; } = new(true, null, null);
}

/// <summary>
/// Runs user-configured shell commands at lifecycle points.
///
/// EN: hooks receive a JSON payload on stdin and answer with their exit code — 0 to allow, non-zero
/// to block a <c>Pre*</c> event. Anything a hook prints on stdout is fed back to the model as extra
/// context, which is what makes a hook able to say *why* it blocked something.
/// ID: hook menerima payload JSON melalui stdin dan menjawab lewat exit code — 0 untuk mengizinkan,
/// selain 0 untuk memblokir event <c>Pre*</c>. Keluaran stdout dikembalikan ke model sebagai konteks.
/// </summary>
public sealed class HookRunner(AutoCodeOptions options)
{
    /// <summary>True when at least one hook is bound to the event, so callers can skip the work.</summary>
    public bool HasHooks(string eventName) =>
        options.Hooks.TryGetValue(eventName, out var hooks) && hooks.Count > 0;

    public async Task<HookOutcome> RunAsync(
        string eventName,
        string? matcherSubject,
        JsonObject payload,
        string workspaceRoot,
        CancellationToken cancellationToken)
    {
        if (!options.Hooks.TryGetValue(eventName, out var hooks) || hooks.Count == 0)
            return HookOutcome.Allow;

        var context = new StringBuilder();

        foreach (var hook in hooks)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!Matches(hook.Matcher, matcherSubject))
                continue;

            payload["hook_event_name"] = eventName;

            var (exitCode, stdout, stderr) = await ExecuteAsync(hook, payload, workspaceRoot, cancellationToken)
                .ConfigureAwait(false);

            if (stdout.Length > 0)
                context.Append(stdout.Trim()).Append('\n');

            if (exitCode != 0 && hook.Blocking)
            {
                var reason = stderr.Trim().Length > 0 ? stderr.Trim() : stdout.Trim();

                return new HookOutcome(
                    Allowed: false,
                    Reason: reason.Length > 0
                        ? reason
                        : $"Hook '{hook.Command}' blocked this action (exit code {exitCode}).",
                    AdditionalContext: null);
            }
        }

        return new HookOutcome(true, null, context.Length > 0 ? context.ToString().TrimEnd() : null);
    }

    private static bool Matches(string? matcher, string? subject)
    {
        if (string.IsNullOrWhiteSpace(matcher))
            return true;

        return subject is not null && Glob.IsMatch(matcher, subject);
    }

    private async Task<(int ExitCode, string Stdout, string Stderr)> ExecuteAsync(
        HookOptions hook,
        JsonObject payload,
        string workspaceRoot,
        CancellationToken cancellationToken)
    {
        var (fileName, arguments) = ShellCommand.Build(options.Shell, hook.Command);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workspaceRoot,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        process.StartInfo.Environment["AUTOCODE_HOOK_EVENT"] = payload["hook_event_name"]?.GetValue<string>() ?? "";
        process.StartInfo.Environment["AUTOCODE_WORKSPACE"] = workspaceRoot;

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            // A misconfigured hook must not take the session down.
            return (0, "", $"Hook '{hook.Command}' could not start: {ex.Message}");
        }

        await process.StandardInput.WriteAsync(payload.ToJsonString(JsonOptions)).ConfigureAwait(false);
        process.StandardInput.Close();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(hook.TimeoutMs);

        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or SystemException) { }
            return (0, "", $"Hook '{hook.Command}' timed out after {hook.TimeoutMs}ms and was ignored.");
        }

        return (process.ExitCode,
                await stdoutTask.ConfigureAwait(false),
                await stderrTask.ConfigureAwait(false));
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
}

/// <summary>Shell selection shared by hooks and the Bash tool.</summary>
public static class ShellCommand
{
    public static (string FileName, string Arguments) Build(string? configuredShell, string command)
    {
        var shell = configuredShell;

        if (string.IsNullOrWhiteSpace(shell))
        {
            shell = OperatingSystem.IsWindows()
                ? "powershell.exe"
                : (Environment.GetEnvironmentVariable("SHELL") ?? "/bin/bash");
        }

        var name = Path.GetFileNameWithoutExtension(shell).ToLowerInvariant();
        var quoted = $"\"{command.Replace("\"", "\\\"")}\"";

        return name switch
        {
            "pwsh" or "powershell" => (shell, $"-NoLogo -NoProfile -NonInteractive -Command {quoted}"),
            "cmd" => (shell, $"/d /c {quoted}"),
            _ => (shell, $"-lc {quoted}"),
        };
    }
}
