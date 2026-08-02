// Auto Code — Gravicode Studios (Kang Fadhil)

using System.Text.Json;
using AutoCode.Core.Abstractions;
using AutoCode.Core.Configuration;
using AutoCode.Tools;
using Xunit;

namespace AutoCode.Tests;

public sealed class FileToolTests
{
    [Fact]
    public async Task Read_numbers_lines_and_marks_the_file_as_seen()
    {
        using var workspace = new TempWorkspace();
        workspace.Write("a.txt", "alpha\nbeta\ngamma");

        var services = new TestServices(workspace.Root);
        var result = await new ReadTool().CallAsync(services, """{"file_path":"a.txt"}""");

        Assert.True(result.IsSuccess);
        Assert.Contains("     1\talpha", result.Content);
        Assert.Contains("     3\tgamma", result.Content);
        Assert.True(services.Files.HasRead(Path.Combine(workspace.Root, "a.txt")));
    }

    [Fact]
    public async Task Read_pages_with_offset_and_limit()
    {
        using var workspace = new TempWorkspace();
        workspace.Write("a.txt", string.Join('\n', Enumerable.Range(1, 100).Select(i => $"line{i}")));

        var result = await new ReadTool().CallAsync(new TestServices(workspace.Root),
            """{"file_path":"a.txt","offset":50,"limit":2}""");

        Assert.Contains("line50", result.Content);
        Assert.Contains("line51", result.Content);
        Assert.DoesNotContain("line52", result.Content);
        Assert.Contains("more lines", result.Content);
    }

    [Fact]
    public async Task Read_reports_a_missing_file_rather_than_throwing()
    {
        using var workspace = new TempWorkspace();

        var result = await new ReadTool().CallAsync(new TestServices(workspace.Root), """{"file_path":"nope.txt"}""");

        Assert.False(result.IsSuccess);
        Assert.Contains("not found", result.Content);
    }

    [Fact]
    public async Task Write_creates_a_file_and_its_parent_directories()
    {
        using var workspace = new TempWorkspace();

        var result = await new WriteTool().CallAsync(new TestServices(workspace.Root),
            """{"file_path":"deep/nested/a.txt","content":"hello"}""");

        Assert.True(result.IsSuccess);
        Assert.Equal("hello", workspace.Read("deep/nested/a.txt"));
    }

    [Fact]
    public async Task Write_refuses_to_clobber_a_file_that_was_never_read()
    {
        using var workspace = new TempWorkspace();
        workspace.Write("a.txt", "important");

        var result = await new WriteTool().CallAsync(new TestServices(workspace.Root),
            """{"file_path":"a.txt","content":"replaced"}""");

        Assert.False(result.IsSuccess);
        Assert.Contains("has not been read", result.Content);
        Assert.Equal("important", workspace.Read("a.txt"));
    }

    [Fact]
    public async Task Edit_requires_the_file_to_have_been_read()
    {
        using var workspace = new TempWorkspace();
        workspace.Write("a.txt", "alpha");

        var result = await new EditTool().CallAsync(new TestServices(workspace.Root),
            """{"file_path":"a.txt","old_string":"alpha","new_string":"beta"}""");

        Assert.False(result.IsSuccess);
        Assert.Contains("before editing", result.Content);
    }

    [Fact]
    public async Task Edit_replaces_a_unique_match()
    {
        using var workspace = new TempWorkspace();
        workspace.Write("a.txt", "alpha\nbeta");

        var services = new TestServices(workspace.Root);
        await new ReadTool().CallAsync(services, """{"file_path":"a.txt"}""");

        var result = await new EditTool().CallAsync(services,
            """{"file_path":"a.txt","old_string":"alpha","new_string":"omega"}""");

        Assert.True(result.IsSuccess);
        Assert.Equal("omega\nbeta", workspace.Read("a.txt"));
    }

    [Fact]
    public async Task Edit_refuses_an_ambiguous_match_unless_replace_all_is_set()
    {
        using var workspace = new TempWorkspace();
        workspace.Write("a.txt", "x\nx\nx");

        var services = new TestServices(workspace.Root);
        await new ReadTool().CallAsync(services, """{"file_path":"a.txt"}""");

        var ambiguous = await new EditTool().CallAsync(services,
            """{"file_path":"a.txt","old_string":"x","new_string":"y"}""");

        Assert.False(ambiguous.IsSuccess);
        Assert.Contains("appears 3 times", ambiguous.Content);

        var all = await new EditTool().CallAsync(services,
            """{"file_path":"a.txt","old_string":"x","new_string":"y","replace_all":true}""");

        Assert.True(all.IsSuccess);
        Assert.Equal("y\ny\ny", workspace.Read("a.txt"));
    }

