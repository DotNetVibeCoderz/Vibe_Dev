using System.Text.Json;
using System.Text.Json.Nodes;
using DotCode.Abstractions;
using DotCode.Engine;
using DotCode.Engine.Configuration;
using DotCode.Tui.Rendering;

namespace DotCode.Tui.Themes;

/// <summary>Glyph set: "unicode" matches Claude Code's look; "ascii" suits fonts without symbol glyphs;
/// "nerd" uses Nerd Font icons.</summary>
public sealed record Glyphs(
    string Dot, string Result, string Prompt, string Pointer, string Check, string Cross,
    string TodoDone, string TodoOpen, string TodoActive, string AcceptEdits, string PlanMode, string Bypass,
    string Bullet, string Ellipsis, string Up, string Down, string Star, string Warning, string Info, string Mascot)
{
    public static readonly Glyphs Unicode = new("●", "⎿", ">", "❯", "✔", "✘", "☒", "☐", "◼", "⏵⏵", "⏸", "⏵⏵", "•", "…", "↑", "↓", "✻", "⚠", "ℹ", "unicode");
    public static readonly Glyphs Ascii = new("*", "L", ">", ">", "v", "x", "[x]", "[ ]", "[~]", ">>", "||", ">>", "-", "...", "^", "v", "*", "!", "i", "ascii");
    public static readonly Glyphs Nerd = new("", "╰", "", "", "", "", "", "", "", "", "", "", "", "…", "", "", "", "", "", "unicode");

    public static Glyphs ByName(string? name) => name?.ToLowerInvariant() switch
    {
        "ascii" => Ascii,
        "nerd" or "nerdfont" => Nerd,
        _ => Unicode,
    };
}

public sealed record BorderSet(string TopLeft, string TopRight, string BottomLeft, string BottomRight, string Horizontal, string Vertical)
{
    public static readonly BorderSet Rounded = new("╭", "╮", "╰", "╯", "─", "│");
    public static readonly BorderSet Single = new("┌", "┐", "└", "┘", "─", "│");
    public static readonly BorderSet Double = new("╔", "╗", "╚", "╝", "═", "║");
    public static readonly BorderSet Heavy = new("┏", "┓", "┗", "┛", "━", "┃");
    public static readonly BorderSet AsciiBox = new("+", "+", "+", "+", "-", "|");

    public static BorderSet ByName(string? name) => name?.ToLowerInvariant() switch
    {
        "single" => Single,
        "double" => Double,
        "heavy" or "bold" => Heavy,
        "ascii" => AsciiBox,
        _ => Rounded,
    };
}

public static class Spinners
{
    public static string[] ByName(string? name, bool ascii) => (ascii ? "line" : name?.ToLowerInvariant()) switch
    {
        "dots" => ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"],
        "line" => ["-", "\\", "|", "/"],
        "star" => ["✶", "✸", "✹", "✺", "✹", "✷"],
        "bounce" => ["⠁", "⠂", "⠄", "⡀", "⢀", "⠠", "⠐", "⠈"],
        "arc" => ["◜", "◠", "◝", "◞", "◡", "◟"],
        "dotnet" => ["·", "•", "●", "•"],
        // Claude Code style: grows then shrinks.
        _ => ["·", "✢", "✳", "✶", "✻", "✽", "✽", "✻", "✶", "✳", "✢", "·"],
    };
}

/// <summary>Color theme. Built-in themes reproduce Claude Code's palettes (dark, light, daltonized, ANSI) plus
/// DotCode originals; users can add JSON themes in ~/.dotcode/themes.</summary>
public sealed partial class Theme
{
    public required string Name { get; init; }
    public required string DisplayName { get; init; }
    public bool IsDark { get; init; } = true;
    public Rgb Brand { get; init; }
    public Rgb BrandShimmer { get; init; }
    public Rgb Text { get; init; }
    public Rgb Secondary { get; init; }
    public Rgb Subtle { get; init; }
    public Rgb Suggestion { get; init; }
    public Rgb Permission { get; init; }
    public Rgb PlanMode { get; init; }
    public Rgb AutoAccept { get; init; }
    public Rgb Bash { get; init; }
    public Rgb Error { get; init; }
    public Rgb Success { get; init; }
    public Rgb Warning { get; init; }
    public Rgb DiffAddedBg { get; init; }
    public Rgb DiffRemovedBg { get; init; }
    public Rgb DiffAddedWord { get; init; }
    public Rgb DiffRemovedWord { get; init; }
    public Rgb UserMessageBg { get; init; }
    public Rgb PromptBorder { get; init; }
    public Rgb InlineCode { get; init; }
    public Rgb Link { get; init; }
    public Rgb CodeKeyword { get; init; }
    public Rgb CodeString { get; init; }
    public Rgb CodeComment { get; init; }
    public Rgb CodeNumber { get; init; }
    public Rgb CodeType { get; init; }

