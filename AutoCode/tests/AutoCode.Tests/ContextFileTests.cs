// Auto Code — Gravicode Studios (Kang Fadhil)

using AutoCode.Core.Configuration;
using AutoCode.Core.Context;
using Xunit;

namespace AutoCode.Tests;

public sealed class ContextFileLoaderTests
{
    [Fact]
    public void Files_are_gathered_from_the_root_downwards()
    {
        using var workspace = new TempWorkspace();
        workspace.Write("AUTOCODE.md", "root rules");
        workspace.Write("src/api/AUTOCODE.md", "api rules");

        var nested = Path.Combine(workspace.Root, "src", "api");

        var files = ContextFileLoader.Discover(workspace.Root, nested, new AutoCodeOptions());

        var project = files.Where(f => f.Scope == ContextScope.Project).ToList();

        Assert.Equal(2, project.Count);
        Assert.Contains("root rules", project[0].Content);
        Assert.Contains("api rules", project[1].Content);
    }

    [Fact]
    public void A_working_directory_outside_the_workspace_does_not_drag_in_foreign_files()
    {
        // This is what --cwd does: the process runs in one repository while the session is rooted in
        // another. Walking up from the process directory would collect the wrong project's rules and
        // never reach the workspace root at all.
        using var workspace = new TempWorkspace();
        using var elsewhere = new TempWorkspace();

        workspace.Write("AUTOCODE.md", "the workspace rules");
        elsewhere.Write("AUTOCODE.md", "a completely unrelated project");

        var files = ContextFileLoader.Discover(workspace.Root, elsewhere.Root, new AutoCodeOptions());

        var project = Assert.Single(files, f => f.Scope == ContextScope.Project);
        Assert.Contains("the workspace rules", project.Content);
        Assert.DoesNotContain(files, f => f.Content.Contains("unrelated"));
    }

    [Fact]
    public void Claude_md_is_read_so_an_existing_project_works_unchanged()
    {
        using var workspace = new TempWorkspace();
        workspace.Write("CLAUDE.md", "instructions written for Claude Code");

        var files = ContextFileLoader.Discover(workspace.Root, workspace.Root, new AutoCodeOptions());

        Assert.Contains(files, f => f.DisplayPath.EndsWith("CLAUDE.md", StringComparison.Ordinal));
    }

    [Fact]
    public void Explicitly_configured_files_are_included()
    {
        using var workspace = new TempWorkspace();
        workspace.Write("docs/conventions.md", "our conventions");

        var options = new AutoCodeOptions();
        options.ContextFiles.Add("docs/conventions.md");

        var files = ContextFileLoader.Discover(workspace.Root, workspace.Root, options);

        Assert.Contains(files, f => f.Scope == ContextScope.Explicit && f.Content.Contains("our conventions"));
    }

    [Fact]
    public void The_rendered_block_names_every_file_it_includes()
    {
        using var workspace = new TempWorkspace();
        workspace.Write("AUTOCODE.md", "root rules");

        var files = ContextFileLoader.Discover(workspace.Root, workspace.Root, new AutoCodeOptions());
        var rendered = ContextFileLoader.Render(files);

        Assert.Contains("AUTOCODE.md", rendered);
        Assert.Contains("root rules", rendered);
    }

    [Fact]
    public void No_files_renders_to_nothing_rather_than_an_empty_heading()
    {
        Assert.Equal("", ContextFileLoader.Render([]));
    }
}
