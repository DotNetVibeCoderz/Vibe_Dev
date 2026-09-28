using DotCode.Abstractions;
using DotCode.Sdk;
using DotCode.Tui.Components;
using DotCode.Tui.Rendering;
using DotCode.Tui.Themes;

namespace DotCode.Tests;

public sealed class TuiTests
{
    [Theory]
    [InlineData("hello", 5)]
    [InlineData("\u001b[31mred\u001b[0m", 3)]
    [InlineData("中文", 4)]
    [InlineData("á", 1)]
    public void Measures_display_width(string text, int width) => Assert.Equal(width, TextWidth.Of(text));

    [Fact]
    public void Wraps_without_exceeding_width_and_keeps_styles()
    {
        var text = "\u001b[1m" + string.Join(' ', Enumerable.Repeat("word", 30)) + "\u001b[0m";
        var lines = TextWidth.Wrap(text, 20);
        Assert.All(lines, l => Assert.True(TextWidth.Of(l) <= 20));
        Assert.True(lines.Count > 5);
        Assert.StartsWith("\u001b[1m", lines[1]);
    }

    [Fact]
    public void Truncate_adds_ellipsis() => Assert.Equal("abc…", TextWidth.Truncate("abcdefgh", 4));

    [Fact]
    public void Markdown_renders_headings_lists_code_and_tables()
    {
        var md = "# Title\n\nSome **bold** and `code`.\n\n- one\n- two\n\n```cs\nvar x = 1; // c\n```\n\n| a | b |\n|---|---|\n| 1 | 2 |";
        var lines = Markdown.Render(md, 60, Theme.Dark());
        var plain = lines.Select(Ansi.Strip).ToList();
        Assert.Contains("Title", plain[0]);
        Assert.Contains(plain, l => l.StartsWith("- one", StringComparison.Ordinal));
        Assert.Contains(plain, l => l.Contains("var x = 1; // c"));
        Assert.Contains(plain, l => l.Contains("│ 1"));
        Assert.All(lines, l => Assert.True(TextWidth.Of(l) <= 60));
    }

    [Fact]
    public void All_builtin_themes_load_with_every_glyph_set()
    {
        foreach (var name in Theme.BuiltinNames())
            foreach (var glyphs in new[] { "unicode", "ascii", "nerd" })
            {
                var theme = Theme.Load(name, new Engine.Configuration.TuiSettings { Glyphs = glyphs, Spinner = "dots" });
                var blocks = new Blocks(theme);
                Assert.NotEmpty(blocks.DiffLines("@@ -1,1 +1,1 @@\n-a\n+b", 40));
                Assert.Contains(theme.Glyphs.Ellipsis, Ansi.Strip(SpinnerLine.Render(theme, 1, "Testing", TimeSpan.FromSeconds(3), 10, true, false, null, 80)));
            }
    }

    [Fact]
    public void Custom_json_theme_overrides_colors()
    {
        var t = Theme.FromJson("""{"base":"light","colors":{"brand":"#123456"},"border":"double"}""", "mine");
        Assert.Equal("#123456", t.Brand.ToString());
        Assert.False(t.IsDark);
    }

    [Fact]
    public void Diff_block_shows_line_numbers()
    {
        var lines = new Blocks(Theme.Dark()).DiffLines("@@ -10,2 +10,2 @@\n context\n-old\n+new", 50).Select(Ansi.Strip).ToList();
        Assert.Contains(lines, l => l.Contains("11 -old"));
        Assert.Contains(lines, l => l.Contains("11 +new"));
    }
}

/// <summary>SDK conformance: the same scenario must behave identically in-process and over JSON-RPC.</summary>
public sealed class SdkConformanceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dc-sdk-" + Guid.NewGuid().ToString("n")[..8]);

    public SdkConformanceTests()
    {
        Directory.CreateDirectory(_dir);
        Environment.SetEnvironmentVariable("DOTCODE_CONFIG_DIR", Path.Combine(_dir, ".cfg"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private SessionOptions Options(List<AgentEvent> events)
    {
        var script = Path.Combine(_dir, "s.json");
        File.WriteAllText(script, """
            {"responses":[
              {"text":"Checking the weather.","toolCalls":[{"name":"get_weather","input":{"city":"Bogor"}}]},
              {"text":"It is rainy in Bogor."}
            ]}
            """);
        return new SessionOptions
        {
            Cwd = _dir,
            Model = "mock:scripted",
            SettingsJson = """{"providers":{"mock":{"type":"mock","script":"SCRIPT"}}}""".Replace("SCRIPT", script.Replace("\\", "\\\\")),
            PersistSession = false,
            NoMcp = true,
            Tools = [DotCodeTool.Create("get_weather", "Weather for a city", """{"type":"object","properties":{"city":{"type":"string"}},"required":["city"]}""",
                input => $"{input.GetString("city")}: rainy, 24°C", readOnly: true)],
            OnEvent = events.Add,
        };
    }

    [Fact]
    public async Task In_process_session_runs_host_tools()
    {
        var events = new List<AgentEvent>();
        await using var client = new DotCodeClient(new DotCodeClientOptions { Mode = ClientMode.InProcess, Cwd = _dir });
        await using var session = await client.CreateSessionAsync(Options(events));
        var result = await session.SendAsync("weather?");
        Assert.Equal("It is rainy in Bogor.", result.Result);
        Assert.Contains(events.OfType<ToolCompletedEvent>(), e => e.Name == "get_weather" && e.Output.Contains("rainy"));
    }

    [Fact]
    public async Task Remote_session_over_json_rpc_matches_in_process_behavior()
    {
        var cli = FindCli();
        if (cli is null) return; // CLI not built in this configuration
        var events = new List<AgentEvent>();
        await using var client = new DotCodeClient(new DotCodeClientOptions { CliPath = cli, Cwd = _dir, Environment = new Dictionary<string, string> { ["DOTCODE_CONFIG_DIR"] = Path.Combine(_dir, ".cfg") } });
        await using var session = await client.CreateSessionAsync(Options(events));
        var streamed = new List<AgentEvent>();
        await foreach (var e in session.StreamAsync("weather?")) streamed.Add(e);
        Assert.IsType<TurnCompletedEvent>(streamed[^1]);
        Assert.Equal("It is rainy in Bogor.", ((TurnCompletedEvent)streamed[^1]).ResultText);
        Assert.Contains(streamed.OfType<ToolCompletedEvent>(), e => e.Name == "get_weather");
        var messages = await session.GetMessagesAsync();
        Assert.Equal(4, messages.Count);
    }

    private static string? FindCli()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DotCode.slnx"))) dir = dir.Parent;
        if (dir is null) return null;
        foreach (var config in new[] { "Debug", "Release" })
        {
            var p = Path.Combine(dir.FullName, "src", "DotCode.Cli", "bin", config, "net10.0", "dotcode.dll");
            if (File.Exists(p)) return p;
        }
        return null;
    }
}
