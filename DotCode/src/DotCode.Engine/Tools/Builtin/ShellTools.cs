using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DotCode.Abstractions;
using DotCode.Engine.Agent;
using DotCode.Engine.Util;

namespace DotCode.Engine.Tools.Builtin;

/// <summary>Shared implementation for Bash and PowerShell: scripts are written to a temp file (no quoting pitfalls),
/// the working directory persists between calls via a trailing marker, output is merged and truncated, and the
/// whole process tree is killed on timeout or interrupt.</summary>
public abstract class ShellToolBase : Tool
{
    protected const string CwdMarker = "__DOTCODE_CWD__";
    private const int DefaultTimeoutMs = 120_000;
    private const int MaxTimeoutMs = 600_000;

    protected abstract string ShellName { get; }
    protected abstract ProcessStartInfo CreateProcess(string scriptPath, string cwd);
    protected abstract string BuildScript(string command, string cwd);
    protected abstract string ScriptExtension { get; }

    public override JsonElement InputSchema { get; } = Schema("""
        {"type":"object","properties":{
          "command":{"type":"string","description":"The command to execute"},
          "description":{"type":"string","description":"Clear, concise description of what this command does in 5-10 words"},
          "timeout":{"type":"number","description":"Optional timeout in milliseconds (max 600000)"},
          "run_in_background":{"type":"boolean","description":"Run in the background; read output later with BashOutput"}},
         "required":["command"]}
        """);

    public override bool IsReadOnly(JsonElement input) => Permissions.ShellCommand.IsReadOnly(Str(input, "command"));
    public override string DisplayName(JsonElement input, AgentSession s) => $"{Name}({TextUtil.FirstLine(Str(input, "command"), 120)})";
    public override PermissionTarget GetPermissionTarget(JsonElement input, AgentSession s) => new(PermissionKind.Shell, Str(input, "command"));

    public override (string Title, string? Detail, string? Diff) DescribeForPermission(JsonElement input, AgentSession s) =>
        ($"{ShellName} command", input.GetString("description"), null);

    public override async Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
    {
        var command = Str(input, "command");
        var session = ctx.Session;
        var cwd = ShellState.GetCwd(session);
        var timeout = Math.Clamp((int)(input.GetProp("timeout")?.GetDouble() ?? DefaultTimeoutMs), 1000, MaxTimeoutMs);

        var script = Path.Combine(Path.GetTempPath(), $"dotcode-{Guid.NewGuid():n}{ScriptExtension}");
        await File.WriteAllTextAsync(script, BuildScript(command, cwd), new UTF8Encoding(ScriptExtension == ".ps1"), ct).ConfigureAwait(false);
        var psi = CreateProcess(script, cwd);
        psi.Environment["DOTCODE"] = "1";
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GIT_PAGER"] = "cat";
        psi.Environment["PAGER"] = "cat";
        psi.Environment["GIT_EDITOR"] = "true";

        if (input.GetBool("run_in_background") == true)
        {
            var id = session.Runtime.BackgroundShells.Start(psi, command, script);
            return ToolResult.Ok($"Command running in background with ID: {id}. Use BashOutput with bash_id=\"{id}\" to read its output, and KillShell to stop it.", $"Running in background ({id})");
        }

        var lastProgress = Stopwatch.StartNew();
        var tail = new Queue<string>();
        try
        {
            var result = await ProcessRunner.RunAsync(psi, TimeSpan.FromMilliseconds(timeout), ct, onLine: line =>
            {
                lock (tail)
                {
                    tail.Enqueue(line);
                    while (tail.Count > 5) tail.Dequeue();
                    if (lastProgress.ElapsedMilliseconds < 400 || line.StartsWith(CwdMarker, StringComparison.Ordinal)) return;
                    lastProgress.Restart();
                    ctx.Progress(string.Join('\n', tail));
                }
            }).ConfigureAwait(false);
            if (result.Cancelled) ct.ThrowIfCancellationRequested();

            var output = ExtractCwd(result.Output, session);
            output = output.TrimEnd('\n', '\r');
            var sb = new StringBuilder(output);
            if (result.TimedOut) sb.Append($"\n\nCommand timed out after {timeout / 1000.0:0.#}s");
            else if (result.ExitCode != 0) sb.Append(sb.Length > 0 ? "\n" : "").Append($"Exit code {result.ExitCode}");
            var text = sb.Length == 0 ? "(no output)" : sb.ToString();
            var lines = output.Length == 0 ? 0 : output.Split('\n').Length;
            return new ToolResult
            {
                Content = [new TextPart(text)],
                IsError = result.ExitCode != 0 || result.TimedOut,
                Summary = result.TimedOut ? "Timed out" : result.ExitCode != 0 ? $"Exit code {result.ExitCode}" : lines == 0 ? "(No content)" : TextUtil.Plural(lines, "line"),
                DisplayOutput = text,
            };
        }
        finally
        {
            try { File.Delete(script); } catch (IOException) { }
        }
    }

