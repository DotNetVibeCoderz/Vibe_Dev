using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DotCode.Abstractions;
using DotCode.Engine.Agent;
using DotCode.Engine.Configuration;
using DotCode.Engine.Permissions;
using DotCode.Engine.Tools.Builtin;
using DotCode.Engine.Util;

namespace DotCode.Engine.Sandbox;

public enum SandboxKind
{
    /// <summary>No sandbox available (or disabled).</summary>
    None,
    /// <summary>Linux bubblewrap: read-only root, writable working dirs, hidden credential dirs, optional network namespace.</summary>
    Bubblewrap,
    /// <summary>macOS sandbox-exec (Seatbelt) profile with the same policy.</summary>
    Seatbelt,
    /// <summary>Windows Job Object: process-tree containment and resource/UI limits (no file-system isolation).</summary>
    JobObject,
}

/// <summary>What a sandboxed command may do.</summary>
public sealed record SandboxPolicy(IReadOnlyList<string> Writable, IReadOnlyList<string> Hidden, bool DenyNetwork, long? MemoryLimitBytes, int? MaxProcesses);

/// <summary>The sandbox decision for one shell command.</summary>
public sealed class SandboxPlan
{
    public SandboxKind Kind { get; init; }
    public SandboxPolicy? Policy { get; init; }
    /// <summary>Why the command runs unsandboxed (disabled, excluded, escape hatch, unavailable) — null when sandboxed.</summary>
    public string? UnsandboxedReason { get; init; }
    public bool Active => Kind != SandboxKind.None && Policy is not null;

    /// <summary>Rewrites the start info so the shell runs inside bwrap / sandbox-exec (Job Objects attach after start).</summary>
    public void Apply(ProcessStartInfo psi)
    {
        if (!Active) return;
        switch (Kind)
        {
            case SandboxKind.Bubblewrap:
                Wrap(psi, ShellSandbox.BwrapPath!, ShellSandbox.BubblewrapArgs(Policy!, psi.WorkingDirectory));
                break;
            case SandboxKind.Seatbelt:
                Wrap(psi, ShellSandbox.SeatbeltPath, ["-p", ShellSandbox.SeatbeltProfile(Policy!)]);
                break;
        }
    }

    private static void Wrap(ProcessStartInfo psi, string launcher, IReadOnlyList<string> launcherArgs)
    {
        var original = new List<string> { psi.FileName };
        original.AddRange(psi.ArgumentList);
        psi.FileName = launcher;
        psi.ArgumentList.Clear();
        foreach (var a in launcherArgs) psi.ArgumentList.Add(a);
        if (launcher == ShellSandbox.BwrapPath) psi.ArgumentList.Add("--");
        foreach (var a in original) psi.ArgumentList.Add(a);
    }

    /// <summary>Called right after the process starts (Windows: put it in a Job Object). Dispose the result when done.</summary>
    public IDisposable? OnStarted(Process process) =>
        Active && Kind == SandboxKind.JobObject && OperatingSystem.IsWindows() ? JobObject.Attach(process, Policy!) : null;

    public string Describe() => Kind switch
    {
        SandboxKind.Bubblewrap or SandboxKind.Seatbelt => $"writes limited to the working directories, temp and package caches; credentials hidden; network {(Policy!.DenyNetwork ? "blocked" : "allowed")}",
        SandboxKind.JobObject => "process tree contained in a Job Object (no file-system isolation on Windows)",
        _ => "not sandboxed",
    };
}

/// <summary>OS-level sandboxing for the Bash and PowerShell tools. Enabled with <c>"sandbox": {"enabled": true}</c>.</summary>
public static class ShellSandbox
{
    public const string SeatbeltPath = "/usr/bin/sandbox-exec";
    public static string? BwrapPath { get; } = OperatingSystem.IsLinux() ? ProcessRunner.FindOnPath("bwrap") : null;

    /// <summary>Test hook: force a sandbox kind.</summary>
    public static SandboxKind? OverrideKind { get; set; }

    private static readonly Lazy<SandboxKind> Detected = new(Detect);

    /// <summary>The sandbox mechanism available on this machine.</summary>
    public static SandboxKind Available => OverrideKind ?? Detected.Value;

    public static bool IsolatesFileSystem(SandboxKind kind) => kind is SandboxKind.Bubblewrap or SandboxKind.Seatbelt;

