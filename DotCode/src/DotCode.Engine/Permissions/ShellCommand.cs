using System.Text;

namespace DotCode.Engine.Permissions;

/// <summary>Minimal shell tokenizer used for permission checks: splits compound commands on
/// <c>&amp;&amp; || ; | &amp; newline</c> (outside quotes), detects redirections and command substitution, so a rule
/// like <c>Bash(npm test:*)</c> cannot be bypassed with <c>npm test &amp;&amp; rm -rf /</c>.</summary>
public static class ShellCommand
{
    public sealed record Analysis(IReadOnlyList<string> Subcommands, bool HasRedirection, bool HasSubstitution);

    public static Analysis Analyze(string command)
    {
        var parts = new List<string>();
        var sb = new StringBuilder();
        var redirect = false;
        var substitution = false;
        char quote = '\0';
        for (var i = 0; i < command.Length; i++)
        {
            var c = command[i];
            if (quote != '\0')
            {
                sb.Append(c);
                if (c == '\\' && quote == '"' && i + 1 < command.Length) { sb.Append(command[++i]); continue; }
                if (c == quote) quote = '\0';
                else if (quote == '"' && c == '$' && i + 1 < command.Length && command[i + 1] == '(') substitution = true;
                else if (quote == '"' && c == '`') substitution = true;
                continue;
            }
            switch (c)
            {
                case '\'' or '"':
                    quote = c;
                    sb.Append(c);
                    break;
                case '\\' when i + 1 < command.Length:
                    sb.Append(c).Append(command[++i]);
                    break;
                case '`':
                    substitution = true;
                    sb.Append(c);
                    break;
                case '$' when i + 1 < command.Length && command[i + 1] == '(':
                    substitution = true;
                    sb.Append(c);
                    break;
                case '&' when i + 1 < command.Length && command[i + 1] == '&':
                case '|' when i + 1 < command.Length && command[i + 1] == '|':
                    Flush();
                    i++;
                    break;
                case ';' or '\n' or '|':
                    Flush();
                    break;
                case '&':
                    // "2>&1" / "&>" are redirections, a lone "&" backgrounds the command.
                    if (sb.Length > 0 && sb[^1] == '>' || i + 1 < command.Length && command[i + 1] == '>') { sb.Append(c); break; }
                    Flush();
                    break;
                case '>':
                    // Harmless: 2>&1, >/dev/null, 2>/dev/null
                    var rest = command[(i + 1)..].TrimStart();
                    var prevIsFd = sb.Length > 0 && char.IsDigit(sb[^1]);
                    if (rest.StartsWith("&1", StringComparison.Ordinal) || rest.StartsWith("&2", StringComparison.Ordinal) ||
                        rest.StartsWith("/dev/null", StringComparison.Ordinal) || rest.StartsWith("$null", StringComparison.OrdinalIgnoreCase) ||
                        rest.StartsWith("nul", StringComparison.OrdinalIgnoreCase) && (rest.Length == 3 || !char.IsLetterOrDigit(rest[3])))
                    {
                        sb.Append(c);
                        _ = prevIsFd;
                        break;
                    }
                    redirect = true;
                    sb.Append(c);
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }
        Flush();
        return new Analysis(parts, redirect, substitution);

        void Flush()
        {
            var s = sb.ToString().Trim();
            if (s.Length > 0) parts.Add(StripEnvPrefix(s));
            sb.Clear();
        }
    }

    /// <summary>Drops leading <c>VAR=value</c> assignments and a leading <c>cd dir &amp;&amp;</c> is handled by splitting.</summary>
    private static string StripEnvPrefix(string s)
    {
        while (true)
        {
            var sp = s.IndexOf(' ');
            if (sp <= 0) return s;
            var head = s[..sp];
            var eq = head.IndexOf('=');
            if (eq > 0 && head[..eq].All(ch => char.IsLetterOrDigit(ch) || ch == '_')) s = s[(sp + 1)..].TrimStart();
            else return s;
        }
    }

    /// <summary>Command prefix for "don't ask again" suggestions: first word, plus sub-command for multi-command tools.</summary>
    public static string SuggestPrefix(string subcommand)
    {
        var words = subcommand.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return subcommand;
        var multi = words[0] is "git" or "npm" or "npx" or "pnpm" or "yarn" or "dotnet" or "cargo" or "go" or "docker" or "kubectl" or "pip" or "python" or "python3" or "uv" or "bun" or "gh" or "make" or "mvn" or "gradle" or "node";
        if (multi && words.Length > 1 && !words[1].StartsWith('-')) return words[0] + " " + words[1];
        return words[0];
    }

    private static readonly HashSet<string> ReadOnlyCommands =
    [
        "ls", "pwd", "echo", "cat", "head", "tail", "wc", "which", "whoami", "date", "tree", "file", "stat", "du", "df",
        "grep", "rg", "find", "sort", "uniq", "diff", "basename", "dirname", "realpath", "env", "printenv", "uname", "hostname",
        "dir", "type", "where", "ver",
        "Get-ChildItem", "Get-Content", "Get-Location", "Get-Item", "Get-Command", "Select-String", "Test-Path", "Get-Date", "Resolve-Path",
    ];

    private static readonly string[] ReadOnlyPrefixes =
    [
        "git status", "git log", "git diff", "git show", "git branch", "git rev-parse", "git remote -v", "git ls-files", "git blame",
        "dotnet --version", "dotnet --info", "dotnet --list-sdks", "node --version", "npm --version", "python --version", "npm ls", "npm list",
    ];

    /// <summary>True when every subcommand is a known read-only command and nothing is redirected or substituted.</summary>
    public static bool IsReadOnly(string command)
    {
        var a = Analyze(command);
        if (a.HasRedirection || a.HasSubstitution || a.Subcommands.Count == 0) return false;
        foreach (var sub in a.Subcommands)
        {
            var first = sub.Split(' ', 2)[0];
            if (first == "find" && (sub.Contains("-exec", StringComparison.Ordinal) || sub.Contains("-delete", StringComparison.Ordinal))) return false;
            if (first == "cd") continue;
            if (ReadOnlyCommands.Contains(first)) continue;
            if (ReadOnlyPrefixes.Any(p => sub.StartsWith(p, StringComparison.Ordinal))) continue;
            return false;
        }
        return true;
    }
}
