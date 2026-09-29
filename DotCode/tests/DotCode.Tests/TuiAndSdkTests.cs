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

/// <summary>Tool arguments for the SDK tests (source-generated JSON, as a NativeAOT host would use).</summary>
public sealed record WeatherArgs([property: System.ComponentModel.Description("City name")] string City, string? Unit = null);

[System.Text.Json.Serialization.JsonSerializable(typeof(WeatherArgs))]
[System.Text.Json.Serialization.JsonSourceGenerationOptions(PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase)]
public sealed partial class SdkTestJson : System.Text.Json.Serialization.JsonSerializerContext;

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

    private static readonly DotCodeTool Weather = DotCodeTool.DefineTool("get_weather", "Weather for a city",
        (WeatherArgs args, ToolInvocation inv, CancellationToken _) => Task.FromResult<ToolResult>($"{args.City}: rainy, 24°C ({inv.ToolName})"),
        SdkTestJson.Default.WeatherArgs).AsReadOnly();

    private SessionConfig Config(List<AgentEvent> events, string responses = """
        [
          {"text":"Checking the weather.","toolCalls":[{"name":"get_weather","input":{"city":"Bogor"}}]},
          {"text":"It is rainy in Bogor."}
        ]
        """, IReadOnlyList<DotCodeTool>? tools = null)
    {
        var script = Path.Combine(_dir, "s.json");
        File.WriteAllText(script, "{\"responses\":" + responses + "}");
        return new SessionConfig
        {
            WorkingDirectory = _dir,
            Model = "mock:scripted",
            Providers = new Dictionary<string, DotCode.Providers.ProviderConfig> { ["mock"] = new() { Type = "mock", Script = script } },
            PersistSession = false,
            DisableMcp = true,
            Tools = tools ?? [Weather],
            OnEvent = events.Add,
        };
    }

    [Fact]
    public void Tool_schema_is_generated_from_the_arguments_type()
    {
        var schema = Weather.InputSchema;
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.Equal("City name", schema.GetProperty("properties").GetProperty("city").GetProperty("description").GetString());
        Assert.Equal(["city"], schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
    }

    [Fact]
    public async Task In_process_session_runs_typed_host_tools()
    {
        var events = new List<AgentEvent>();
        await using var client = new DotCodeClient(new DotCodeClientOptions { Mode = ClientMode.InProcess, Cwd = _dir });
        await using var session = await client.CreateSessionAsync(Config(events));
        var completed = new List<ToolCompletedEvent>();
        using var _ = session.On<ToolCompletedEvent>(completed.Add);
        var result = await session.SendAndWaitAsync("weather?");
        Assert.Equal("It is rainy in Bogor.", result.Result);
        Assert.Contains(completed, e => e.Name == "get_weather" && e.Output.Contains("rainy, 24°C (get_weather)"));
        Assert.Contains(events, e => e is AssistantTextDeltaEvent);
    }

    [Fact]
    public async Task Invalid_arguments_are_reported_to_the_model()
    {
        var events = new List<AgentEvent>();
        await using var client = new DotCodeClient(new DotCodeClientOptions { Mode = ClientMode.InProcess, Cwd = _dir });
        await using var session = await client.CreateSessionAsync(Config(events, """
            [{"toolCalls":[{"name":"get_weather","input":{"city":42}}]},{"text":"done"}]
            """));
        var result = await session.SendAndWaitAsync("weather?", TimeSpan.FromSeconds(60));
        Assert.Equal("done", result.Result);
        Assert.Contains(events.OfType<ToolCompletedEvent>(), e => e.IsError && e.Output.Contains("invalid arguments"));
    }

    [Fact]
    public async Task Remote_session_over_json_rpc_matches_in_process_behavior()
    {
        var cli = FindCli();
        if (cli is null) return; // CLI not built in this configuration
        var events = new List<AgentEvent>();
        await using var client = new DotCodeClient(new DotCodeClientOptions { CliPath = cli, Cwd = _dir, Environment = new Dictionary<string, string> { ["DOTCODE_CONFIG_DIR"] = Path.Combine(_dir, ".cfg") } });
        await client.StartAsync();
        await using var session = await client.CreateSessionAsync(Config(events));
        var streamed = new List<AgentEvent>();
        await foreach (var e in session.StreamAsync("weather?")) streamed.Add(e);
        Assert.IsType<TurnCompletedEvent>(streamed[^1]);
        Assert.Equal("It is rainy in Bogor.", ((TurnCompletedEvent)streamed[^1]).ResultText);
        Assert.Contains(streamed.OfType<ToolCompletedEvent>(), e => e.Name == "get_weather" && e.Output.Contains("(get_weather)"));
        var messages = await session.GetMessagesAsync();
        Assert.Equal(4, messages.Count);
    }

    [Fact]
    public async Task Remote_permission_handler_and_send_events()
    {
        var cli = FindCli();
        if (cli is null) return;
        var target = Path.Combine(_dir, "out.txt");
        var events = new List<AgentEvent>();
        var asked = new List<string>();
        await using var client = new DotCodeClient(new DotCodeClientOptions { CliPath = cli, Cwd = _dir, Environment = new Dictionary<string, string> { ["DOTCODE_CONFIG_DIR"] = Path.Combine(_dir, ".cfg") } });
        var config = Config(events, "[{\"toolCalls\":[{\"name\":\"Write\",\"input\":{\"file_path\":" + System.Text.Json.JsonSerializer.Serialize(target, DotCode.Abstractions.AbstractionsJsonContext.Default.String) + ",\"content\":\"from dotnet\"}}]},{\"text\":\"written\"}]", tools: []);
        await using var session = await client.CreateSessionAsync(new SessionConfig
        {
            WorkingDirectory = config.WorkingDirectory, Model = config.Model, Providers = config.Providers, PersistSession = false, DisableMcp = true,
            OnPermissionRequest = (request, _, _) => { asked.Add(request.ToolName); return Task.FromResult(PermissionDecision.ApproveOnce()); },
        });
        var done = new TaskCompletionSource<TurnCompletedEvent>();
        using var _ = session.On<TurnCompletedEvent>(e => done.TrySetResult(e));
        await session.SendAsync("write a file");
        var turn = await done.Task.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal("written", turn.ResultText);
        Assert.Equal(["Write"], asked);
        Assert.Equal("from dotnet", File.ReadAllText(target));
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
