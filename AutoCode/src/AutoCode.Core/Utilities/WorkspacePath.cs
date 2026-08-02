// Auto Code — Gravicode Studios (Kang Fadhil)

namespace AutoCode.Core.Utilities;

/// <summary>Path helpers that keep tool arguments anchored to the session workspace.</summary>
public static class WorkspacePath
{
    /// <summary>
    /// Turns a model-supplied path into an absolute one. Relative paths resolve against the workspace root;
    /// <c>~</c> expands to the user profile.
    /// </summary>
    public static string Resolve(string workspaceRoot, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path must not be empty.", nameof(path));

        path = path.Trim().Trim('"');

        if (path is "~" || path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify);
            path = path.Length <= 1 ? home : Path.Combine(home, path[2..]);
        }

        return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(workspaceRoot, path));
    }

    /// <summary>True when <paramref name="absolutePath"/> lives inside the workspace.</summary>
    public static bool IsInside(string workspaceRoot, string absolutePath)
    {
        var root = Path.GetFullPath(workspaceRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var full = Path.GetFullPath(absolutePath);

        return full.Equals(root, PathComparison) ||
               full.StartsWith(root + Path.DirectorySeparatorChar, PathComparison) ||
               full.StartsWith(root + Path.AltDirectorySeparatorChar, PathComparison);
    }

    /// <summary>Workspace-relative display form, with forward slashes. Falls back to the absolute path.</summary>
    public static string Relative(string workspaceRoot, string absolutePath)
    {
        if (!IsInside(workspaceRoot, absolutePath))
            return absolutePath.Replace('\\', '/');

        var relative = Path.GetRelativePath(workspaceRoot, absolutePath);
        return relative.Replace('\\', '/');
    }

    /// <summary>Windows paths are case-insensitive; POSIX paths are not.</summary>
    public static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Directory names skipped by every recursive walk (search, indexing, listing).</summary>
    public static readonly IReadOnlySet<string> IgnoredDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".svn", ".hg", "node_modules", "bin", "obj", ".vs", ".vscode", ".idea",
        "dist", "build", "out", "target", "__pycache__", ".pytest_cache", ".mypy_cache",
        "venv", ".venv", "env", ".tox", "packages", ".nuget", ".gradle", ".next", ".nuxt",
        "vendor", "coverage", ".terraform", ".autocode",
    };

    /// <summary>True when any segment of the path is an ignored directory.</summary>
    public static bool IsIgnored(string workspaceRoot, string absolutePath)
    {
        var relative = Relative(workspaceRoot, absolutePath);
        foreach (var segment in relative.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (IgnoredDirectories.Contains(segment))
                return true;
        }

        return false;
    }
}