    public Glyphs Glyphs { get; set; } = Glyphs.Unicode;
    public BorderSet Border { get; set; } = BorderSet.Rounded;
    public string[] SpinnerFrames { get; set; } = Spinners.ByName(null, false);
    public bool ReducedMotion { get; set; }
    public bool Ascii => Glyphs.Mascot == "ascii";

    // ---- styling helpers ----
    public string C(string text, Rgb color) => Ansi.Color(text, color);
    public string B(string text) => Ansi.Styled(text, Ansi.Bold);
    public string Dim(string text) => Ansi.Color(text, Secondary);
    public string Faint(string text) => Ansi.Color(text, Subtle);
    public string Accent(string text) => Ansi.Color(text, Brand);

    public Theme With(Glyphs glyphs, BorderSet border, string[] spinner, bool reducedMotion)
    {
        Glyphs = glyphs;
        Border = border;
        SpinnerFrames = spinner;
        ReducedMotion = reducedMotion;
        return this;
    }

    private static Rgb H(string hex) => Rgb.Hex(hex);

    public static Theme Dark() => new()
    {
        Name = "dark", DisplayName = "Dark mode",
        Brand = H("#D77757"), BrandShimmer = H("#F5B99E"), Text = H("#FFFFFF"), Secondary = H("#999999"), Subtle = H("#505050"),
        Suggestion = H("#B1B9F9"), Permission = H("#B1B9F9"), PlanMode = H("#48968C"), AutoAccept = H("#AF87FF"), Bash = H("#FD5DB1"),
        Error = H("#FF6B80"), Success = H("#4EBA65"), Warning = H("#FFC107"),
        DiffAddedBg = H("#225C2B"), DiffRemovedBg = H("#7A2936"), DiffAddedWord = H("#38A660"), DiffRemovedWord = H("#B3596B"),
        UserMessageBg = H("#373737"), PromptBorder = H("#888888"), InlineCode = H("#B1B9F9"), Link = H("#6FB3F2"),
        CodeKeyword = H("#C678DD"), CodeString = H("#98C379"), CodeComment = H("#7F848E"), CodeNumber = H("#D19A66"), CodeType = H("#E5C07B"),
    };

    public static Theme Light() => new()
    {
        Name = "light", DisplayName = "Light mode", IsDark = false,
        Brand = H("#D77757"), BrandShimmer = H("#F0A382"), Text = H("#000000"), Secondary = H("#666666"), Subtle = H("#AFAFAF"),
        Suggestion = H("#5769F7"), Permission = H("#5769F7"), PlanMode = H("#006666"), AutoAccept = H("#8700FF"), Bash = H("#FF0087"),
        Error = H("#AB2B3F"), Success = H("#2C7A39"), Warning = H("#966C1E"),
        DiffAddedBg = H("#69DB7C"), DiffRemovedBg = H("#FFA8B4"), DiffAddedWord = H("#2F9D44"), DiffRemovedWord = H("#D1454B"),
        UserMessageBg = H("#F0F0F0"), PromptBorder = H("#999999"), InlineCode = H("#5769F7"), Link = H("#0B63C5"),
        CodeKeyword = H("#A626A4"), CodeString = H("#50A14F"), CodeComment = H("#A0A1A7"), CodeNumber = H("#986801"), CodeType = H("#C18401"),
    };

    public static Theme DarkDaltonized() => Dark().Copy("dark-daltonized", "Dark mode (colorblind-friendly)", t => t with
    {
        Success = H("#3399FF"), Error = H("#FF6600"), DiffAddedBg = H("#004466"), DiffRemovedBg = H("#663300"),
        DiffAddedWord = H("#0077B3"), DiffRemovedWord = H("#B35900"),
    });

