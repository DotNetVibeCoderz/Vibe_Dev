using System.Text.Json;
using DotCode.Abstractions;
using DotCode.Engine.Agent;
using DotCode.Engine.Util;

namespace DotCode.Tests;

public sealed class WorktreeTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "dc-wt-" + Guid.NewGuid().ToString("n")[..8]);

    public WorktreeTests()
    {
        Directory.CreateDirectory(_repo);
        Environment.SetEnvironmentVariable("DOTCODE_CONFIG_DIR", Path.Combine(_repo, ".cfg-home"));
        Git("init -q -b main");
        File.WriteAllText(Path.Combine(_repo, "README.md"), "# demo\n");
        File.WriteAllText(Path.Combine(_repo, ".gitignore"), ".cfg-home/\n");
        Git("add -A");
        Git("-c user.name=Test -c user.email=test@example.com commit -q -m init");
    }

    public void Dispose()
    {
        try
        {
            // git marks object files read-only on Windows.
            foreach (var f in Directory.EnumerateFiles(_repo, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_repo, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private string Git(string args)
    {
        var (code, output) = Worktrees.Git(_repo, args);
        Assert.True(code == 0, $"git {args}: {output}");
        return output;
    }

    [Fact]
    public void Creates_detects_changes_and_removes_worktrees()
    {
        var wt = Worktrees.Create(_repo, "feature-x");
        Assert.Equal(Path.Combine(_repo, ".dotcode", "worktrees", "feature-x"), wt.Path);
        Assert.Equal("dotcode/feature-x", wt.Branch);
        Assert.True(File.Exists(Path.Combine(wt.Path, "README.md")));
        Assert.False(Worktrees.HasChanges(wt));
        // The worktree folder does not show up in the main checkout's status.
        Assert.Equal("", Git("status --porcelain").Trim());
        Assert.Equal(_repo, Worktrees.RepoRoot(wt.Path), ignoreCase: true);

        File.WriteAllText(Path.Combine(wt.Path, "new.txt"), "x");
        Assert.Equal((true, 0), Worktrees.Changes(wt));
        Worktrees.Git(wt.Path, "add -A");
        Worktrees.Git(wt.Path, "-c user.name=Test -c user.email=test@example.com commit -q -m work");
        Assert.Equal((false, 1), Worktrees.Changes(wt));

        // Reusing by name keeps the checkout.
        var again = Worktrees.Create(_repo, "feature-x");
        Assert.True(again.Reused);
        Assert.Single(Worktrees.List(_repo), w => w.Name == "feature-x" && w.Branch == "dotcode/feature-x");

        Assert.True(Worktrees.Remove(_repo, "feature-x"));
        Assert.False(Directory.Exists(wt.Path));
        Assert.Empty(Worktrees.List(_repo));
        Assert.NotEqual(0, Worktrees.Git(_repo, "rev-parse --verify --quiet refs/heads/dotcode/feature-x").Code);
        Assert.Throws<InvalidOperationException>(() => Worktrees.Create(_repo, "../escape"));
    }

    private AgentRuntime Runtime(string script)
    {
        var scriptPath = Path.Combine(_repo, ".cfg-home", "script.json");
        Directory.CreateDirectory(Path.GetDirectoryName(scriptPath)!);
        File.WriteAllText(scriptPath, script);
        var settings = $$$"""{"providers":{"mock":{"type":"mock","script":{{{JsonSerializer.Serialize(scriptPath, TestJson.Default.String)}}}}},"autoCompact":false}""";
        return AgentRuntime.Create(new RuntimeOptions
        {
            Cwd = _repo, Model = "mock:scripted", SettingsJson = settings, NoMcp = true, PersistSession = false, DangerouslySkipPermissions = true,
        });
    }

    [Fact]
    public async Task Isolated_subagent_works_in_its_own_worktree()
    {
        // Main: Agent(isolation=worktree) → subagent: Write note.txt → subagent reports → main answers.
        await using var runtime = Runtime("""
            {"responses":[
              {"toolCalls":[{"name":"Agent","input":{"description":"write a note","prompt":"create note.txt","isolation":"worktree"}}]},
              {"toolCalls":[{"name":"Write","input":{"file_path":"note.txt","content":"from the worktree\n"}}]},
              {"text":"Wrote note.txt"},
              {"text":"The subagent finished."}
            ]}
            """);
        var session = runtime.CreateSession(persist: false);
        var result = await session.RunTurnAsync("delegate it");

        Assert.Equal("The subagent finished.", result.Text);
        Assert.False(File.Exists(Path.Combine(_repo, "note.txt")));        // main checkout untouched
        var kept = Assert.Single(Worktrees.List(_repo));
        Assert.Equal("from the worktree\n", File.ReadAllText(Path.Combine(kept.Path, "note.txt")));
        var toolResult = session.Messages.SelectMany(m => m.ToolResults).Single();
        Assert.Contains("Worktree kept:", toolResult.TextContent);
        Assert.Contains(kept.Branch!, toolResult.TextContent);
    }

    [Fact]
    public async Task Unchanged_subagent_worktree_is_removed()
    {
        await using var runtime = Runtime("""
            {"responses":[
              {"toolCalls":[{"name":"Agent","input":{"description":"look around","prompt":"read the readme","isolation":"worktree"}}]},
              {"text":"Nothing to change"},
              {"text":"ok"}
            ]}
            """);
        var session = runtime.CreateSession(persist: false);
        await session.RunTurnAsync("check");
        Assert.Empty(Worktrees.List(_repo));
        Assert.Contains("made no changes and was removed", session.Messages.SelectMany(m => m.ToolResults).Single().TextContent);
    }
}