    private static string ExtractCwd(string output, AgentSession session)
    {
        var idx = output.LastIndexOf(CwdMarker, StringComparison.Ordinal);
        if (idx < 0) return output;
        var end = output.IndexOf('\n', idx);
        var path = (end < 0 ? output[(idx + CwdMarker.Length)..] : output[(idx + CwdMarker.Length)..end]).Trim();
        if (path.Length > 0)
        {
            try
            {
                var resolved = DotCodePaths.Resolve(path, session.Cwd);
                if (Directory.Exists(resolved)) ShellState.SetCwd(session, resolved);
            }
            catch (Exception) { }
        }
        var before = output[..idx];
        var after = end < 0 ? "" : output[(end + 1)..];
        return before + after;
    }
}

/// <summary>Per-session shell working directory (persists across Bash/PowerShell calls, shared with subagents in the
/// same checkout; a subagent in its own worktree gets its own).</summary>
public static class ShellState
{
    private static readonly ConditionalWeakTable<AgentSession, StrongBox<string>> Cwds = new();

    public static string GetCwd(AgentSession session)
    {
        var root = session;
        while (root.Parent is not null && root.Worktree == root.Parent.Worktree) root = root.Parent;
        var box = Cwds.GetValue(root, s => new StrongBox<string>(s.Cwd));
        return Directory.Exists(box.Value!) ? box.Value! : root.Cwd;
    }

    public static void SetCwd(AgentSession session, string cwd)
    {
        var root = session;
        while (root.Parent is not null && root.Worktree == root.Parent.Worktree) root = root.Parent;
        Cwds.GetValue(root, s => new StrongBox<string>(s.Cwd)).Value = cwd;
    }
}

public sealed class BashTool : ShellToolBase
{
    public override string Name => "Bash";
    protected override string ShellName => "Bash";
    protected override string ScriptExtension => ".sh";
    public override string Description => """
        Executes a bash command and returns its combined output. On Windows this runs Git Bash (POSIX syntax).
        - The working directory persists between calls; prefer absolute paths.
        - Quote paths containing spaces. Chain dependent commands with &&; make independent calls in parallel.
        - Default timeout 2 minutes (max 10). Output over 30000 characters is truncated.
        - Use run_in_background for servers/watchers, then BashOutput to read their output.
        - Avoid find/grep/cat/head/tail/sed/echo for file work: use Glob, Grep, Read, Edit and Write instead.
        - Never use interactive commands (git rebase -i, editors, prompts); pass non-interactive flags.
        - Git: only commit when asked; never push --force or skip hooks unless asked.
        """;

    public override bool IsEnabled(AgentSession session) => ProcessRunner.BashPath is not null;