    public static Theme LightDaltonized() => Light().Copy("light-daltonized", "Light mode (colorblind-friendly)", t => t with
    {
        Success = H("#006699"), Error = H("#CC0000"), DiffAddedBg = H("#99CCFF"), DiffRemovedBg = H("#FFCC99"),
        DiffAddedWord = H("#3366CC"), DiffRemovedWord = H("#CC6600"),
    });

    public static Theme DarkAnsi() => Dark().Copy("dark-ansi", "Dark mode (ANSI colors only)", t => t with { Brand = H("#FFAF5F") });
    public static Theme LightAnsi() => Light().Copy("light-ansi", "Light mode (ANSI colors only)", t => t with { });

    public static Theme DotNet() => Dark().Copy("dotnet", "DotCode .NET purple", t => t with
    {
        Brand = H("#9B7BFF"), BrandShimmer = H("#D1C4FF"), Suggestion = H("#8BD5FF"), Permission = H("#8BD5FF"),
        AutoAccept = H("#FF9BD2"), UserMessageBg = H("#2A2140"), InlineCode = H("#C3B1FF"), PromptBorder = H("#6E5BA8"),
    });

    public static Theme Dracula() => Dark().Copy("dracula", "Dracula", t => t with
    {
        Brand = H("#FF79C6"), BrandShimmer = H("#FFB8E1"), Text = H("#F8F8F2"), Secondary = H("#A0A4C0"), Subtle = H("#44475A"),
        Suggestion = H("#BD93F9"), Permission = H("#BD93F9"), PlanMode = H("#8BE9FD"), AutoAccept = H("#FFB86C"), Bash = H("#FF79C6"),
        Error = H("#FF5555"), Success = H("#50FA7B"), Warning = H("#F1FA8C"), UserMessageBg = H("#343746"),
        DiffAddedBg = H("#1F4D2E"), DiffRemovedBg = H("#5C2230"), InlineCode = H("#8BE9FD"), PromptBorder = H("#6272A4"),
        CodeKeyword = H("#FF79C6"), CodeString = H("#F1FA8C"), CodeComment = H("#6272A4"), CodeNumber = H("#BD93F9"), CodeType = H("#8BE9FD"),
    });

    public static Theme Nord() => Dark().Copy("nord", "Nord", t => t with
    {
        Brand = H("#88C0D0"), BrandShimmer = H("#C5E5EE"), Text = H("#ECEFF4"), Secondary = H("#9AA5B8"), Subtle = H("#4C566A"),
        Suggestion = H("#81A1C1"), Permission = H("#81A1C1"), PlanMode = H("#8FBCBB"), AutoAccept = H("#B48EAD"), Bash = H("#D08770"),
        Error = H("#BF616A"), Success = H("#A3BE8C"), Warning = H("#EBCB8B"), UserMessageBg = H("#3B4252"),
        DiffAddedBg = H("#3B4F3A"), DiffRemovedBg = H("#5A3A40"), InlineCode = H("#8FBCBB"), PromptBorder = H("#616E88"),
        CodeKeyword = H("#81A1C1"), CodeString = H("#A3BE8C"), CodeComment = H("#616E88"), CodeNumber = H("#B48EAD"), CodeType = H("#8FBCBB"),
    });

    public static Theme SolarizedDark() => Dark().Copy("solarized-dark", "Solarized Dark", t => t with
    {
        Brand = H("#CB4B16"), BrandShimmer = H("#F08B5E"), Text = H("#EEE8D5"), Secondary = H("#93A1A1"), Subtle = H("#586E75"),
        Suggestion = H("#268BD2"), Permission = H("#268BD2"), PlanMode = H("#2AA198"), AutoAccept = H("#6C71C4"), Bash = H("#D33682"),
        Error = H("#DC322F"), Success = H("#859900"), Warning = H("#B58900"), UserMessageBg = H("#073642"),
        DiffAddedBg = H("#2B4A1B"), DiffRemovedBg = H("#5A1F1D"), InlineCode = H("#2AA198"), PromptBorder = H("#586E75"),
        CodeKeyword = H("#859900"), CodeString = H("#2AA198"), CodeComment = H("#586E75"), CodeNumber = H("#D33682"), CodeType = H("#B58900"),
    });

