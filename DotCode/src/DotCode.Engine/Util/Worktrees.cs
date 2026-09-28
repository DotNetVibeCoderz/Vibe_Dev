using System.Diagnostics;
using System.Text.RegularExpressions;

namespace DotCode.Engine.Util;

/// <summary>A DotCode-managed git worktree: <c>&lt;repo&gt;/.dotcode/worktrees/&lt;name&gt;</c> on branch <c>dotcode/&lt;name&gt;</c>.</summary>
public sealed record WorktreeInfo(string Name, string Path, string Branch, string RepoRoot, string BaseCommit, bool Reused = false);

/// <summary>Creates, inspects and removes git worktrees so a session (<c>--worktree</c>) or a subagent
/// (<c>isolation: worktree</c>) can work on an isolated checkout without touching the main working tree.</summary>
public static partial class Worktrees
{
    public const string BranchPrefix = "dotcode/";

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")]
    private static partial Regex NamePattern();

    public static bool IsValidName(string name) => NamePattern().IsMatch(name) && !name.Contains("..", StringComparison.Ordinal) && !name.EndsWith(".lock", StringComparison.Ordinal);

    public static string NewName() => $"wt-{DateTime.Now:MMdd-HHmmss}-{Guid.NewGuid().ToString("n")[..4]}";

    /// <summary>Top level of the repository containing <paramref name="cwd"/> (the main checkout, also from inside a worktree).</summary>
    public static string? RepoRoot(string cwd)
    {
        var common = Git(cwd, "rev-parse --path-format=absolute --git-common-dir");
        if (common.Code == 0 && common.Output.Length > 0)
        {
            var dir = System.IO.Path.GetFullPath(common.Output.Trim());
            if (System.IO.Path.GetFileName(dir.TrimEnd('/', '\\')) == ".git") return System.IO.Path.GetDirectoryName(dir.TrimEnd('/', '\\'));
        }
        var top = Git(cwd, "rev-parse --show-toplevel");
        return top.Code == 0 && top.Output.Length > 0 ? System.IO.Path.GetFullPath(top.Output.Trim()) : null;
    }

    public static string Root(string repoRoot) => System.IO.Path.Combine(repoRoot, ".dotcode", "worktrees");

    /// <summary>Creates (or reuses, when it already exists) the worktree <paramref name="name"/> branching from HEAD.</summary>
    public static WorktreeInfo Create(string cwd, string? name = null)
    {
        var repo = RepoRoot(cwd) ?? throw new InvalidOperationException("--worktree needs a git repository (run `git init` and make a first commit).");
        name ??= NewName();
        if (!IsValidName(name)) throw new InvalidOperationException($"Invalid worktree name '{name}' (letters, digits, '.', '_' and '-' only).");
        var root = Root(repo);
        Directory.CreateDirectory(root);
        // Keep the worktrees out of the main checkout's `git status`.
        var ignore = System.IO.Path.Combine(root, ".gitignore");
        if (!File.Exists(ignore)) File.WriteAllText(ignore, "*\n");

        var path = System.IO.Path.Combine(root, name);
        var branch = BranchPrefix + name;
        if (Directory.Exists(path) && File.Exists(System.IO.Path.Combine(path, ".git")))
            return new WorktreeInfo(name, path, branch, repo, Head(path) ?? "", Reused: true);

        var head = Head(repo) ?? throw new InvalidOperationException("The repository has no commits yet; make a first commit before using worktrees.");
        var branchExists = Git(repo, $"rev-parse --verify --quiet refs/heads/{branch}").Code == 0;
        var add = branchExists
            ? Git(repo, $"worktree add \"{path}\" {branch}")
            : Git(repo, $"worktree add -b {branch} \"{path}\" {head}");
        if (add.Code != 0) throw new InvalidOperationException($"git worktree add failed: {add.Output.Trim()}");
        return new WorktreeInfo(name, path, branch, repo, branchExists ? Head(path) ?? head : head);
    }

    /// <summary>Uncommitted changes or commits made since the worktree was created.</summary>
    public static (bool Dirty, int Commits) Changes(WorktreeInfo wt)
    {
        var status = Git(wt.Path, "status --porcelain");
        var dirty = status.Code != 0 || status.Output.Trim().Length > 0;
        var commits = 0;
        if (wt.BaseCommit.Length > 0 && Git(wt.Path, $"rev-list --count {wt.BaseCommit}..HEAD") is { Code: 0 } count)
            int.TryParse(count.Output.Trim(), out commits);
        return (dirty, commits);
    }

    public static bool HasChanges(WorktreeInfo wt)
    {
        var (dirty, commits) = Changes(wt);
        return dirty || commits > 0;
    }

    /// <summary>Removes the worktree directory and (by default) its branch.</summary>
    public static bool Remove(string repoRoot, string name, bool deleteBranch = true, bool force = true)
    {
        var path = System.IO.Path.Combine(Root(repoRoot), name);
        var result = Git(repoRoot, $"worktree remove {(force ? "--force " : "")}\"{path}\"");
        if (result.Code != 0 && Directory.Exists(path)) return false;
        Git(repoRoot, "worktree prune");
        if (deleteBranch) Git(repoRoot, $"branch -D {BranchPrefix}{name}");
        return true;
    }

    /// <summary>DotCode-managed worktrees of the repository.</summary>
    public static List<(string Name, string Path, string? Branch)> List(string cwd)
    {
        var repo = RepoRoot(cwd);
        if (repo is null) return [];
        var root = System.IO.Path.GetFullPath(Root(repo));
        var list = new List<(string, string, string?)>();
        var result = Git(repo, "worktree list --porcelain");
        if (result.Code != 0) return list;
        string? path = null, branch = null;
        foreach (var raw in (result.Output + "\n").Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("worktree ", StringComparison.Ordinal)) path = System.IO.Path.GetFullPath(line[9..]);
            else if (line.StartsWith("branch ", StringComparison.Ordinal)) branch = line[7..].Replace("refs/heads/", "");
            else if (line.Length == 0 && path is not null)
            {
                if (DotCodePaths.IsUnder(path, root) && !string.Equals(path, root, StringComparison.OrdinalIgnoreCase))
                    list.Add((System.IO.Path.GetFileName(path), path, branch));
                path = null;
                branch = null;
            }
        }
        return list;
    }

    private static string? Head(string cwd) => Git(cwd, "rev-parse HEAD") is { Code: 0 } r ? r.Output.Trim() : null;

    public static (int Code, string Output) Git(string cwd, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(ProcessRunner.FindOnPath("git") ?? "git", args)
            {
                WorkingDirectory = cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return (-1, "git not found");
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(60_000)) { ProcessRunner.Kill(p); return (-1, "git timed out"); }
            return (p.ExitCode, p.ExitCode == 0 ? stdout.Result : stderr.Result + stdout.Result);
        }
        catch (Exception ex) { return (-1, ex.Message); }
    }
}
