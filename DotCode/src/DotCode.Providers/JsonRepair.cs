using System.Text;
using System.Text.Json;
using DotCode.Abstractions;

namespace DotCode.Providers;

/// <summary>Best-effort repair for truncated or slightly malformed tool-argument JSON produced by models
/// (unterminated strings, missing closing brackets, trailing commas, markdown fences).</summary>
public static class JsonRepair
{
    public static JsonElement TryRepair(string text)
    {
        var s = text.Trim();
        if (s.StartsWith("```", StringComparison.Ordinal))
        {
            var nl = s.IndexOf('\n');
            s = nl >= 0 ? s[(nl + 1)..] : s[3..];
            if (s.EndsWith("```", StringComparison.Ordinal)) s = s[..^3];
            s = s.Trim();
        }
        var start = s.IndexOf('{');
        if (start > 0) s = s[start..];

        if (TryParse(s, out var ok)) return ok;

        var sb = new StringBuilder(s.Length + 8);
        var stack = new Stack<char>();
        var inString = false;
        var escaped = false;
        foreach (var ch in s)
        {
            sb.Append(ch);
            if (inString)
            {
                if (escaped) escaped = false;
                else if (ch == '\\') escaped = true;
                else if (ch == '"') inString = false;
                continue;
            }
            switch (ch)
            {
                case '"': inString = true; break;
                case '{': stack.Push('}'); break;
                case '[': stack.Push(']'); break;
                case '}' or ']' when stack.Count > 0: stack.Pop(); break;
            }
        }
        if (inString) sb.Append('"');
        var trimmed = sb.ToString().TrimEnd();
        while (trimmed.EndsWith(',') || trimmed.EndsWith(':')) trimmed = trimmed[..^1].TrimEnd();
        sb.Clear().Append(trimmed);
        while (stack.Count > 0) sb.Append(stack.Pop());
        if (TryParse(sb.ToString(), out ok)) return ok;

        return DotCodeJson.Build(w =>
        {
            w.WriteStartObject();
            w.WriteString("_invalid_json", text);
            w.WriteEndObject();
        });
    }

    private static bool TryParse(string s, out JsonElement element)
    {
        try
        {
            element = DotCodeJson.Parse(s);
            return element.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            element = default;
            return false;
        }
    }
}