    public static Theme Monokai() => Dark().Copy("monokai", "Monokai", t => t with
    {
        Brand = H("#FD971F"), BrandShimmer = H("#FFC98A"), Text = H("#F8F8F2"), Secondary = H("#A59F85"), Subtle = H("#49483E"),
        Suggestion = H("#66D9EF"), Permission = H("#66D9EF"), PlanMode = H("#A6E22E"), AutoAccept = H("#AE81FF"), Bash = H("#F92672"),
        Error = H("#F92672"), Success = H("#A6E22E"), Warning = H("#E6DB74"), UserMessageBg = H("#3E3D32"),
        DiffAddedBg = H("#3A4A1A"), DiffRemovedBg = H("#5A1F2E"), InlineCode = H("#66D9EF"), PromptBorder = H("#75715E"),
        CodeKeyword = H("#F92672"), CodeString = H("#E6DB74"), CodeComment = H("#75715E"), CodeNumber = H("#AE81FF"), CodeType = H("#66D9EF"),
    });

    public static Theme Gruvbox() => Dark().Copy("gruvbox", "Gruvbox", t => t with
    {
        Brand = H("#FE8019"), BrandShimmer = H("#FFC18A"), Text = H("#EBDBB2"), Secondary = H("#A89984"), Subtle = H("#504945"),
        Suggestion = H("#83A598"), Permission = H("#83A598"), PlanMode = H("#8EC07C"), AutoAccept = H("#D3869B"), Bash = H("#FB4934"),
        Error = H("#FB4934"), Success = H("#B8BB26"), Warning = H("#FABD2F"), UserMessageBg = H("#3C3836"),
        DiffAddedBg = H("#3D4220"), DiffRemovedBg = H("#5A2420"), InlineCode = H("#8EC07C"), PromptBorder = H("#665C54"),
        CodeKeyword = H("#FB4934"), CodeString = H("#B8BB26"), CodeComment = H("#928374"), CodeNumber = H("#D3869B"), CodeType = H("#FABD2F"),
    });

    public static Theme Catppuccin() => Dark().Copy("catppuccin", "Catppuccin Mocha", t => t with
    {
        Brand = H("#F5A97F"), BrandShimmer = H("#FAD6C0"), Text = H("#CDD6F4"), Secondary = H("#A6ADC8"), Subtle = H("#45475A"),
        Suggestion = H("#89B4FA"), Permission = H("#B4BEFE"), PlanMode = H("#94E2D5"), AutoAccept = H("#CBA6F7"), Bash = H("#F5C2E7"),
        Error = H("#F38BA8"), Success = H("#A6E3A1"), Warning = H("#F9E2AF"), UserMessageBg = H("#313244"),
        DiffAddedBg = H("#2E4A3A"), DiffRemovedBg = H("#533246"), InlineCode = H("#94E2D5"), PromptBorder = H("#6C7086"),
        CodeKeyword = H("#CBA6F7"), CodeString = H("#A6E3A1"), CodeComment = H("#6C7086"), CodeNumber = H("#FAB387"), CodeType = H("#F9E2AF"),
    });

    public static Theme Matrix() => Dark().Copy("matrix", "Matrix", t => t with
    {
        Brand = H("#00FF41"), BrandShimmer = H("#B3FFC6"), Text = H("#C8FFD4"), Secondary = H("#4F9A62"), Subtle = H("#1E4D2B"),
        Suggestion = H("#00FF41"), Permission = H("#39FF88"), PlanMode = H("#00B32C"), AutoAccept = H("#7CFFA0"), Bash = H("#00FF41"),
        Error = H("#FF4D4D"), Success = H("#00FF41"), Warning = H("#D4FF00"), UserMessageBg = H("#0D2614"),
        DiffAddedBg = H("#0F4020"), DiffRemovedBg = H("#401010"), InlineCode = H("#7CFFA0"), PromptBorder = H("#1E7A3A"),
        CodeKeyword = H("#39FF88"), CodeString = H("#B3FFC6"), CodeComment = H("#2E6B3E"), CodeNumber = H("#D4FF00"), CodeType = H("#7CFFA0"),
    });

    public static Theme Ocean() => Light().Copy("ocean-light", "Ocean (light)", t => t with
    {
        Brand = H("#0077B6"), BrandShimmer = H("#48CAE4"), Suggestion = H("#0096C7"), Permission = H("#023E8A"),
        UserMessageBg = H("#E6F4FA"), InlineCode = H("#0077B6"), PromptBorder = H("#90E0EF"),
    });

