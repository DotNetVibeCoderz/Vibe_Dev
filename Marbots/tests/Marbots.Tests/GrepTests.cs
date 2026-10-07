using System.Text.Json;
using Marbots.Abstractions;
using Marbots.Kernel;

namespace Marbots.Tests;

public sealed class GrepTests : IDisposable
{
    private readonly string _ws = Path.Combine(Path.GetTempPath(), "mb-grep-" + Guid.NewGuid().ToString("N")[..8]);

    private async Task<string> Grep(string json)
    {
        var fn = KernelCatalog.CreateDefault().First(f => f.Descriptor.Name == "grep");
        var ctx = new FunctionExecutionContext { Bot = new BotDefinition { Id = "t" }, TaskId = "t", ThreadId = "t", WorkspacePath = _ws, Services = null! };
        return (await fn.InvokeAsync(new FunctionCall("1", "grep", JsonDocument.Parse(json).RootElement), ctx, default)).Content;
    }

    private void Write(string rel, string text)
    {
        var p = Path.Combine(_ws, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, text);
    }

    [Fact]
    public async Task Finds_literals_regexes_and_anchored_lines_in_order()
    {
        Write("a/one.cs", "class A {}\r\nvar total = 42; // TODO fix\r\nend\r\n");
        Write("b/two.md", "# Laporan\nTODO: tulis ringkasan\nselesai");
        Write("node_modules/x/skip.js", "TODO hidden");
        File.WriteAllBytes(Path.Combine(_ws, "a", "bin.dat"), [84, 79, 68, 79, 0, 1, 2]);

        var todo = await Grep("""{"pattern":"TODO"}""");
        Assert.Equal(["a/one.cs:2: var total = 42; // TODO fix", "b/two.md:2: TODO: tulis ringkasan"], todo.Trim().Split(Environment.NewLine));
        Assert.Contains("a/one.cs:2:", await Grep("""{"pattern":"total\\s*=\\s*\\d+"}"""));
        // "$" anchors to the end of each line, also with CRLF line endings.
        Assert.Contains("a/one.cs:3: end", await Grep("""{"pattern":"^end$"}"""));
        Assert.Contains("b/two.md:3: selesai", await Grep("""{"pattern":"ai$"}"""));
        Assert.Equal("No matches.", await Grep("""{"pattern":"TODO","glob":"*.py"}"""));
        Assert.StartsWith("b/two.md:2:", await Grep("""{"pattern":"TODO","glob":"*.md"}"""));
        Assert.Equal("No matches.", await Grep("""{"pattern":"hidden"}"""));
    }

    [Fact]
    public async Task Stops_at_200_matches()
    {
        for (var i = 0; i < 300; i++) Write($"f{i:000}.txt", "needle\n");
        var result = await Grep("""{"pattern":"needle"}""");
        Assert.EndsWith("[truncated at 200 matches]", result);
        Assert.StartsWith("f000.txt:1: needle", result);
    }

    public void Dispose()
    {
        try { Directory.Delete(_ws, true); } catch (IOException) { }
    }
}
