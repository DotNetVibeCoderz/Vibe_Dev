// Auto Code — Gravicode Studios (Kang Fadhil)

using System.Text;
using AutoCode.Core.Configuration;

namespace AutoCode.Core.Context;

/// <summary>A persistent instruction file discovered for the session.</summary>
public sealed record ContextFile(string Path, string DisplayPath, string Content, ContextScope Scope);

public enum ContextScope
{
    /// <summary>~/.autocode/AUTOCODE.md — applies to every project on the machine.</summary>
    User = 0,

    /// <summary>A file found while walking from the workspace root down to the working directory.</summary>
    Project = 1,

    /// <summary>Explicitly listed in settings.</summary>
    Explicit = 2,
}

/// <summary>
/// Finds the persistent-context files that shape a session.
///
/// EN: Auto Code reads AUTOCODE.md, and also CLAUDE.md so an existing Claude Code project works
/// unchanged. Files are gathered from the user home first, then from the workspace root downwards,
/// so a nested directory can add detail without restating what the root already said.
/// ID: Auto Code membaca AUTOCODE.md, dan juga CLAUDE.md agar proyek Claude Code yang sudah ada
/// langsung bisa dipakai. Berkas dikumpulkan dari home pengguna lalu dari root workspace ke bawah.
/// </summary>
public static class ContextFileLoader
{
    public static readonly string[] FileNames = ["AUTOCODE.md", "CLAUDE.md"];

    public static IReadOnlyList<ContextFile> Discover(
        string workspaceRoot,
        string workingDirectory,
        AutoCodeOptions options)
    {
        var results = new List<ContextFile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in FileNames)
        {
            var userFile = Path.Combine(ConfigurationLoader.UserHome, name);
            TryAdd(results, seen, userFile, $"~/{ConfigurationLoader.UserDirectoryName}/{name}", ContextScope.User);
        }

        foreach (var directory in DirectoriesFromRoot(workspaceRoot, workingDirectory))
        {
            foreach (var name in FileNames)
            {
                var candidate = Path.Combine(directory, name);
                var display = Utilities.WorkspacePath.Relative(workspaceRoot, candidate);
                TryAdd(results, seen, candidate, display, ContextScope.Project);
            }
        }

        foreach (var configured in options.ContextFiles)
        {
            var resolved = Utilities.WorkspacePath.Resolve(workspaceRoot, configured);
            var display = Utilities.WorkspacePath.Relative(workspaceRoot, resolved);
            TryAdd(results, seen, resolved, display, ContextScope.Explicit);
        }

        return results;
    }

    /// <summary>Renders the discovered files into the block appended to the system prompt.</summary>
    public static string Render(IReadOnlyList<ContextFile> files)
    {
        if (files.Count == 0)
            return "";

        var builder = new StringBuilder();
        builder.Append("# Project context\n\n")
               .Append("The following instruction files apply to this workspace. Treat them as standing\n")
               .Append("directives from the user: they outrank your defaults, and a later file may refine\n")
               .Append("an earlier one.\n\n");

        foreach (var file in files)
        {
            builder.Append("## ").Append(file.DisplayPath).Append("\n\n")
                   .Append(file.Content.Trim()).Append("\n\n");
        }

        return builder.ToString();
    }

    /// <summary>Root-first list of directories between the workspace root and the working directory.</summary>
    private static IEnumerable<string> DirectoriesFromRoot(string workspaceRoot, string workingDirectory)
    {
        var root = Path.GetFullPath(workspaceRoot);

        // The walk only makes sense inside the workspace. When the process's directory is somewhere
        // else entirely — which is exactly what --cwd does — walking up from it collects instruction
        // files from unrelated projects and never reaches the root at all.
        var start = Utilities.WorkspacePath.IsInside(root, workingDirectory)
            ? Path.GetFullPath(workingDirectory)
            : root;

        var current = new DirectoryInfo(start);
        var chain = new List<string>();

        while (current is not null)
        {
            chain.Add(current.FullName);

            if (string.Equals(current.FullName.TrimEnd(Path.DirectorySeparatorChar), root.TrimEnd(Path.DirectorySeparatorChar),
                    Utilities.WorkspacePath.PathComparison))
            {
                break;
            }

            current = current.Parent;
        }

        chain.Reverse();
        return chain;
    }

    private static void TryAdd(
        List<ContextFile> results,
        HashSet<string> seen,
        string path,
        string displayPath,
        ContextScope scope)
    {
        if (!File.Exists(path) || !seen.Add(Path.GetFullPath(path)))
            return;

        try
        {
            var content = File.ReadAllText(path);
            if (content.Trim().Length > 0)
                results.Add(new ContextFile(path, displayPath, content, scope));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unreadable context file is not worth aborting the session over.
        }
    }
}