    private Theme Copy(string name, string display, Func<Palette, Palette> change)
    {
        var p = change(new Palette(Brand, BrandShimmer, Text, Secondary, Subtle, Suggestion, Permission, PlanMode, AutoAccept, Bash, Error, Success, Warning,
            DiffAddedBg, DiffRemovedBg, DiffAddedWord, DiffRemovedWord, UserMessageBg, PromptBorder, InlineCode, Link, CodeKeyword, CodeString, CodeComment, CodeNumber, CodeType));
        return p.ToTheme(name, display, IsDark);
    }

    private sealed record Palette(Rgb Brand, Rgb BrandShimmer, Rgb Text, Rgb Secondary, Rgb Subtle, Rgb Suggestion, Rgb Permission, Rgb PlanMode, Rgb AutoAccept,
        Rgb Bash, Rgb Error, Rgb Success, Rgb Warning, Rgb DiffAddedBg, Rgb DiffRemovedBg, Rgb DiffAddedWord, Rgb DiffRemovedWord, Rgb UserMessageBg,
        Rgb PromptBorder, Rgb InlineCode, Rgb Link, Rgb CodeKeyword, Rgb CodeString, Rgb CodeComment, Rgb CodeNumber, Rgb CodeType)
    {
        public Theme ToTheme(string name, string display, bool dark) => new()
        {
            Name = name, DisplayName = display, IsDark = dark, Brand = Brand, BrandShimmer = BrandShimmer, Text = Text, Secondary = Secondary, Subtle = Subtle,
            Suggestion = Suggestion, Permission = Permission, PlanMode = PlanMode, AutoAccept = AutoAccept, Bash = Bash, Error = Error, Success = Success,
            Warning = Warning, DiffAddedBg = DiffAddedBg, DiffRemovedBg = DiffRemovedBg, DiffAddedWord = DiffAddedWord, DiffRemovedWord = DiffRemovedWord,
            UserMessageBg = UserMessageBg, PromptBorder = PromptBorder, InlineCode = InlineCode, Link = Link, CodeKeyword = CodeKeyword,
            CodeString = CodeString, CodeComment = CodeComment, CodeNumber = CodeNumber, CodeType = CodeType,
        };
    }

    private static readonly (string Name, Func<Theme> Factory)[] Builtins =
    [
        ("dark", Dark), ("light", Light), ("dark-daltonized", DarkDaltonized), ("light-daltonized", LightDaltonized),
        ("dark-ansi", DarkAnsi), ("light-ansi", LightAnsi), ("dotnet", DotNet), ("dracula", Dracula), ("nord", Nord),
        ("solarized-dark", SolarizedDark), ("monokai", Monokai), ("gruvbox", Gruvbox), ("catppuccin", Catppuccin), ("matrix", Matrix), ("ocean-light", Ocean),
    ];

    public static IEnumerable<string> BuiltinNames() => Builtins.Select(b => b.Name).Concat(CustomThemeFiles().Select(Path.GetFileNameWithoutExtension)!)!;

    public static IEnumerable<(string Name, string Display)> All() =>
        Builtins.Select(b => (b.Name, b.Factory().DisplayName))
            .Concat(CustomThemeFiles().Select(f => (Path.GetFileNameWithoutExtension(f), Path.GetFileNameWithoutExtension(f) + " (custom)")));

    private static IEnumerable<string> CustomThemeFiles() =>
        Directory.Exists(DotCodePaths.ThemesDir) ? Directory.EnumerateFiles(DotCodePaths.ThemesDir, "*.json") : [];

    /// <summary>Resolves a theme by name (built-in or ~/.dotcode/themes/NAME.json) and applies TUI settings.</summary>
    public static Theme Load(string? name, TuiSettings? tui)
    {
        var theme = Create(name ?? "dark");
        var glyphs = Glyphs.ByName(tui?.Glyphs ?? Environment.GetEnvironmentVariable("DOTCODE_GLYPHS"));
        theme.With(glyphs, glyphs == Glyphs.Ascii ? BorderSet.AsciiBox : BorderSet.ByName(tui?.Border ?? theme.BorderName),
            Spinners.ByName(tui?.Spinner ?? theme.SpinnerName, glyphs == Glyphs.Ascii), tui?.ReducedMotion == true);
        if (tui?.Accent is { Length: > 0 } accent)
        {
            try { theme = theme.Recolor(Rgb.Hex(accent)); } catch (FormatException) { }
        }
        // ANSI themes restrict output to the 16 basic colors; every other theme uses what the terminal supports.
        Ansi.Depth = theme.Name.EndsWith("-ansi", StringComparison.Ordinal) && Ansi.DetectedDepth != ColorDepth.None ? ColorDepth.Ansi16 : Ansi.DetectedDepth;
        return theme;
    }

