// Auto Code — Gravicode Studios (Kang Fadhil)

using AutoCode.Core.Abstractions;
using AutoCode.Core.Configuration;
using AutoCode.Core.Permissions;
using AutoCode.Tools;
using Xunit;

namespace AutoCode.Tests;

public sealed class PermissionRuleTests
{
    [Theory]
    [InlineData("Read", "Read", "any", true)]
    [InlineData("Read", "Write", "any", false)]
    [InlineData("Bash(git status)", "Bash", "git status", true)]
    [InlineData("Bash(git status)", "Bash", "git push", false)]
    [InlineData("Bash(npm run *)", "Bash", "npm run build", true)]
    [InlineData("Bash(git diff:*)", "Bash", "git diff --stat HEAD", true)]
    [InlineData("Bash(git diff:*)", "Bash", "git log", false)]
    [InlineData("Write(src/**)", "Write", "src/a/b.cs", true)]
    [InlineData("Write(src/**)", "Write", "tests/a.cs", false)]
    [InlineData("mcp__github__*", "mcp__github__create_issue", "", true)]
    public void Rules_match_tool_and_subject(string rule, string tool, string subject, bool expected) =>
        Assert.Equal(expected, PermissionEngine.Matches(rule, tool, subject));
}

public sealed class PermissionEngineTests
{
    private static PermissionEngine Engine(PermissionMode mode, Action<PermissionRules>? configure = null)
    {
        var options = new AutoCodeOptions { PermissionMode = mode };
        configure?.Invoke(options.Permissions);
        return new PermissionEngine(options);
    }

    [Fact]
    public void Read_only_tools_never_require_approval()
    {
        var engine = Engine(PermissionMode.Ask);
        Assert.Equal(PermissionCheckKind.Allow, engine.Evaluate(new ReadTool(), "any.cs").Kind);
    }

    [Fact]
    public void Ask_mode_prompts_before_writing()
    {
        var engine = Engine(PermissionMode.Ask);
        Assert.Equal(PermissionCheckKind.Ask, engine.Evaluate(new WriteTool(), "src/a.cs").Kind);
    }

    [Fact]
    public void AcceptEdits_allows_writes_but_still_prompts_for_shell()
    {
        var engine = Engine(PermissionMode.AcceptEdits);

        Assert.Equal(PermissionCheckKind.Allow, engine.Evaluate(new WriteTool(), "src/a.cs").Kind);
        Assert.Equal(PermissionCheckKind.Ask, engine.Evaluate(new BashTool(new AutoCodeOptions()), "ls").Kind);
    }

    [Fact]
    public void Plan_mode_refuses_every_mutation()
    {
        var engine = Engine(PermissionMode.Plan);

        Assert.Equal(PermissionCheckKind.Deny, engine.Evaluate(new WriteTool(), "src/a.cs").Kind);
        Assert.Equal(PermissionCheckKind.Deny, engine.Evaluate(new BashTool(new AutoCodeOptions()), "ls").Kind);
        Assert.Equal(PermissionCheckKind.Allow, engine.Evaluate(new ReadTool(), "src/a.cs").Kind);
    }

    [Fact]
    public void Bypass_mode_allows_everything()
    {
        var engine = Engine(PermissionMode.BypassPermissions);
        Assert.Equal(PermissionCheckKind.Allow, engine.Evaluate(new BashTool(new AutoCodeOptions()), "rm -rf /").Kind);
    }

    [Fact]
    public void Deny_rules_beat_bypass_mode()
    {
        var engine = Engine(PermissionMode.BypassPermissions, rules => rules.Deny.Add("Bash(rm -rf:*)"));

        var check = engine.Evaluate(new BashTool(new AutoCodeOptions()), "rm -rf /");

        Assert.Equal(PermissionCheckKind.Deny, check.Kind);
        Assert.Contains("deny rule", check.Reason);
    }

    [Fact]
    public void Deny_rules_beat_allow_rules()
    {
        var engine = Engine(PermissionMode.Ask, rules =>
        {
            rules.Allow.Add("Bash");
            rules.Deny.Add("Bash(rm:*)");
        });

        Assert.Equal(PermissionCheckKind.Allow, engine.Evaluate(new BashTool(new AutoCodeOptions()), "ls").Kind);
        Assert.Equal(PermissionCheckKind.Deny, engine.Evaluate(new BashTool(new AutoCodeOptions()), "rm x").Kind);
    }

    [Fact]
    public void Allow_always_persists_a_rule_for_the_session()
    {
        var engine = Engine(PermissionMode.Ask);
        var tool = new BashTool(new AutoCodeOptions());

        Assert.Equal(PermissionCheckKind.Ask, engine.Evaluate(tool, "npm run build").Kind);

        engine.AddRule(PermissionOutcome.AllowAlways, PermissionEngine.SuggestRule("Bash", "npm run build", ToolCapability.ExecutesCommands));

        Assert.Equal(PermissionCheckKind.Allow, engine.Evaluate(tool, "npm run build").Kind);
        Assert.Equal(PermissionCheckKind.Allow, engine.Evaluate(tool, "npm run test").Kind);
    }

    [Fact]
    public void Disabled_tools_are_refused_outright()
    {
        var options = new AutoCodeOptions { PermissionMode = PermissionMode.BypassPermissions };
        options.DisabledTools.Add("Bash");

        var check = new PermissionEngine(options).Evaluate(new BashTool(options), "ls");

        Assert.Equal(PermissionCheckKind.Deny, check.Kind);
        Assert.Contains("disabled", check.Reason);
    }

    [Fact]
    public void Suggested_rule_generalises_a_command_but_pins_a_path()
    {
        Assert.Equal("Bash(npm run:*)", PermissionEngine.SuggestRule("Bash", "npm run build", ToolCapability.ExecutesCommands));
        Assert.Equal("Write(src/a.cs)", PermissionEngine.SuggestRule("Write", "src/a.cs", ToolCapability.WritesFiles));
    }
}
