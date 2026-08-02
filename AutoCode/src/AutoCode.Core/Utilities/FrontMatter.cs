// Auto Code — Gravicode Studios (Kang Fadhil)

namespace AutoCode.Core.Utilities;

/// <summary>
/// Parses the YAML-ish front matter that heads skill and agent markdown files.
///
/// EN: deliberately not a YAML parser. Skills and agent definitions use a flat
/// <c>key: value</c> block with optional list values, and pulling in a full YAML dependency to read
/// six keys would cost more than it is worth. Anything more exotic is reported rather than guessed at.
/// ID: sengaja bukan parser YAML penuh. Berkas skill dan agent hanya memakai blok <c>key: value</c>
/// sederhana, sehingga dependensi YAML lengkap tidak sepadan dengan manfaatnya.
/// </summary>
public static class FrontMatter
{
    /// <summary>Splits a document into its front matter map and its body.</summary>
    public static (Dictionary<string, string> Fields, string Body) Parse(string document)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(document))
            return (fields, "");

        var normalized = document.ReplaceLineEndings("\n");

        if (!normalized.StartsWith("---\n", StringComparison.Ordinal))
            return (fields, normalized.Trim());

        var end = normalized.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (end < 0)
            return (fields, normalized.Trim());

        var header = normalized[4..end];
        var bodyStart = normalized.IndexOf('\n', end + 1);
        var body = bodyStart < 0 ? "" : normalized[(bodyStart + 1)..];

        string? currentListKey = null;
        var currentList = new List<string>();

        void FlushList()
        {
            if (currentListKey is not null)
            {
                fields[currentListKey] = string.Join(',', currentList);
                currentList.Clear();
                currentListKey = null;
            }
        }

        foreach (var rawLine in header.Split('\n'))
        {
            var line = rawLine.TrimEnd();

            if (line.Trim().Length == 0)
                continue;

            // "  - item" continues the list opened by the previous key.
            if (line.TrimStart().StartsWith("- ", StringComparison.Ordinal) && currentListKey is not null)
            {
                currentList.Add(Unquote(line.TrimStart()[2..].Trim()));
                continue;
            }

            var colon = line.IndexOf(':');
            if (colon <= 0)
                continue;

            FlushList();

            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();

            if (value.Length == 0)
            {
                currentListKey = key;
                continue;
            }

            fields[key] = Unquote(value);
        }

        FlushList();

        return (fields, body.Trim());
    }

    /// <summary>Reads a field as a comma-separated list.</summary>
    public static List<string> AsList(Dictionary<string, string> fields, string key)
    {
        if (!fields.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
            return [];

        return [.. value
            .Trim('[', ']')
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Unquote)
            .Where(v => v.Length > 0)];
    }

    private static string Unquote(string value)
    {
        value = value.Trim();

        if (value.Length >= 2 &&
            ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
        {
            return value[1..^1];
        }

        return value;
    }
}