    private string? BorderName { get; init; }
    private string? SpinnerName { get; init; }

    private Theme Recolor(Rgb accent)
    {
        var t = Copy(Name, DisplayName, p => p with { Brand = accent, BrandShimmer = accent.Lerp(new Rgb(255, 255, 255), 0.5) });
        return t.With(Glyphs, Border, SpinnerFrames, ReducedMotion);
    }

    private static Theme Create(string name)
    {
        foreach (var (n, f) in Builtins) if (n.Equals(name, StringComparison.OrdinalIgnoreCase)) return f();
        var file = Path.Combine(DotCodePaths.ThemesDir, name + ".json");
        if (File.Exists(file))
        {
            try { return FromJson(File.ReadAllText(file), name); }
            catch (Exception ex) when (ex is JsonException or FormatException) { }
        }
        return Dark();
    }

    /// <summary>Custom theme: <c>{"base":"dark","colors":{"brand":"#ff8800",...},"border":"double","spinner":"dots"}</c>.</summary>
    public static Theme FromJson(string json, string name)
    {
        var root = JsonNode.Parse(json, documentOptions: DotCodeJson.DocumentOptions) as JsonObject ?? new JsonObject();
        var baseTheme = Create(root["base"]?.GetValue<string>() ?? "dark");
        var colors = root["colors"] as JsonObject ?? new JsonObject();
        Rgb Get(string key, Rgb fallback) => colors[key]?.GetValue<string>() is { } hex ? Rgb.Hex(hex) : fallback;
        var t = baseTheme.Copy(name, root["displayName"]?.GetValue<string>() ?? name, p => p with
        {
            Brand = Get("brand", p.Brand), BrandShimmer = Get("brandShimmer", p.BrandShimmer), Text = Get("text", p.Text),
            Secondary = Get("secondary", p.Secondary), Subtle = Get("subtle", p.Subtle), Suggestion = Get("suggestion", p.Suggestion),
            Permission = Get("permission", p.Permission), PlanMode = Get("planMode", p.PlanMode), AutoAccept = Get("autoAccept", p.AutoAccept),
            Bash = Get("bash", p.Bash), Error = Get("error", p.Error), Success = Get("success", p.Success), Warning = Get("warning", p.Warning),
            DiffAddedBg = Get("diffAdded", p.DiffAddedBg), DiffRemovedBg = Get("diffRemoved", p.DiffRemovedBg),
            DiffAddedWord = Get("diffAddedWord", p.DiffAddedWord), DiffRemovedWord = Get("diffRemovedWord", p.DiffRemovedWord),
            UserMessageBg = Get("userMessageBackground", p.UserMessageBg), PromptBorder = Get("promptBorder", p.PromptBorder),
            InlineCode = Get("inlineCode", p.InlineCode), Link = Get("link", p.Link),
        });
        return new Theme
        {
            Name = t.Name, DisplayName = t.DisplayName, IsDark = root["dark"]?.GetValue<bool>() ?? baseTheme.IsDark,
            Brand = t.Brand, BrandShimmer = t.BrandShimmer, Text = t.Text, Secondary = t.Secondary, Subtle = t.Subtle, Suggestion = t.Suggestion,
            Permission = t.Permission, PlanMode = t.PlanMode, AutoAccept = t.AutoAccept, Bash = t.Bash, Error = t.Error, Success = t.Success,
            Warning = t.Warning, DiffAddedBg = t.DiffAddedBg, DiffRemovedBg = t.DiffRemovedBg, DiffAddedWord = t.DiffAddedWord,
            DiffRemovedWord = t.DiffRemovedWord, UserMessageBg = t.UserMessageBg, PromptBorder = t.PromptBorder, InlineCode = t.InlineCode,
            Link = t.Link, CodeKeyword = t.CodeKeyword, CodeString = t.CodeString, CodeComment = t.CodeComment, CodeNumber = t.CodeNumber, CodeType = t.CodeType,
            BorderName = root["border"]?.GetValue<string>(), SpinnerName = root["spinner"]?.GetValue<string>(),
        };
    }
}