    protected override string BuildScript(string command, string cwd)
    {
        var bashCwd = cwd.Replace('\\', '/');
        if (OperatingSystem.IsWindows() && bashCwd.Length > 1 && bashCwd[1] == ':') bashCwd = "/" + char.ToLowerInvariant(bashCwd[0]) + bashCwd[2..];
        return $"cd -- '{bashCwd.Replace("'", "'\\''")}' 2>/dev/null\nexec 2>&1\n{command}\n__dc_ec=$?\nprintf '\\n{CwdMarker}%s\\n' \"$(pwd -W 2>/dev/null || pwd)\"\nexit $__dc_ec\n";
    }

    protected override ProcessStartInfo CreateProcess(string scriptPath, string cwd)
    {
        var psi = new ProcessStartInfo(ProcessRunner.BashPath!) { WorkingDirectory = cwd };
        psi.ArgumentList.Add("--noprofile");
        psi.ArgumentList.Add("--norc");
        psi.ArgumentList.Add(scriptPath);
        return psi;
    }
}

public sealed class PowerShellTool : ShellToolBase
{
    public override string Name => "PowerShell";
    protected override string ShellName => "PowerShell";
    protected override string ScriptExtension => ".ps1";
    public override string Description => """
        Executes a PowerShell command (Windows PowerShell 5.1 or pwsh) and returns its output.
        - The working directory persists between calls. Use PowerShell syntax: $env:VAR, Get-ChildItem, Test-Path, `;` to chain.
        - In Windows PowerShell 5.1, `&&`/`||` are not available: use `A; if ($?) { B }`.
        - Never use Read-Host or other interactive prompts. Add -Confirm:$false to destructive cmdlets you intend to run.
        - Default timeout 2 minutes (max 10). Use run_in_background for long-running processes.
        - Prefer Read/Edit/Write/Glob/Grep tools for file operations.
        """;

    public override bool IsEnabled(AgentSession session) => OperatingSystem.IsWindows() || ProcessRunner.FindOnPath("pwsh") is not null;

    protected override string BuildScript(string command, string cwd) => $$"""
        $ErrorActionPreference = 'Continue'
        $ProgressPreference = 'SilentlyContinue'
        try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch {}
        Set-Location -LiteralPath '{{cwd.Replace("'", "''")}}'
        $global:LASTEXITCODE = 0
        & {
        {{command}}
        } 2>&1 | Out-String -Stream -Width 4096
        $__ok = $?
        $__ec = if ($global:LASTEXITCODE) { $global:LASTEXITCODE } elseif (-not $__ok) { 1 } else { 0 }
        Write-Output ("{{CwdMarker}}" + (Get-Location).Path)
        exit $__ec
        """;

    protected override ProcessStartInfo CreateProcess(string scriptPath, string cwd)
    {
        var psi = new ProcessStartInfo(ProcessRunner.PowerShellPath) { WorkingDirectory = cwd };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath }) psi.ArgumentList.Add(a);
        return psi;
    }
}

/// <summary>Background shell processes started with run_in_background.</summary>
public sealed class BackgroundShellManager
{
    public sealed class Shell
    {
        public required string Id { get; init; }
        public required string Command { get; init; }
        public required Process Process { get; init; }
        public StringBuilder Output { get; } = new();
        public int ReadOffset { get; set; }
        public DateTimeOffset Started { get; } = DateTimeOffset.UtcNow;
        public string? ScriptPath { get; init; }
        public string Status => Process.HasExited ? (Killed ? "killed" : $"completed (exit code {SafeExit()})") : "running";
        public bool Killed { get; set; }
        private int SafeExit() { try { return Process.ExitCode; } catch { return -1; } }
    }

    private readonly ConcurrentDictionary<string, Shell> _shells = new();
    private int _counter;

    public IReadOnlyCollection<Shell> Shells => [.. _shells.Values.OrderBy(s => s.Started)];

