namespace DotCode.Engine.Extensibility;

/// <summary>Markdown file with a YAML-subset frontmatter block (scalars, quoted strings, inline and dash lists,
/// <c>|</c>/<c>&gt;</c> block scalars) — enough for SKILL.md, agent and command files.</summary>
public sealed class Frontmatter
{
    public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<string>> Lists { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string Body { get; private set; } = "";

    public string? Get(string key) => Values.TryGetValue(key, out var v) ? v : null;

    /// <summary>List value; scalar "a, b" is split on commas.</summary>
    public List<string>? GetList(string key)
    {
        if (Lists.TryGetValue(key, out var l)) return l;
        if (Values.TryGetValue(key, out var v) && v.Length > 0)
            return [.. v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
        return null;
    }

    public static Frontmatter Parse(string text)
    {
        var fm = new Frontmatter();
        text = text.Replace("\r\n", "\n");
        if (text.StartsWith('﻿')) text = text[1..];
        if (!text.StartsWith("---\n", StringComparison.Ordinal))
        {
            fm.Body = text;
            return fm;
        }
        var end = text.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (end < 0)
        {
            fm.Body = text;
            return fm;
        }
        var header = text[4..end];
        var bodyStart = text.IndexOf('\n', end + 4);
        fm.Body = bodyStart < 0 ? "" : text[(bodyStart + 1)..].TrimStart('\n');

        var lines = header.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Trim().Length == 0 || line.TrimStart().StartsWith('#')) continue;
            if (char.IsWhiteSpace(line[0])) continue;
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();

            if (value is "|" or ">" or "|-" or ">-" or "|+" or ">+")
            {
                var folded = value.StartsWith('>');
                var block = new List<string>();
                while (i + 1 < lines.Length && (lines[i + 1].Length == 0 || char.IsWhiteSpace(lines[i + 1][0])))
                    block.Add(lines[++i].Trim());
                fm.Values[key] = string.Join(folded ? " " : "\n", block).Trim();
                continue;
            }
            if (value.Length == 0)
            {
                var items = new List<string>();
                while (i + 1 < lines.Length && lines[i + 1].TrimStart().StartsWith("- ", StringComparison.Ordinal))
                    items.Add(Unquote(lines[++i].TrimStart()[2..].Trim()));
                if (items.Count > 0) fm.Lists[key] = items;
                else fm.Values[key] = "";
                continue;
            }
            if (value.StartsWith('[') && value.EndsWith(']'))
            {
                fm.Lists[key] = [.. value[1..^1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Unquote)];
                continue;
            }
            fm.Values[key] = Unquote(value);
        }
        return fm;
    }

    private static string Unquote(string v)
    {
        if (v.Length >= 2 && (v[0] == '"' && v[^1] == '"' || v[0] == '\'' && v[^1] == '\''))
            return v[1..^1].Replace("\\\"", "\"").Replace("''", "'");
        var hash = v.IndexOf(" #", StringComparison.Ordinal);
        return hash > 0 ? v[..hash].TrimEnd() : v;
    }
}
