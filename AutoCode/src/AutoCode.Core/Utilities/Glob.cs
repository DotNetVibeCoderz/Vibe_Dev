// Auto Code — Gravicode Studios (Kang Fadhil)

using System.Text;
using System.Text.RegularExpressions;

namespace AutoCode.Core.Utilities;

/// <summary>
/// Glob matching used by the file tools and the permission engine.
/// Supports <c>*</c> (no separator), <c>**</c> (any depth), <c>?</c>, character classes and <c>{a,b}</c> alternation.
/// Compiled patterns are cached because the permission engine re-evaluates them on every tool call.
/// </summary>
public static class Glob
{
    private static readonly Dictionary<(string Pattern, bool PathMode), Regex> Cache = [];
    private static readonly Lock CacheLock = new();

    /// <summary>Matches a path-like value, treating <c>/</c> and <c>\</c> as equivalent separators.</summary>
    public static bool IsPathMatch(string pattern, string path) =>
        GetRegex(pattern, pathMode: true).IsMatch(Normalize(path));

    /// <summary>Matches an arbitrary string (command lines, tool names) where separators are not special.</summary>
    public static bool IsMatch(string pattern, string value) =>
        GetRegex(pattern, pathMode: false).IsMatch(value);

    /// <summary>Normalises separators and strips a leading <c>./</c>.</summary>
    public static string Normalize(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.StartsWith("./", StringComparison.Ordinal) ? normalized[2..] : normalized;
    }

    private static Regex GetRegex(string pattern, bool pathMode)
    {
        var key = (pattern, pathMode);

        lock (CacheLock)
        {
            if (Cache.TryGetValue(key, out var cached))
                return cached;

            var regex = new Regex(
                Translate(Normalize(pattern), pathMode),
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);

            // Bound the cache: patterns come from config and model output, so this is not a fixed set.
            if (Cache.Count > 512)
                Cache.Clear();

            Cache[key] = regex;
            return regex;
        }
    }

    private static string Translate(string pattern, bool pathMode)
    {
        var sb = new StringBuilder("^");
        var i = 0;

        while (i < pattern.Length)
        {
            var c = pattern[i];

            switch (c)
            {
                case '*' when pathMode:
                    if (i + 1 < pattern.Length && pattern[i + 1] == '*')
                    {
                        i += 2;
                        // "**/" should also match zero directories, so "src/**/x" matches "src/x".
                        if (i < pattern.Length && pattern[i] == '/')
                        {
                            i++;
                            sb.Append("(?:.*/)?");
                        }
                        else
                        {
                            sb.Append(".*");
                        }
                    }
                    else
                    {
                        i++;
                        sb.Append("[^/]*");
                    }
                    break;

                case '*':
                    i++;
                    sb.Append(".*");
                    break;

                case '?':
                    i++;
                    sb.Append(pathMode ? "[^/]" : ".");
                    break;

                case '[':
                    {
                        var close = pattern.IndexOf(']', i + 1);
                        if (close < 0)
                        {
                            sb.Append("\\[");
                            i++;
                            break;
                        }

                        var cls = pattern[(i + 1)..close];
                        sb.Append('[');
                        if (cls.StartsWith('!'))
                            sb.Append('^').Append(Regex.Escape(cls[1..]).Replace("\\-", "-"));
                        else
                            sb.Append(Regex.Escape(cls).Replace("\\-", "-"));
                        sb.Append(']');
                        i = close + 1;
                        break;
                    }

                case '{':
                    {
                        var close = pattern.IndexOf('}', i + 1);
                        if (close < 0)
                        {
                            sb.Append("\\{");
                            i++;
                            break;
                        }

                        var alternatives = pattern[(i + 1)..close].Split(',');
                        sb.Append("(?:")
                          .Append(string.Join('|', alternatives.Select(a => Unanchor(Translate(a, pathMode)))))
                          .Append(')');
                        i = close + 1;
                        break;
                    }

                default:
                    sb.Append(Regex.Escape(c.ToString()));
                    i++;
                    break;
            }
        }

        return sb.Append('$').ToString();
    }

    /// <summary>Strips the anchors that <see cref="Translate"/> adds, so a sub-pattern can be embedded.</summary>
    private static string Unanchor(string translated) => translated[1..^1];
}
