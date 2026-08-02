// Auto Code — Gravicode Studios (Kang Fadhil)

using Spectre.Console;

namespace AutoCode.Cli.Ui;

/// <summary>
/// The character set the interface is drawn with.
///
/// EN: glyphs are chosen for coverage, not for looks. U+23FA (⏺) and U+23BF (⎿) read well in a
/// design mock and then render as replacement boxes in Windows Terminal's default font, which is
/// where most of this product's users are. Box-drawing and geometric-shape characters are present
/// in every console font shipped in the last twenty years, so the interface uses those and keeps a
/// pure-ASCII fallback for terminals that report no Unicode support at all.
/// ID: glyph dipilih berdasarkan ketersediaan font, bukan estetika. Karakter box-drawing tersedia di
/// hampir semua font konsol, sementara simbol eksotis sering muncul sebagai kotak kosong.
/// </summary>
public sealed class Glyphs
{
    public required string ToolCall { get; init; }
    public required string TraceMid { get; init; }
    public required string TraceEnd { get; init; }
    public required string Denied { get; init; }
    public required string Thinking { get; init; }
    public required string Subagent { get; init; }
    public required string Compact { get; init; }
    public required string TodoDone { get; init; }
    public required string TodoActive { get; init; }
    public required string TodoPending { get; init; }
    public required string Prompt { get; init; }
    public required string Continuation { get; init; }
    public required string MeterFull { get; init; }
    public required string MeterEmpty { get; init; }
    public required string Added { get; init; }
    public required string Removed { get; init; }

    public static Glyphs Unicode { get; } = new()
    {
        ToolCall = "▸",
        TraceMid = "│",
        TraceEnd = "└",
        Denied = "✗",
        Thinking = "·",
        Subagent = "◆",
        Compact = "⟳",
        TodoDone = "✔",
        TodoActive = "▶",
        TodoPending = "○",
        Prompt = "›",
        Continuation = "…",
        MeterFull = "▰",
        MeterEmpty = "▱",
        Added = "+",
        Removed = "-",
    };

    public static Glyphs Ascii { get; } = new()
    {
        ToolCall = ">",
        TraceMid = "|",
        TraceEnd = "\\",
        Denied = "x",
        Thinking = ".",
        Subagent = "*",
        Compact = "~",
        TodoDone = "x",
        TodoActive = ">",
        TodoPending = "o",
        Prompt = ">",
        Continuation = "...",
        MeterFull = "#",
        MeterEmpty = "-",
        Added = "+",
        Removed = "-",
    };

    public static Glyphs Detect() =>
        AnsiConsole.Profile.Capabilities.Unicode ? Unicode : Ascii;
}
