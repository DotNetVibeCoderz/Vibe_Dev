using System.Text.Json;
using DotCode.Abstractions;
using DotCode.Engine;
using DotCode.Engine.Agent;
using DotCode.Engine.Configuration;
using DotCode.Engine.Extensibility;
using DotCode.Engine.Observability;
using DotCode.Engine.Permissions;
using DotCode.Engine.Util;
using DotCode.Providers;
using DotCode.Providers.Http;

namespace DotCode.Tests;

public sealed class PermissionTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "dc-perm");

    [Theory]
    [InlineData("Bash(npm run test:*)", "Bash", "npm run test:*")]
    [InlineData("Read", "Read", null)]
    [InlineData("WebFetch(domain:example.com)", "WebFetch", "domain:example.com")]
    [InlineData("mcp__github", "mcp__github", null)]
    [InlineData("Bash(*)", "Bash", null)]
    public void Parses_rules(string rule, string tool, string? spec)
    {
        var r = PermissionRule.Parse(rule)!;
        Assert.Equal(tool, r.Tool);
        Assert.Equal(spec, r.Specifier);
    }

    [Fact]
    public void Mcp_server_rule_matches_all_server_tools()
    {
        var r = PermissionRule.Parse("mcp__github")!;
        Assert.True(r.MatchesTool("mcp__github__create_issue"));
        Assert.False(r.MatchesTool("mcp__gitlab__x"));
    }

    [Theory]
    [InlineData("ls -la", true)]
    [InlineData("git status && git log --oneline", true)]
    [InlineData("cat a.txt > b.txt", false)]
    [InlineData("rm -rf /", false)]
    [InlineData("echo $(rm x)", false)]
    [InlineData("git status 2>&1", true)]
    [InlineData("find . -name x -delete", false)]
    public void Detects_read_only_shell_commands(string cmd, bool readOnly) =>
        Assert.Equal(readOnly, ShellCommand.IsReadOnly(cmd));

    [Fact]
    public void Splits_compound_commands()
    {
        var a = ShellCommand.Analyze("cd src && npm test; echo 'a && b' | grep a");
        Assert.Equal(["cd src", "npm test", "echo 'a && b'", "grep a"], a.Subcommands);
    }

    [Fact]
    public void Wildcards_and_path_patterns()
    {
        Assert.True(Wildcard.IsMatch("git commit -m x", "git *"));
        Assert.False(Wildcard.IsMatch("npm install", "git *"));
        var cwd = Path.Combine(Root, "proj");
        Assert.True(PathPattern.IsMatch(Path.Combine(cwd, "src", "a", "b.cs"), "/src/**", cwd, cwd));
        Assert.True(PathPattern.IsMatch(Path.Combine(cwd, "x", ".env"), ".env", cwd, cwd));
        Assert.False(PathPattern.IsMatch(Path.Combine(cwd, "docs", "a.md"), "/src/**", cwd, cwd));
        Assert.True(PathPattern.IsMatch(Path.Combine(cwd, "src", "a.cs"), "src/*.cs", cwd, cwd));
    }

    [Fact]
    public void Suggests_prefix_rules()
    {
        Assert.Equal("npm test", ShellCommand.SuggestPrefix("npm test -- --watch"));
        Assert.Equal("git commit", ShellCommand.SuggestPrefix("git commit -m x"));
        Assert.Equal("ls", ShellCommand.SuggestPrefix("ls -la"));
    }

    [Fact]
    public void Splits_cli_rule_lists_respecting_parentheses()
    {
        Assert.Equal(["Bash(git log *)", "Edit", "Read"], PermissionEngine.SplitRuleList("Bash(git log *) Edit,Read").ToList());
    }
}

public sealed class UtilTests
{
    [Fact]
    public void Unified_diff_counts_changes_with_line_numbers()
    {
        var diff = UnifiedDiff.Create("a\nb\nc\nd\n", "a\nB\nc\nd\ne\n", "f.txt");
        Assert.Contains("-b", diff);
        Assert.Contains("+B", diff);
        Assert.Contains("+e", diff);
        Assert.Equal((2, 1), UnifiedDiff.Count(diff));
        Assert.Contains("@@ -1,4 +1,5 @@", diff);
    }

    [Fact]
    public void Diff_of_identical_text_is_empty() => Assert.Equal("", UnifiedDiff.Create("x\ny", "x\ny", "f"));

