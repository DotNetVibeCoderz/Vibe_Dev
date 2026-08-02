// Auto Code — Gravicode Studios (Kang Fadhil)

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AutoCode.Core.Abstractions;
using AutoCode.Core.Configuration;

namespace AutoCode.Tools;

/// <summary>
/// Runs a shell command in the workspace.
///
/// EN: the working directory persists between calls within a session, matching what a developer
/// expects from a terminal — but shell state (variables, functions) does not, because each call is
/// a fresh process.
/// ID: direktori kerja tetap bertahan antar pemanggilan dalam satu sesi, namun state shell
/// (variabel, fungsi) tidak, karena setiap pemanggilan adalah proses baru.
/// </summary>
public sealed class BashTool(AutoCodeOptions options) : ToolBase
{
    private readonly Lock _gate = new();
    private string? _workingDirectory;

    public override string Name => "Bash";

    public override string Description =>
        $"""
        Execute a shell command and return its combined output.

        - Runs through {DescribeShell(options)}.
        - The working directory persists across calls in a session; shell variables do not.
        - timeout_ms defaults to {options.BashTimeoutMs:N0} and is capped at 600000.
        - Output is truncated at 30000 characters.
        - Prefer the dedicated tools where they fit: Read/Write/Edit for files, Glob for finding them,
          Grep for searching them. Reach for the shell when you need to build, test, or use git.
        - Interactive commands will hang: pass non-interactive flags, never `-i`.
        """;

    public override ToolCapability Capability => ToolCapability.ExecutesCommands;

    protected override string SchemaJson =>
        """
        {
          "type": "object",
          "properties": {
            "command": { "type": "string", "description": "The shell command to execute." },
            "description": { "type": "string", "description": "Short active-voice description of what the command does." },
            "timeout_ms": { "type": "integer", "description": "Timeout in milliseconds. Max 600000." },
            "working_directory": { "type": "string", "description": "Run in this directory and remember it for later calls." }
          },
          "required": ["command"]
        }
        """;

    public override string Summarize(JsonElement arguments)
    {
        var command = Peek(arguments, "command") ?? "?";
        var collapsed = command.ReplaceLineEndings(" ").Trim();
        if (collapsed.Length > 120)
            collapsed = collapsed[..120] + "…";

        return $"Bash({collapsed})";
    }

    protected override async ValueTask<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var command = invocation.GetString("command");
        var timeout = Math.Clamp(invocation.TryGetInt("timeout_ms") ?? options.BashTimeoutMs, 1_000, 600_000);

        if (invocation.TryGetString("working_directory") is { Length: > 0 } requested)
        {
            var resolved = Core.Utilities.WorkspacePath.Resolve(invocation.WorkspaceRoot, requested);
            if (!Directory.Exists(resolved))
                return ToolResult.Fail($"working_directory does not exist: {resolved}");

            lock (_gate)
                _workingDirectory = resolved;
        }

        string cwd;
        lock (_gate)
            cwd = _workingDirectory ??= invocation.WorkspaceRoot;

        var (fileName, arguments) = BuildShellInvocation(options, command);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            },
        };

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"Failed to start shell '{fileName}': {ex.Message}");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // Nothing is going to type at an interactive prompt, so close stdin immediately;
        // a command that waits for input then fails fast instead of hanging until the timeout.
        try { process.StandardInput.Close(); } catch (IOException) { }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            timedOut = true;
            TryKill(process);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        var output = Combine(stdout, stderr);

        if (timedOut)
        {
            return ToolResult.Fail(
                $"Command timed out after {timeout:N0}ms and was terminated.\n\n{Truncate(output)}");
        }

        var exitCode = process.ExitCode;
        var body = Truncate(output);

        if (body.Length == 0)
            body = "(no output)";

        var display = invocation.TryGetString("description") is { Length: > 0 } described
            ? described
            : Summarize(invocation.Arguments);

        return exitCode == 0
            ? ToolResult.Ok(body, display)
            : ToolResult.Fail($"Exit code {exitCode}.\n\n{body}");
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or SystemException)
        {
            // The process died on its own between the check and the kill; nothing to clean up.
        }
    }

    private static string Combine(StringBuilder stdout, StringBuilder stderr)
    {
        if (stderr.Length == 0)
            return stdout.ToString().TrimEnd();

        if (stdout.Length == 0)
            return stderr.ToString().TrimEnd();

        return $"{stdout.ToString().TrimEnd()}\n{stderr.ToString().TrimEnd()}";
    }

    private static string Truncate(string value) =>
        value.Length <= 30_000
            ? value
            : value[..30_000] + $"\n\n… output truncated ({value.Length:N0} characters total).";

    /// <summary>Picks the shell and the flag that makes it run a single command string.</summary>
    internal static (string FileName, string Arguments) BuildShellInvocation(AutoCodeOptions options, string command)
    {
        var shell = options.Shell;

        if (string.IsNullOrWhiteSpace(shell))
        {
            shell = OperatingSystem.IsWindows()
                ? (FindOnPath("pwsh.exe") ?? "powershell.exe")
                : (Environment.GetEnvironmentVariable("SHELL") ?? "/bin/bash");
        }

        var name = Path.GetFileNameWithoutExtension(shell).ToLowerInvariant();

        return name switch
        {
            "pwsh" or "powershell" =>
                (shell, $"-NoLogo -NoProfile -NonInteractive -Command {Quote(command)}"),
            "cmd" =>
                (shell, $"/d /c {Quote(command)}"),
            _ =>
                (shell, $"-lc {Quote(command)}"),
        };
    }

    private static string DescribeShell(AutoCodeOptions options)
    {
        var (fileName, _) = BuildShellInvocation(options, "");
        return Path.GetFileNameWithoutExtension(fileName);
    }

    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"")}\"";

    private static string? FindOnPath(string executable)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
            return null;

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim('"'), executable);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry is not worth failing over.
            }
        }

        return null;
    }
}
