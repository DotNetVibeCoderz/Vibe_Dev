// Auto Code — Gravicode Studios (Kang Fadhil)

using Spectre.Console;

namespace AutoCode.Cli.Ui;

/// <summary>
/// The colour vocabulary for the terminal.
///
/// EN: kept to six roles rather than a colour per message type. A terminal palette that assigns
/// meaning to every hue stops communicating anything, and it has to survive light backgrounds,
/// dark backgrounds and 16-colour terminals alike.
/// ID: dibatasi enam peran warna. Palet yang memberi arti pada setiap warna justru berhenti
/// menyampaikan makna, dan harus tetap terbaca di terminal terang, gelap, maupun 16 warna.
/// </summary>
public sealed class Theme
{
    public string Accent { get; init; } = "mediumpurple2";
    public string ToolBullet { get; init; } = "mediumpurple2";
    public string ToolName { get; init; } = "white";
    public string Muted { get; init; } = "grey54";
    public string Thinking { get; init; } = "grey42 italic";
    public string Success { get; init; } = "green3";
    public string Warning { get; init; } = "orange1";
    public string Error { get; init; } = "red3";
    public string Prompt { get; init; } = "mediumpurple2";

    public Color AccentColor { get; init; } = Color.MediumPurple2;

    /// <summary>The default palette.</summary>
    public static Theme Default { get; } = new();

    /// <summary>
    /// A palette for terminals that only offer the basic sixteen colours, and for
    /// NO_COLOR-respecting environments where hue must not carry meaning on its own.
    /// </summary>
    public static Theme Plain { get; } = new()
    {
        Accent = "default",
        ToolBullet = "default",
        ToolName = "default",
        Muted = "default",
        Thinking = "italic",
        Success = "default",
        Warning = "default",
        Error = "default",
        Prompt = "default",
        AccentColor = Color.Default,
    };

    /// <summary>Picks a palette from the environment, honouring NO_COLOR.</summary>
    public static Theme Detect()
    {
        var noColor = Environment.GetEnvironmentVariable("NO_COLOR");

        if (!string.IsNullOrEmpty(noColor))
            return Plain;

        return AnsiConsole.Profile.Capabilities.ColorSystem == ColorSystem.NoColors ? Plain : Default;
    }
}