    [Fact]
    public async Task Edit_detects_a_file_that_changed_on_disk_since_it_was_read()
    {
        using var workspace = new TempWorkspace();
        workspace.Write("a.txt", "original");

        var services = new TestServices(workspace.Root);
        await new ReadTool().CallAsync(services, """{"file_path":"a.txt"}""");

        workspace.Write("a.txt", "changed by someone else");

        var result = await new EditTool().CallAsync(services,
            """{"file_path":"a.txt","old_string":"changed","new_string":"nope"}""");

        Assert.False(result.IsSuccess);
        Assert.Contains("changed on disk", result.Content);
    }

    [Fact]
    public async Task MultiEdit_leaves_the_file_untouched_when_any_edit_fails()
    {
        using var workspace = new TempWorkspace();
        workspace.Write("a.txt", "one\ntwo");

        var services = new TestServices(workspace.Root);
        await new ReadTool().CallAsync(services, """{"file_path":"a.txt"}""");

        var result = await new MultiEditTool().CallAsync(services,
            """
            {"file_path":"a.txt","edits":[
              {"old_string":"one","new_string":"1"},
              {"old_string":"missing","new_string":"x"}
            ]}
            """);

        Assert.False(result.IsSuccess);
        Assert.Contains("No changes were written", result.Content);
        Assert.Equal("one\ntwo", workspace.Read("a.txt"));
    }

    [Fact]
    public async Task MultiEdit_applies_edits_in_order()
    {
        using var workspace = new TempWorkspace();
        workspace.Write("a.txt", "one\ntwo");

        var services = new TestServices(workspace.Root);
        await new ReadTool().CallAsync(services, """{"file_path":"a.txt"}""");

        var result = await new MultiEditTool().CallAsync(services,
            """
            {"file_path":"a.txt","edits":[
              {"old_string":"one","new_string":"1"},
              {"old_string":"two","new_string":"2"}
            ]}
            """);

        Assert.True(result.IsSuccess);
        Assert.Equal("1\n2", workspace.Read("a.txt"));
    }
}

public sealed class SearchToolTests
{
    [Fact]
    public async Task Glob_finds_files_and_skips_ignored_directories()
    {
        using var workspace = new TempWorkspace();
        workspace.Write("src/a.cs", "//");
        workspace.Write("src/deep/b.cs", "//");
        workspace.Write("node_modules/c.cs", "//");

        var result = await new GlobTool().CallAsync(new TestServices(workspace.Root), """{"pattern":"**/*.cs"}""");

        Assert.Contains("src/a.cs", result.Content);
        Assert.Contains("src/deep/b.cs", result.Content);
        Assert.DoesNotContain("node_modules", result.Content);
    }

    [Fact]
    public async Task Grep_returns_matching_lines_with_numbers()
    {
        using var workspace = new TempWorkspace();
        workspace.Write("a.cs", "class Foo\n{\n    void Bar() { }\n}");

        var result = await new GrepTool().CallAsync(new TestServices(workspace.Root), """{"pattern":"void \\w+"}""");

        Assert.True(result.IsSuccess);
        Assert.Contains("a.cs:3:", result.Content);
    }

    [Fact]
    public async Task Grep_supports_files_with_matches_mode()
    {
        using var workspace = new TempWorkspace();
        workspace.Write("a.cs", "needle");
        workspace.Write("b.cs", "haystack");

        var result = await new GrepTool().CallAsync(new TestServices(workspace.Root),
            """{"pattern":"needle","output_mode":"files_with_matches"}""");

        Assert.Contains("a.cs", result.Content);
        Assert.DoesNotContain("b.cs", result.Content);
    }

    [Fact]
    public async Task Grep_rejects_an_invalid_regex_with_a_usable_message()
    {
        using var workspace = new TempWorkspace();

        var result = await new GrepTool().CallAsync(new TestServices(workspace.Root), """{"pattern":"[unclosed"}""");

        Assert.False(result.IsSuccess);
        Assert.Contains("Invalid regular expression", result.Content);
    }