    public string Start(ProcessStartInfo psi, string command, string? scriptPath)
    {
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.RedirectStandardInput = true;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.StandardOutputEncoding = Encoding.UTF8;
        psi.StandardErrorEncoding = Encoding.UTF8;
        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var id = $"bash_{Interlocked.Increment(ref _counter)}";
        var shell = new Shell { Id = id, Command = command, Process = process, ScriptPath = scriptPath };
        void OnData(string? line)
        {
            if (line is null || line.StartsWith("__DOTCODE_CWD__", StringComparison.Ordinal)) return;
            lock (shell.Output)
            {
                if (shell.Output.Length > 5_000_000) shell.Output.Remove(0, 1_000_000);
                shell.Output.Append(line).Append('\n');
            }
        }
        process.OutputDataReceived += (_, e) => OnData(e.Data);
        process.ErrorDataReceived += (_, e) => OnData(e.Data);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.StandardInput.Close();
        _shells[id] = shell;
        return id;
    }

    public Shell? Get(string id) => _shells.TryGetValue(id, out var s) ? s : null;

    public bool Kill(string id)
    {
        if (!_shells.TryGetValue(id, out var s)) return false;
        s.Killed = true;
        ProcessRunner.Kill(s.Process);
        return true;
    }

    public void KillAll()
    {
        foreach (var s in _shells.Values) if (!s.Process.HasExited) { s.Killed = true; ProcessRunner.Kill(s.Process); }
    }
}

public sealed class BashOutputTool : Tool
{
    public override string Name => "BashOutput";
    public override string Description => "Retrieves new output from a background shell started with run_in_background. Returns only output produced since the last read, plus the shell status. Optionally filter lines with a regex.";
    public override JsonElement InputSchema { get; } = Schema("""
        {"type":"object","properties":{
          "bash_id":{"type":"string","description":"ID of the background shell"},
          "filter":{"type":"string","description":"Optional regex; only matching lines are returned"}},
         "required":["bash_id"]}
        """);
    public override bool IsReadOnly(JsonElement input) => true;
    public override PermissionTarget GetPermissionTarget(JsonElement input, AgentSession s) => new(PermissionKind.None);
    public override string DisplayName(JsonElement input, AgentSession s) => $"BashOutput({Str(input, "bash_id")})";

    public override Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
    {
        var shell = ctx.Session.Runtime.BackgroundShells.Get(Str(input, "bash_id"));
        if (shell is null) return Task.FromResult(ToolResult.Error($"No background shell with id {Str(input, "bash_id")}"));
        string output;
        lock (shell.Output)
        {
            var start = Math.Min(shell.ReadOffset, shell.Output.Length);
            output = shell.Output.ToString(start, shell.Output.Length - start);
            shell.ReadOffset = shell.Output.Length;
        }
        if (input.GetString("filter") is { Length: > 0 } filter)
        {
            try
            {
                var re = new System.Text.RegularExpressions.Regex(filter);
                output = string.Join('\n', output.Split('\n').Where(l => re.IsMatch(l)));
            }
            catch (ArgumentException ex) { return Task.FromResult(ToolResult.Error($"Invalid filter regex: {ex.Message}")); }
        }
        var text = $"<status>{shell.Status}</status>\n<output>\n{TextUtil.Truncate(output.TrimEnd(), 30_000)}\n</output>";
        return Task.FromResult(ToolResult.Ok(text, shell.Status));
    }
}

public sealed class KillShellTool : Tool
{
    public override string Name => "KillShell";
    public override string Description => "Kills a running background shell by its ID.";
    public override JsonElement InputSchema { get; } = Schema("""{"type":"object","properties":{"shell_id":{"type":"string","description":"ID of the background shell to kill"}},"required":["shell_id"]}""");
    public override PermissionTarget GetPermissionTarget(JsonElement input, AgentSession s) => new(PermissionKind.None);
    public override string DisplayName(JsonElement input, AgentSession s) => $"KillShell({Str(input, "shell_id")})";

    public override Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
    {
        var id = Str(input, "shell_id", Str(input, "bash_id"));
        return Task.FromResult(ctx.Session.Runtime.BackgroundShells.Kill(id)
            ? ToolResult.Ok($"Successfully killed shell: {id}", $"Killed {id}")
            : ToolResult.Error($"No background shell with id {id}"));
    }
}