    [Fact]
    public void Large_rewrite_diff_is_bounded()
    {
        var a = string.Join('\n', Enumerable.Range(0, 5000).Select(i => "a" + i));
        var b = string.Join('\n', Enumerable.Range(0, 5000).Select(i => "b" + i));
        var diff = UnifiedDiff.Create(a, b, "big");
        Assert.Equal((5000, 5000), UnifiedDiff.Count(diff));
    }

    [Theory]
    [InlineData("{\"a\": \"b", "b")]
    [InlineData("```json\n{\"a\":\"b\"}\n```", "b")]
    [InlineData("{\"a\":\"b\",", "b")]
    public void Json_repair_fixes_truncated_arguments(string input, string expected) =>
        Assert.Equal(expected, JsonRepair.TryRepair(input).GetString("a"));

    [Fact]
    public void Expands_env_references()
    {
        Environment.SetEnvironmentVariable("DC_TEST_VAR", "xyz");
        Assert.Equal("key-xyz", ConfigValue.Expand("key-${env:DC_TEST_VAR}"));
        Assert.Equal("fallback", ConfigValue.Expand("${env:DC_NOT_SET:-fallback}"));
    }

    [Fact]
    public void Frontmatter_parses_scalars_lists_and_blocks()
    {
        var fm = Frontmatter.Parse("---\nname: pdf\ndescription: >\n  Work with\n  PDF files\ntools: [Read, Write]\nallowed-tools:\n  - Bash(python *)\n---\n# Body\ntext");
        Assert.Equal("pdf", fm.Get("name"));
        Assert.Equal("Work with PDF files", fm.Get("description"));
        Assert.Equal(["Read", "Write"], fm.GetList("tools"));
        Assert.Equal(["Bash(python *)"], fm.GetList("allowed-tools"));
        Assert.StartsWith("# Body", fm.Body);
    }

    [Fact]
    public void Command_arguments_are_expanded()
    {
        Assert.Equal("Fix issue 42 in api", ExtensionRegistry.ExpandArguments("Fix issue $1 in $2", "42 api"));
        Assert.Equal("Review: all of it", ExtensionRegistry.ExpandArguments("Review: $ARGUMENTS", "all of it"));
    }

    [Fact]
    public void Schema_sanitizer_produces_openapi_subset()
    {
        var schema = DotCodeJson.Parse("""
            {"$schema":"x","type":"object","$defs":{"P":{"type":"object","properties":{"n":{"type":"integer"}}}},
             "properties":{"p":{"$ref":"#/$defs/P"},"k":{"const":"a"},"t":{"type":["string","null"]}},"additionalProperties":false}
            """);
        var s = SchemaSanitizer.Sanitize(schema, JsonSchemaProfile.OpenApiSubset);
        Assert.False(s.TryGetProperty("$schema", out _));
        Assert.False(s.TryGetProperty("additionalProperties", out _));
        Assert.Equal("integer", s.GetProperty("properties").GetProperty("p").GetProperty("properties").GetProperty("n").GetString("type"));
        Assert.Equal("a", s.GetProperty("properties").GetProperty("k").GetProperty("enum")[0].GetString());
        Assert.True(s.GetProperty("properties").GetProperty("t").GetProperty("nullable").GetBoolean());

        var strict = SchemaSanitizer.Sanitize(DotCodeJson.Parse("""{"type":"object","properties":{"a":{"type":"string"},"b":{"type":"integer"}},"required":["a"]}"""), JsonSchemaProfile.Strict);
        Assert.Equal(2, strict.GetProperty("required").GetArrayLength());
        Assert.Equal("null", strict.GetProperty("properties").GetProperty("b").GetProperty("type")[1].GetString());
    }

    [Fact]
    public void Settings_merge_concatenates_permission_lists()
    {
        var a = System.Text.Json.Nodes.JsonNode.Parse("""{"permissions":{"allow":["Read"]},"theme":"dark"}""")!.AsObject();
        var b = System.Text.Json.Nodes.JsonNode.Parse("""{"permissions":{"allow":["Edit","Read"],"deny":["Bash(rm *)"]},"theme":"light"}""")!.AsObject();
        SettingsLoader.Merge(a, b);
        Assert.Equal(2, a["permissions"]!["allow"]!.AsArray().Count);
        Assert.Equal("light", a["theme"]!.GetValue<string>());
        Assert.Single(a["permissions"]!["deny"]!.AsArray());
    }