    [Fact]
    public async Task List_marks_ignored_directories_without_descending()
    {
        using var workspace = new TempWorkspace();
        workspace.Write("src/a.cs", "//");
        workspace.Write("node_modules/pkg/b.cs", "//");

        var result = await new ListTool().CallAsync(new TestServices(workspace.Root), """{"depth":3}""");

        Assert.Contains("(skipped)", result.Content);
        Assert.DoesNotContain("b.cs", result.Content);
    }
}

public sealed class BashToolTests
{
    [Fact]
    public async Task Runs_a_command_and_captures_output()
    {
        using var workspace = new TempWorkspace();
        var options = new AutoCodeOptions();

        var command = OperatingSystem.IsWindows() ? "Write-Output hello" : "echo hello";
        var result = await new BashTool(options).CallAsync(
            new TestServices(workspace.Root, options),
            JsonSerializer.Serialize(new { command }));

        Assert.True(result.IsSuccess);
        Assert.Contains("hello", result.Content);
    }

    [Fact]
    public async Task Reports_a_non_zero_exit_code_as_a_failure()
    {
        using var workspace = new TempWorkspace();
        var options = new AutoCodeOptions();

        var command = OperatingSystem.IsWindows() ? "exit 3" : "exit 3";
        var result = await new BashTool(options).CallAsync(
            new TestServices(workspace.Root, options),
            JsonSerializer.Serialize(new { command }));

        Assert.False(result.IsSuccess);
        Assert.Contains("Exit code 3", result.Content);
    }

    [Fact]
    public async Task Terminates_a_command_that_exceeds_its_timeout()
    {
        using var workspace = new TempWorkspace();
        var options = new AutoCodeOptions();

        var command = OperatingSystem.IsWindows() ? "Start-Sleep -Seconds 30" : "sleep 30";
        var result = await new BashTool(options).CallAsync(
            new TestServices(workspace.Root, options),
            JsonSerializer.Serialize(new { command, timeout_ms = 1_500 }));

        Assert.False(result.IsSuccess);
        Assert.Contains("timed out", result.Content);
    }
}

public sealed class WorkflowToolTests
{
    [Fact]
    public async Task TodoWrite_publishes_the_list_and_rejects_two_in_progress_items()
    {
        using var workspace = new TempWorkspace();
        var services = new TestServices(workspace.Root);

        var ok = await new TodoWriteTool().CallAsync(services,
            """{"todos":[{"content":"a","status":"completed"},{"content":"b","status":"in_progress"}]}""");

        Assert.True(ok.IsSuccess);
        Assert.Equal(2, services.Todos.Items.Count);
        Assert.Equal(TodoStatus.Completed, services.Todos.Items[0].Status);

        var invalid = await new TodoWriteTool().CallAsync(services,
            """{"todos":[{"content":"a","status":"in_progress"},{"content":"b","status":"in_progress"}]}""");

        Assert.False(invalid.IsSuccess);
    }

    [Fact]
    public void HtmlToText_strips_markup_and_drops_scripts()
    {
        const string html = "<html><head><style>body{}</style></head><body><h1>Title</h1><script>evil()</script><p>Body &amp; more</p></body></html>";

        var text = WebFetchTool.HtmlToText(html);

        Assert.Contains("Title", text);
        Assert.Contains("Body & more", text);
        Assert.DoesNotContain("evil()", text);
        Assert.DoesNotContain("body{}", text);
    }

    [Fact]
    public async Task Task_tool_reports_an_unknown_subagent_rather_than_failing_silently()
    {
        using var workspace = new TempWorkspace();
        var services = new TestServices(workspace.Root) { Subagents = new StubDispatcher() };

        var result = await new TaskTool().CallAsync(services,
            """{"subagent_type":"nope","prompt":"do something"}""");

        Assert.False(result.IsSuccess);
        Assert.Contains("Unknown subagent", result.Content);
    }

    private sealed class StubDispatcher : ISubagentDispatcher
    {
        public IReadOnlyCollection<string> AvailableAgents => ["explorer"];

        public Task<string> RunAsync(string agentName, string prompt, string? description, CancellationToken cancellationToken) =>
            Task.FromResult("report");
    }
}
