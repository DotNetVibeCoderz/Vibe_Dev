// Auto Code — Gravicode Studios (Kang Fadhil)

using Spectre.Console;
using Spectre.Console.Rendering;

namespace AutoCode.Cli.Ui;

/// <summary>
/// Renders a pending edit as a diff.
///
/// EN: an approval prompt that shows only the replacement text asks the user to approve a change
/// they cannot see. What matters is what is being *removed* — that is where the damage is. Showing
/// both sides turns the prompt from a formality into an actual decision.
/// ID: prompt persetujuan yang hanya menampilkan teks pengganti meminta pengguna menyetujui
/// perubahan yang tidak bisa mereka lihat. Yang penting justru apa yang *dihapus*.
/// </summary>
public static class DiffRenderer
{
    private const int MaxLines = 18;

    /// <summary>Builds a renderable diff, or null when the arguments are not an edit.</summary>
    public static IRenderable? TryBuild(Theme theme, Glyphs glyphs, System.Text.Json.JsonElement? arguments)
    {
        if (arguments is not { ValueKind: System.Text.Json.JsonValueKind.Object } args)
            return null;

        if (!TryGetString(args, "old_string", out var oldText) ||
            !TryGetString(args, "new_string", out var newText))
        {
            return null;
        }

        return Build(theme, glyphs, oldText, newText);
    }

    public static IRenderable Build(Theme theme, Glyphs glyphs, string oldText, string newText)
    {
        var removed = oldText.ReplaceLineEndings("\n").Split('\n');
        var added = newText.ReplaceLineEndings("\n").Split('\n');

        var rows = new List<IRenderable>();
        var shown = 0;

        // Common leading and trailing lines are context, not change. Collapsing them keeps the
        // prompt focused on what actually differs.
        var prefix = CommonPrefix(removed, added);
        var suffix = CommonSuffix(removed, added, prefix);

        for (var i = prefix; i < removed.Length - suffix && shown < MaxLines; i++, shown++)
            rows.Add(Line(theme, glyphs.Removed, theme.DiffRemove, removed[i]));

        for (var i = prefix; i < added.Length - suffix && shown < MaxLines; i++, shown++)
            rows.Add(Line(theme, glyphs.Added, theme.DiffAdd, added[i]));

        var total = (removed.Length - prefix - suffix) + (added.Length - prefix - suffix);
        if (total > shown)
            rows.Add(new Markup($"[{theme.Faint}]… {total - shown} more line(s)[/]"));

        if (rows.Count == 0)
            rows.Add(new Markup($"[{theme.Faint}](no textual change)[/]"));

        return new Rows(rows);
    }

    private static IRenderable Line(Theme theme, string marker, string colour, string text)
    {
        var clipped = text.Length <= 110 ? text : text[..110] + "…";
        return new Markup($"[{colour}]{marker} {Markup.Escape(clipped)}[/]");
    }

    private static int CommonPrefix(string[] a, string[] b)
    {
        var i = 0;
        while (i < a.Length && i < b.Length && a[i] == b[i])
            i++;

        return i;
    }

    private static int CommonSuffix(string[] a, string[] b, int prefix)
    {
        var i = 0;
        while (i < a.Length - prefix && i < b.Length - prefix && a[^(i + 1)] == b[^(i + 1)])
            i++;

        return i;
    }

    private static bool TryGetString(System.Text.Json.JsonElement element, string name, out string value)
    {
        if (element.TryGetProperty(name, out var property) &&
            property.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            value = property.GetString() ?? "";
            return true;
        }

        value = "";
        return false;
    }
}
