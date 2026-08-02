// Auto Code — Gravicode Studios (Kang Fadhil)

using Spectre.Console;

namespace AutoCode.Cli.Ui;

/// <summary>
/// The colour vocabulary for the terminal.
///
/// EN: six roles, not a colour per message type. Auto Code draws inside someone else's terminal
/// theme, so it spends colour sparingly and leans on structure — indentation, the trace spine,
/// alignment — to carry meaning. Amber is the product's accent, shared with Auto Code Studio,
/// because one product should have one identity; it also sidesteps the cyan-and-green every other
/// CLI already occupies.
/// ID: enam peran warna, bukan satu warna per jenis pesan. Auto Code berjalan di dalam tema terminal
/// milik pengguna, jadi warna dipakai hemat dan makna dibawa oleh struktur. Amber adalah aksen
/// produk, sama dengan Auto Code Studio, agar satu produk punya satu identitas.
/// </summary>
public sealed class Theme
{
    /// <summary>The agent's own identity: the mark, the prompt, the primary action.</summary>
    public string Accent { get; init; } = "orange1";

    /// <summary>Tool names and other things the user reads closely.</summary>
    public string Strong { get; init; } = "white";

    /// <summary>Secondary text: results, counts, descriptions.</summary>
    public string Muted { get; init; } = "grey62";

    /// <summary>Structure only — the spine, rules, meters. Never carries meaning alone.</summary>
    public string Faint { get; init; } = "grey39";

    /// <summary>Streamed reasoning. Dim and italic so it reads as an aside, not as output.</summary>
    public string Thinking { get; init; } = "grey42 italic";

    public string Success { get; init; } = "green3";
    public string Warning { get; init; } = "orange3";
    public string Error { get; init; } = "red3";

    /// <summary>Code spans and fenced blocks inside assistant prose.</summary>
    public string Code { get; init; } = "skyblue2";

    /// <summary>Diff lines in an approval prompt.</summary>
    public string DiffAdd { get; init; } = "green3";
    public string DiffRemove { get; init; } = "red3";

    public Color AccentColor { get; init; } = Color.Orange1;

    public static Theme Default { get; } = new();

    /// <summary>
    /// For NO_COLOR and for terminals that report no colour support. Weight and italics still
    /// differentiate, so the interface stays legible without hue.
    /// </summary>
    public static Theme Plain { get; } = new()
    {
        Accent = "bold",
        Strong = "bold",
        Muted = "default",
        Faint = "dim",
        Thinking = "dim italic",
        Success = "bold",
        Warning = "bold",
        Error = "bold",
        Code = "default",
        DiffAdd = "bold",
        DiffRemove = "dim",
        AccentColor = Color.Default,
    };

    /// <summary>Picks a palette from the environment, honouring NO_COLOR.</summary>
    public static Theme Detect()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR")))
            return Plain;

        return AnsiConsole.Profile.Capabilities.ColorSystem == ColorSystem.NoColors ? Plain : Default;
    }
}
