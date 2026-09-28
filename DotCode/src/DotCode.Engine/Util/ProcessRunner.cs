using System.Diagnostics;
using System.Text;

namespace DotCode.Engine.Util;

public sealed record ProcessResult(int ExitCode, string Output, bool TimedOut, bool Cancelled);

/// <summary>Locates shells and executables, runs processes with timeouts and whole-tree kill.</summary>
public static class ProcessRunner
{
    private static readonly Lazy<string?> GitBash = new(FindGitBash);

    /// <summary>Bash used by the Bash tool and hooks: Git Bash on Windows (never WSL's System32\bash.exe), /bin/bash elsewhere.</summary>
    public static string? BashPath => OperatingSystem.IsWindows() ? GitBash.Value : File.Exists("/bin/bash") ? "/bin/bash" : FindOnPath("bash");

    public static string PowerShellPath => FindOnPath("pwsh") ?? (OperatingSystem.IsWindows() ? "powershell.exe" : "pwsh");

    private static string? FindGitBash()
    {
        if (Environment.GetEnvironmentVariable("DOTCODE_GIT_BASH_PATH") is { Length: > 0 } explicitPath && File.Exists(explicitPath)) return explicitPath;
        string[] candidates =
        [
            @"C:\Program Files\Git\bin\bash.exe",
            @"C:\Program Files\Git\usr\bin\bash.exe",
            @"C:\Program Files (x86)\Git\bin\bash.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Git\bin\bash.exe"),
        ];
        foreach (var c in candidates) if (File.Exists(c)) return c;
        if (FindOnPath("git") is { } git)
        {
            var root = Path.GetDirectoryName(Path.GetDirectoryName(git));
            if (root is not null && Path.Combine(root, "bin", "bash.exe") is var b && File.Exists(b)) return b;
        }
        var onPath = FindOnPath("bash");
        return onPath is not null && !onPath.Contains(@"\System32\", StringComparison.OrdinalIgnoreCase) ? onPath : null;
    }

    private static readonly Dictionary<string, string?> PathCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock PathGate = new();

    public static string? FindOnPath(string name)
    {
        lock (PathGate)
        {
            if (PathCache.TryGetValue(name, out var cached)) return cached;
            var result = Search(name);
            PathCache[name] = result;
            return result;
        }

        static string? Search(string name)
        {
            if (Path.IsPathRooted(name)) return File.Exists(name) ? name : null;
            var exts = OperatingSystem.IsWindows()
                ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';', StringSplitOptions.RemoveEmptyEntries).Prepend("").ToArray()
                : [""];
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                foreach (var ext in exts)
                {
                    try
                    {
                        var candidate = Path.Combine(dir.Trim('"'), name + ext);
                        if (File.Exists(candidate) && (ext.Length > 0 || !OperatingSystem.IsWindows() || Path.HasExtension(name))) return candidate;
                    }
                    catch (ArgumentException) { }
                }
            }
            return null;
        }
    }

    /// <summary>Builds a start-info for an executable name, routing .cmd/.bat shims (npx, npm) through cmd.exe on Windows.</summary>
    public static ProcessStartInfo CreateStartInfo(string command, IEnumerable<string> args, string cwd)
    {
        var resolved = FindOnPath(command) ?? command;
        ProcessStartInfo psi;
        if (OperatingSystem.IsWindows() && (resolved.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || resolved.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)))
        {
            psi = new ProcessStartInfo("cmd.exe") { WorkingDirectory = cwd };
            psi.ArgumentList.Add("/d");
            psi.ArgumentList.Add("/s");
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(resolved);
            foreach (var a in args) psi.ArgumentList.Add(a);
        }
        else
        {
            psi = new ProcessStartInfo(resolved) { WorkingDirectory = cwd };
            foreach (var a in args) psi.ArgumentList.Add(a);
        }
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        return psi;
    }

    /// <summary>Runs a process capturing combined stdout/stderr (in arrival order) with timeout and cancellation.</summary>
    public static async Task<ProcessResult> RunAsync(ProcessStartInfo psi, TimeSpan timeout, CancellationToken ct, string? stdin = null,
        Action<string>? onLine = null, int maxOutputChars = 2_000_000)
    {
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.RedirectStandardInput = true;
        psi.StandardOutputEncoding = Encoding.UTF8;
        psi.StandardErrorEncoding = Encoding.UTF8;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var output = new StringBuilder();
        var gate = new Lock();
        var truncated = false;
        void OnData(string? line)
        {
            if (line is null) return;
            lock (gate)
            {
                if (output.Length < maxOutputChars) output.Append(line).Append('\n');
                else truncated = true;
            }
            onLine?.Invoke(line);
        }
        process.OutputDataReceived += (_, e) => OnData(e.Data);
        process.ErrorDataReceived += (_, e) => OnData(e.Data);

        try { process.Start(); }
        catch (Exception ex) { return new ProcessResult(-1, $"Failed to start '{psi.FileName}': {ex.Message}", false, false); }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            if (stdin is not null) await process.StandardInput.WriteAsync(stdin).ConfigureAwait(false);
            process.StandardInput.Close();
        }
        catch (IOException) { }

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        var timedOut = false;
        var cancelled = false;
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            // Drain async readers.
            process.WaitForExit();
        }
        catch (OperationCanceledException)
        {
            timedOut = timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested;
            cancelled = ct.IsCancellationRequested;
            Kill(process);
        }
        string text;
        lock (gate) text = output.ToString();
        if (truncated) text += "\n[output truncated]";
        return new ProcessResult(timedOut || cancelled ? -1 : SafeExitCode(process), text, timedOut, cancelled);
    }

    private static int SafeExitCode(Process p)
    {
        try { return p.ExitCode; } catch (InvalidOperationException) { return -1; }
    }

    public static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception) { /* already gone */ }
    }

    /// <summary>Quick synchronous helper for short commands (git info, version probes).</summary>
    public static string? TryRun(string exe, string args, string cwd, int timeoutMs = 3000)
    {
        try
        {
            var psi = new ProcessStartInfo(FindOnPath(exe) ?? exe, args)
            {
                WorkingDirectory = cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            using var p = Process.Start(psi);
            if (p is null) return null;
            var outTask = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs)) { Kill(p); return null; }
            return p.ExitCode == 0 ? outTask.Result.TrimEnd() : null;
        }
        catch (Exception) { return null; }
    }
}