    private static SandboxKind Detect()
    {
        if (OperatingSystem.IsWindows()) return SandboxKind.JobObject;
        if (OperatingSystem.IsMacOS()) return File.Exists(SeatbeltPath) && Probe(SeatbeltPath, ["-p", "(version 1)(allow default)", "/usr/bin/true"]) ? SandboxKind.Seatbelt : SandboxKind.None;
        if (OperatingSystem.IsLinux() && BwrapPath is not null)
            return Probe(BwrapPath, ["--ro-bind", "/", "/", "--dev", "/dev", "--proc", "/proc", "--unshare-net", "--die-with-parent", "--", "/bin/true"]) ? SandboxKind.Bubblewrap : SandboxKind.None;
        return SandboxKind.None;
    }

    /// <summary>Runs the sandbox launcher once; unprivileged user namespaces may be blocked (e.g. some containers).</summary>
    private static bool Probe(string exe, string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return false;
            if (!p.WaitForExit(5000)) { ProcessRunner.Kill(p); return false; }
            return p.ExitCode == 0;
        }
        catch (Exception) { return false; }
    }

    public static bool Enabled(AgentSession session) => session.Runtime.Settings.Sandbox?.Enabled == true;

    /// <summary>Decides how a shell command runs: sandboxed, or unsandboxed with the reason.</summary>
    public static SandboxPlan Plan(AgentSession session, string command, bool disableRequested)
    {
        var settings = session.Runtime.Settings.Sandbox;
        if (settings?.Enabled != true) return new SandboxPlan { UnsandboxedReason = "sandbox disabled" };
        if (disableRequested) return new SandboxPlan { UnsandboxedReason = "dangerously_disable_sandbox" };
        if (IsExcluded(settings, command)) return new SandboxPlan { UnsandboxedReason = "excluded command" };
        var kind = Available;
        if (kind == SandboxKind.None) return new SandboxPlan { UnsandboxedReason = "no sandbox available on this system" };
        return new SandboxPlan { Kind = kind, Policy = BuildPolicy(session, settings) };
    }

    public static bool IsExcluded(SandboxSettings settings, string command)
    {
        if (settings.ExcludedCommands is not { Count: > 0 } excluded) return false;
        var subs = ShellCommand.Analyze(command).Subcommands;
        return subs.Any(s => excluded.Any(e => s == e || s.StartsWith(e.TrimEnd() + " ", StringComparison.Ordinal)));
    }

    /// <summary>Sandboxed shell commands need no prompt when the sandbox really isolates the file system
    /// (Linux/macOS), unless the model asked to leave the sandbox or the command is excluded.</summary>
    public static bool AutoAllows(AgentSession session, JsonElement input)
    {
        var settings = session.Runtime.Settings.Sandbox;
        if (settings?.Enabled != true || settings.AutoAllowBashIfSandboxed == false) return false;
        if (input.GetBool("dangerously_disable_sandbox") == true) return false;
        if (!IsolatesFileSystem(Available)) return false;
        return !IsExcluded(settings, input.GetString("command") ?? "");
    }

    private static readonly string[] CacheDirs = [".npm", ".cache", ".nuget", ".dotnet", ".cargo", "go", ".m2", ".gradle", ".bun", ".yarn", ".pnpm-store", ".local/share/pnpm", ".rustup", ".deno"];
    private static readonly string[] CredentialDirs = [".ssh", ".aws", ".gnupg", ".azure", ".kube", ".config/gcloud", ".docker", ".netrc", ".git-credentials", ".pypirc", ".npmrc", ".config/gh"];

    public static SandboxPolicy BuildPolicy(AgentSession session, SandboxSettings settings)
    {
        var home = DotCodePaths.Home;
        var writable = new List<string>();
        void W(string? p)
        {
            if (string.IsNullOrWhiteSpace(p)) return;
            var full = Path.GetFullPath(DotCodePaths.ExpandHome(p)).TrimEnd('/', '\\');
            if (full.Length > 0 && !writable.Contains(full)) writable.Add(full);
        }
        W(session.Cwd);
        W(ShellState.GetCwd(session));
        foreach (var d in session.Permissions.WorkingDirectories.ToList()) W(d);
        W(Path.GetTempPath());
        if (!OperatingSystem.IsWindows()) W("/tmp");
        foreach (var c in CacheDirs) W(Path.Combine(home, c));
        // Commits from inside a git worktree write to the main repository's .git directory.
        if ((session.Worktree ?? session.Runtime.Options.Worktree) is { } wt) W(Path.Combine(wt.RepoRoot, ".git"));
        foreach (var p in settings.AllowWrite ?? []) W(DotCodePaths.Resolve(p, session.Cwd));

        var hidden = new List<string>();
        foreach (var c in CredentialDirs) hidden.Add(Path.Combine(home, c));
        hidden.Add(DotCodePaths.UserDir);
        foreach (var p in settings.DenyRead ?? []) hidden.Add(Path.GetFullPath(DotCodePaths.Resolve(p, session.Cwd)));

        return new SandboxPolicy(writable, hidden.Distinct().ToList(), string.Equals(settings.Network, "deny", StringComparison.OrdinalIgnoreCase),
            settings.MemoryLimitMb is > 0 and var mb ? mb * 1024L * 1024L : null, settings.MaxProcesses is > 0 and var n ? n : null);
    }

    /// <summary>bubblewrap arguments: read-only root, private /dev and /proc, writable binds, hidden credential paths.</summary>
    public static List<string> BubblewrapArgs(SandboxPolicy policy, string? cwd)
    {
        var args = new List<string> { "--ro-bind", "/", "/", "--dev", "/dev", "--proc", "/proc" };
        foreach (var w in policy.Writable)
            if (Directory.Exists(w)) args.AddRange(["--bind", w, w]);
        foreach (var h in policy.Hidden)
        {
            // Hidden paths inside a writable bind are still masked (later mounts win).
            if (Directory.Exists(h)) args.AddRange(["--tmpfs", h]);
            else if (File.Exists(h)) args.AddRange(["--ro-bind", "/dev/null", h]);
        }
        if (policy.DenyNetwork) args.Add("--unshare-net");
        args.AddRange(["--die-with-parent", "--new-session"]);
        if (cwd is { Length: > 0 }) args.AddRange(["--chdir", cwd]);
        return args;
    }

    /// <summary>Seatbelt (SBPL) profile: everything allowed except writes outside the writable paths, reads of hidden
    /// paths and (optionally) outbound network other than localhost.</summary>
    public static string SeatbeltProfile(SandboxPolicy policy)
    {
        static string Q(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        static IEnumerable<string> Variants(string p)
        {
            yield return p;
            // /tmp and /var are symlinks into /private on macOS.
            if (p.StartsWith("/tmp", StringComparison.Ordinal) || p.StartsWith("/var/", StringComparison.Ordinal)) yield return "/private" + p;
        }
        var sb = new StringBuilder("(version 1)\n(allow default)\n(deny file-write*)\n(allow file-write*\n");
        foreach (var w in policy.Writable) foreach (var v in Variants(w)) sb.Append("  (subpath ").Append(Q(v)).Append(")\n");
        sb.Append("  (literal \"/dev/null\") (literal \"/dev/zero\") (literal \"/dev/dtracehelper\") (regex #\"^/dev/tty\") (regex #\"^/dev/fd/\"))\n");
        if (policy.Hidden.Count > 0)
        {
            sb.Append("(deny file-read* file-write*\n");
            foreach (var h in policy.Hidden) sb.Append("  (subpath ").Append(Q(h)).Append(")\n");
            sb.Append(")\n");
        }
        if (policy.DenyNetwork)
            sb.Append("(deny network-outbound (remote ip))\n(allow network-outbound (remote ip \"localhost:*\"))\n");
        return sb.ToString();
    }

    /// <summary>Hint appended to failed sandboxed commands whose output looks like a sandbox denial.</summary>
    public static string? FailureHint(SandboxPlan plan, string output, bool allowEscape)
    {
        if (!plan.Active || plan.Kind == SandboxKind.JobObject) return null;
        string[] markers = ["Read-only file system", "Operation not permitted", "Permission denied", "Could not resolve host", "Temporary failure in name resolution", "Network is unreachable", "getaddrinfo", "EROFS", "EACCES", "EPERM"];
        if (!markers.Any(m => output.Contains(m, StringComparison.OrdinalIgnoreCase))) return null;
        return $"\n\n[This command ran in the DotCode sandbox ({plan.Describe()}). If the failure is caused by the sandbox, "
               + (allowEscape
                   ? "retry with dangerously_disable_sandbox: true — the user will be asked to approve running it outside the sandbox.]"
                   : "ask the user to add the path to sandbox.allowWrite or to run the command themselves; running outside the sandbox is disabled by policy.]");
    }
}