    [Fact]
    public void Model_catalog_knows_major_families()
    {
        Assert.Equal(ReasoningSupport.Budget, ModelCatalog.Lookup("claude-sonnet-4-5").Reasoning);
        Assert.Equal(CachingSupport.Implicit, ModelCatalog.Lookup("gpt-5-mini").Caching);
        Assert.Equal(JsonSchemaProfile.OpenApiSubset, ModelCatalog.Lookup("gemini-2.5-pro").SchemaProfile);
        Assert.Equal(0m, ModelCatalog.Lookup("qwen3-coder", new ProviderConfig { Type = "ollama" }).InputPricePerMTok);
    }
}

/// <summary>End-to-end agent loop with the deterministic scripted provider (no network).</summary>
public sealed class AgentLoopTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dc-loop-" + Guid.NewGuid().ToString("n")[..8]);
    private readonly string _config;

    public AgentLoopTests()
    {
        Directory.CreateDirectory(_dir);
        _config = Path.Combine(_dir, ".cfg");
        Environment.SetEnvironmentVariable("DOTCODE_CONFIG_DIR", _config);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private AgentRuntime Runtime(string script, string? mode = null, bool bypass = false, string extraSettings = "")
    {
        var scriptPath = Path.Combine(_dir, "script.json");
        File.WriteAllText(scriptPath, script);
        var settings = $$$"""{"providers":{"mock":{"type":"mock","script":{{{JsonSerializer.Serialize(scriptPath, TestJson.Default.String)}}}}},"autoCompact":false{{{extraSettings}}}}""";
        return AgentRuntime.Create(new RuntimeOptions
        {
            Cwd = _dir, Model = "mock:scripted", SettingsJson = settings, NoMcp = true, PersistSession = false,
            PermissionMode = mode, DangerouslySkipPermissions = bypass,
        });
    }

    [Fact]
    public async Task Runs_tools_until_the_model_stops()
    {
        var target = Path.Combine(_dir, "hello.txt").Replace("\\", "\\\\");
        await using var runtime = Runtime($$$"""
            {"responses":[
              {"text":"Creating the file.","toolCalls":[{"name":"Write","input":{"file_path":"{{{target}}}","content":"hi there\n"}}]},
              {"text":"Now reading it back.","toolCalls":[{"name":"Read","input":{"file_path":"{{{target}}}"}}]},
              {"text":"All done: the file says hi."}
            ]}
            """, bypass: true);
        var session = runtime.CreateSession(persist: false);
        var events = new List<AgentEvent>();
        session.Sink = new DelegateEventSink(events.Add);
        var result = await session.RunTurnAsync("make a file");

        Assert.False(result.IsError);
        Assert.Equal("All done: the file says hi.", result.Text);
        Assert.Equal(3, result.ModelCalls);
        Assert.Equal("hi there\n", File.ReadAllText(Path.Combine(_dir, "hello.txt")));
        var completed = events.OfType<ToolCompletedEvent>().ToList();
        Assert.Equal(["Write", "Read"], completed.Select(c => c.Name));
        Assert.NotNull(completed[0].Diff);
        Assert.Single(events.OfType<TurnCompletedEvent>());
    }

    [Fact]
    public async Task Non_interactive_denial_is_reported_to_the_model_and_files_stay_untouched()
    {
        var target = Path.Combine(_dir, "nope.txt").Replace("\\", "\\\\");
        await using var runtime = Runtime($$$"""{"responses":[{"toolCalls":[{"name":"Write","input":{"file_path":"{{{target}}}","content":"x"}}]},{"text":"should not run"}]}""");
        var session = runtime.CreateSession(persist: false);
        var result = await session.RunTurnAsync("write it");
        Assert.False(File.Exists(Path.Combine(_dir, "nope.txt")));
        // Headless denials carry guidance, so the model gets a chance to adapt (interactive "No" ends the turn instead).
        Assert.Equal(2, result.ModelCalls);
        var toolResult = session.Messages.SelectMany(m => m.ToolResults).Single();
        Assert.True(toolResult.IsError);
        Assert.Contains("--allowedTools", toolResult.TextContent);
    }

    [Fact]
    public async Task Plan_mode_blocks_mutations()
    {
        var target = Path.Combine(_dir, "plan.txt").Replace("\\", "\\\\");
        await using var runtime = Runtime($$$"""{"responses":[{"toolCalls":[{"name":"Write","input":{"file_path":"{{{target}}}","content":"x"}}]},{"text":"ok, planning"}]}""", mode: "plan");
        var session = runtime.CreateSession(persist: false);
        var result = await session.RunTurnAsync("do it");
        Assert.False(File.Exists(Path.Combine(_dir, "plan.txt")));
        Assert.Contains("Plan mode", session.Messages.SelectMany(m => m.ToolResults).Single().TextContent);
        Assert.Equal("ok, planning", result.Text);
    }

    [Fact]
    public async Task Edit_requires_prior_read_and_preserves_crlf()
    {
        var file = Path.Combine(_dir, "code.cs");
        File.WriteAllText(file, "class A\r\n{\r\n    int x = 1;\r\n}\r\n");
        var path = file.Replace("\\", "\\\\");
        await using var runtime = Runtime($$$"""
            {"responses":[
              {"toolCalls":[{"name":"Edit","input":{"file_path":"{{{path}}}","old_string":"int x = 1;","new_string":"int x = 2;"}}]},
              {"toolCalls":[{"name":"Read","input":{"file_path":"{{{path}}}"}}]},
              {"toolCalls":[{"name":"Edit","input":{"file_path":"{{{path}}}","old_string":"{\n    int x = 1;","new_string":"{\n    int x = 2;\n    int y = 3;"}}]},
              {"text":"edited"}
            ]}
            """, mode: "acceptEdits");
        var session = runtime.CreateSession(persist: false);
        await session.RunTurnAsync("edit");
        var results = session.Messages.SelectMany(m => m.ToolResults).ToList();
        Assert.True(results[0].IsError);
        Assert.Contains("has not been read", results[0].TextContent);
        Assert.False(results[2].IsError);
        Assert.Equal("class A\r\n{\r\n    int x = 2;\r\n    int y = 3;\r\n}\r\n", File.ReadAllText(file));
    }

    [Fact]
    public async Task Subagent_runs_in_its_own_context()
    {
        await using var runtime = Runtime("""
            {"responses":[
              {"toolCalls":[{"name":"Agent","input":{"description":"look around","prompt":"List files","subagent_type":"Explore"}}]},
              {"text":"Subagent report: nothing here."},
              {"text":"The subagent found nothing."}
            ]}
            """, bypass: true);
        var session = runtime.CreateSession(persist: false);
        var events = new List<AgentEvent>();
        session.Sink = new DelegateEventSink(events.Add);
        var result = await session.RunTurnAsync("explore");
        Assert.Equal("The subagent found nothing.", result.Text);
        Assert.Single(events.OfType<SubagentStartedEvent>());
        Assert.Contains(events, e => e.ParentToolUseId is not null);
        var agentResult = session.Messages.SelectMany(m => m.ToolResults).Single();
        Assert.Contains("nothing here", agentResult.TextContent);
    }

    [Fact]
    public async Task Todo_updates_are_tracked()
    {
        await using var runtime = Runtime("""
            {"responses":[
              {"toolCalls":[{"name":"TodoWrite","input":{"todos":[{"content":"A","status":"in_progress","activeForm":"Doing A"},{"content":"B","status":"pending","activeForm":"Doing B"}]}}]},
              {"text":"planned"}
            ]}
            """);
        var session = runtime.CreateSession(persist: false);
        await session.RunTurnAsync("plan");
        Assert.Equal(2, session.Todos.Count);
        Assert.Equal(TodoStatus.InProgress, session.Todos[0].Status);
    }

    [Fact]
    public async Task Custom_commands_and_skills_expand()
    {
        Directory.CreateDirectory(Path.Combine(_dir, ".dotcode", "commands"));
        File.WriteAllText(Path.Combine(_dir, ".dotcode", "commands", "greet.md"), "---\ndescription: Greets\n---\nSay hello to $ARGUMENTS");
        Directory.CreateDirectory(Path.Combine(_dir, ".dotcode", "skills", "haiku"));
        File.WriteAllText(Path.Combine(_dir, ".dotcode", "skills", "haiku", "SKILL.md"), "---\nname: haiku\ndescription: Write haiku\n---\nAlways answer in 5-7-5.");
        await using var runtime = Runtime("""{"responses":[]}""");
        var session = runtime.CreateSession(persist: false);
        var cmd = await CommandExpander.ExpandAsync(session, "/greet Budi", CancellationToken.None);
        Assert.True(cmd.IsCommand);
        Assert.Contains("Say hello to Budi", cmd.Prompt);
        var skill = await CommandExpander.ExpandAsync(session, "/haiku", CancellationToken.None);
        Assert.Contains("5-7-5", skill.Prompt);
        Assert.Contains(session.GetTools(), t => t.Name == "Skill");
    }

    [Fact]
    public async Task Auto_mode_runs_actions_the_classifier_allows()
    {
        // Script order: main call (tool) -> classifier verdict -> main call (final answer).
        await using var runtime = Runtime("""
            {"responses":[
              {"toolCalls":[{"name":"Bash","input":{"command":"printf auto-ok"}}]},
              {"text":"{\"decision\":\"allow\",\"reason\":\"harmless print requested by the user\"}"},
              {"text":"printed"}
            ]}
            """, mode: "auto");
        var session = runtime.CreateSession(persist: false);
        var result = await session.RunTurnAsync("print something");
        var toolResult = session.Messages.SelectMany(m => m.ToolResults).Single();
        Assert.False(toolResult.IsError, toolResult.TextContent);
        Assert.Contains("auto-ok", toolResult.TextContent);
        Assert.Equal("printed", result.Text);
    }

    [Fact]
    public async Task Auto_mode_blocks_actions_the_classifier_denies()
    {
        var target = Path.Combine(_dir, "denied.txt").Replace("\\", "/");
        await using var runtime = Runtime($$$"""
            {"responses":[
              {"toolCalls":[{"name":"Bash","input":{"command":"printf x > {{{target}}}"}}]},
              {"text":"{\"decision\":\"deny\",\"reason\":\"not requested\"}"},
              {"text":"ok, I won't"}
            ]}
            """, mode: "auto");
        var session = runtime.CreateSession(persist: false);
        var result = await session.RunTurnAsync("hello");
        var toolResult = session.Messages.SelectMany(m => m.ToolResults).Single();
        Assert.True(toolResult.IsError);
        Assert.Contains("Auto mode blocked this action: not requested", toolResult.TextContent);
        Assert.False(File.Exists(Path.Combine(_dir, "denied.txt")));
        Assert.Equal("ok, I won't", result.Text);
    }

    [Fact]
    public async Task Auto_mode_falls_back_to_asking_when_the_classifier_is_unclear()
    {
        await using var runtime = Runtime("""
            {"responses":[
              {"toolCalls":[{"name":"Bash","input":{"command":"printf maybe"}}]},
              {"text":"I am not sure"},
              {"text":"done"}
            ]}
            """, mode: "auto");
        var session = runtime.CreateSession(persist: false);
        await session.RunTurnAsync("do it");
        // Headless session: "ask" becomes a non-interactive denial with guidance.
        var toolResult = session.Messages.SelectMany(m => m.ToolResults).Single();
        Assert.True(toolResult.IsError);
        Assert.Contains("not granted", toolResult.TextContent);
    }

    [Fact]
    public void Auto_mode_verdict_parsing_is_fail_safe()
    {
        Assert.Equal(AutoDecision.Allow, AutoModeClassifier.Parse("```json\n{\"decision\": \"allow\", \"reason\": \"ok\"}\n```").Decision);
        Assert.Equal(AutoDecision.Deny, AutoModeClassifier.Parse("{\"decision\":\"DENY\",\"reason\":\"x\"}").Decision);
        Assert.Equal(AutoDecision.Ask, AutoModeClassifier.Parse("allow").Decision);
        Assert.Equal(AutoDecision.Ask, AutoModeClassifier.Parse("{\"decision\":\"maybe\"}").Decision);
        Assert.Equal(PermissionMode.Auto, PermissionModes.Parse("auto"));
        Assert.Equal(PermissionMode.Auto, PermissionMode.AcceptEdits.Next(includeBypass: false, includeAuto: true));
        Assert.Equal(PermissionMode.Plan, PermissionMode.AcceptEdits.Next(includeBypass: false));
    }

    [Fact]
    public async Task Audit_log_records_tool_calls_and_detects_tampering()
    {
        var target = Path.Combine(_dir, "a.txt").Replace("\\", "\\\\");
        var log = Path.Combine(_dir, "audit.jsonl");
        var logJson = JsonSerializer.Serialize(log, TestJson.Default.String);
        await using (var runtime = Runtime($$$"""
            {"responses":[
              {"toolCalls":[{"name":"Write","input":{"file_path":"{{{target}}}","content":"api_key=supersecretvalue123"}}]},
              {"toolCalls":[{"name":"Read","input":{"file_path":"{{{target}}}"}}]},
              {"text":"done"}
            ]}
            """, bypass: true, extraSettings: $$$""","audit":{"enabled":true,"path":{{{logJson}}},"includePrompts":true}"""))
        {
            await runtime.CreateSession(persist: false).RunTurnAsync("write a file");
        }

        var lines = File.ReadAllLines(log);
        Assert.Equal(["user_prompt", "tool_call", "tool_call", "turn_end"], lines.Select(l => DotCodeJson.Parse(l).GetString("event")));
        Assert.Equal("Write", DotCodeJson.Parse(lines[1]).GetString("tool"));
        Assert.Equal("mode", DotCodeJson.Parse(lines[1]).GetString("decision"));
        Assert.DoesNotContain("supersecretvalue123", File.ReadAllText(log));
        Assert.True(AuditLog.Verify(log).Ok);

        File.WriteAllLines(log, [lines[0], lines[1].Replace("\"Write\"", "\"Read\""), .. lines[2..]]);
        var edited = AuditLog.Verify(log);
        Assert.False(edited.Ok);
        Assert.Equal(2, edited.BrokenAtLine);

        File.WriteAllLines(log, [lines[0], .. lines[2..]]);
        Assert.Equal(2, AuditLog.Verify(log).BrokenAtLine);
    }

    [Fact]
    public void Audit_redaction_masks_common_secrets()
    {
        var text = AuditLog.Redact("curl -H 'Authorization: Bearer abcdef123456' sk-ant-abcdefghijklmnop ghp_abcdefghijklmnopqrstuvwxyz password=hunter22");
        Assert.DoesNotContain("abcdefghijklmnop", text);
        Assert.DoesNotContain("hunter22", text);
        Assert.DoesNotContain("ghp_abcdefghijklmnopqrstuvwxyz", text);
        Assert.Contains("curl", text);
    }

    [Fact]
    public async Task Otlp_exporter_sends_turn_model_and_tool_spans_and_metrics()
    {
        var handler = new CapturingHandler();
        ProviderHttp.OverrideHandler = handler;
        try
        {
            OtlpExporter.Start(new OtelConfig("http://collector.test:4318", new Dictionary<string, string> { ["x-api-key"] = "k" }, "dotcode-test", false));
            await using (var runtime = Runtime("""
                {"responses":[{"toolCalls":[{"name":"Glob","input":{"pattern":"*.none"}}]},{"text":"ok"}]}
                """, bypass: true))
            {
                await runtime.CreateSession(persist: false).RunTurnAsync("look");
            }
            await OtlpExporter.StopAsync();
        }
        finally { ProviderHttp.OverrideHandler = null; }

        var traces = string.Concat(handler.Requests.Where(r => r.Path == "/v1/traces").Select(r => r.Body));
        var metrics = string.Concat(handler.Requests.Where(r => r.Path == "/v1/metrics").Select(r => r.Body));
        Assert.Contains("\"dotcode.turn\"", traces);
        Assert.Contains("\"chat scripted\"", traces);
        Assert.Contains("\"execute_tool Glob\"", traces);
        Assert.Contains("dotcode-test", traces);
        Assert.Contains("\"dotcode.tool.calls\"", metrics);
        Assert.Contains("\"dotcode.turns\"", metrics);
        Assert.All(handler.Requests, r => Assert.Equal("k", r.ApiKey));
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<(string Path, string Body, string? ApiKey)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            lock (Requests) Requests.Add((request.RequestUri!.AbsolutePath, body, request.Headers.TryGetValues("x-api-key", out var v) ? v.First() : null));
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task Rewind_restores_files_and_conversation()
    {
        var target = Path.Combine(_dir, "r.txt");
        File.WriteAllText(target, "original");
        var path = target.Replace("\\", "\\\\");
        await using var runtime = Runtime($$$"""
            {"responses":[
              {"toolCalls":[{"name":"Read","input":{"file_path":"{{{path}}}"}}]},
              {"toolCalls":[{"name":"Write","input":{"file_path":"{{{path}}}","content":"changed"}}]},
              {"text":"changed it"}
            ]}
            """, bypass: true);
        var session = runtime.CreateSession(persist: false);
        await session.RunTurnAsync("change the file");
        Assert.Equal("changed", File.ReadAllText(target));
        var turn = session.UserTurns().Single();
        var (removed, restored) = session.Rewind(turn.Id, conversation: true, code: true);
        Assert.Equal("original", File.ReadAllText(target));
        Assert.Single(restored);
        Assert.True(removed > 0);
        Assert.Empty(session.Messages);
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(string))]
internal sealed partial class TestJson : System.Text.Json.Serialization.JsonSerializerContext;
