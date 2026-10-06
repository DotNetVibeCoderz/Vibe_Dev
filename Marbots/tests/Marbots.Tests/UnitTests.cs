using System.Text;
using System.Text.Json;
using Marbots.Abstractions;
using Marbots.Kernel;
using Marbots.Providers;
using Marbots.Runtime;
using Marbots.Storage;

namespace Marbots.Tests;

public class PolicyEngineTests
{
    private static SecurityContext Ctx(string profile) => new(new BotDefinition { Id = "b", PermissionProfile = profile }, "t", "task");

    [Theory]
    [InlineData("read-only", PermissionCategory.ReadOnly, PolicyDecisionKind.Allow)]
    [InlineData("read-only", PermissionCategory.WorkspaceWrite, PolicyDecisionKind.Deny)]
    [InlineData("developer-safe", PermissionCategory.WorkspaceWrite, PolicyDecisionKind.Allow)]
    [InlineData("developer-safe", PermissionCategory.ProcessExecution, PolicyDecisionKind.Ask)]
    [InlineData("workspace-write", PermissionCategory.ProcessExecution, PolicyDecisionKind.Deny)]
    [InlineData("autonomous", PermissionCategory.ProcessExecution, PolicyDecisionKind.Allow)]
    [InlineData("autonomous", PermissionCategory.ExternalCommunication, PolicyDecisionKind.Ask)]
    [InlineData("manager", PermissionCategory.AgentControl, PolicyDecisionKind.Allow)]
    public void Profiles_map_categories_to_decisions(string profile, PermissionCategory category, PolicyDecisionKind expected)
    {
        var engine = new PolicyEngine();
        Assert.Equal(expected, engine.Evaluate(new ActionRequest("x", category, RiskLevel.Low, "{}"), Ctx(profile)).Kind);
    }

    [Fact]
    public void Critical_risk_always_asks_even_when_allowed()
    {
        var engine = new PolicyEngine();
        var d = engine.Evaluate(new ActionRequest("x", PermissionCategory.ReadOnly, RiskLevel.Critical, "{}"), Ctx("autonomous"));
        Assert.Equal(PolicyDecisionKind.Ask, d.Kind);
    }

    [Fact]
    public void Session_grant_turns_ask_into_allow_for_that_thread_only()
    {
        var engine = new PolicyEngine();
        engine.GrantForSession("t", "b", "run_shell");
        var req = new ActionRequest("run_shell", PermissionCategory.ProcessExecution, RiskLevel.High, "{}");
        Assert.Equal(PolicyDecisionKind.Allow, engine.Evaluate(req, Ctx("developer-safe")).Kind);
        var otherThread = new SecurityContext(new BotDefinition { Id = "b", PermissionProfile = "developer-safe" }, "other", "task");
        Assert.Equal(PolicyDecisionKind.Ask, engine.Evaluate(req, otherThread).Kind);
    }

    [Fact]
    public void Skip_approvals_turns_ask_into_allow_but_keeps_deny()
    {
        var engine = new PolicyEngine { SkipApprovals = true };
        var shell = new ActionRequest("run_shell", PermissionCategory.ProcessExecution, RiskLevel.High, "{}");
        Assert.Equal(PolicyDecisionKind.Allow, engine.Evaluate(shell, Ctx("developer-safe")).Kind);
        Assert.Equal(PolicyDecisionKind.Allow, engine.Evaluate(new ActionRequest("x", PermissionCategory.ReadOnly, RiskLevel.Critical, "{}"), Ctx("autonomous")).Kind);
        Assert.Equal(PolicyDecisionKind.Deny, engine.Evaluate(shell, Ctx("read-only")).Kind);
        Assert.Equal(PolicyDecisionKind.Deny, engine.Evaluate(shell, Ctx("workspace-write")).Kind);
        engine.SkipApprovals = false;
        Assert.Equal(PolicyDecisionKind.Ask, engine.Evaluate(shell, Ctx("developer-safe")).Kind);
    }

    [Fact]
    public void Deny_cannot_be_overridden_by_session_grant()
    {
        var engine = new PolicyEngine();
        engine.GrantForSession("t", "b", "write_file");
        var d = engine.Evaluate(new ActionRequest("write_file", PermissionCategory.WorkspaceWrite, RiskLevel.Low, "{}"), Ctx("read-only"));
        Assert.Equal(PolicyDecisionKind.Deny, d.Kind);
    }
}

public class CronTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    [Fact]
    public void Monday_at_eight()
    {
        var cron = CronExpression.Parse("0 8 * * 1");
        var next = cron.Next(new DateTimeOffset(2026, 10, 6, 9, 0, 0, TimeSpan.Zero), Utc); // Tuesday
        Assert.Equal(new DateTimeOffset(2026, 10, 12, 8, 0, 0, TimeSpan.Zero), next);
    }

    [Fact]
    public void Steps_and_ranges()
    {
        var cron = CronExpression.Parse("*/15 9-17 * * 1-5");
        var next = cron.Next(new DateTimeOffset(2026, 10, 6, 9, 7, 0, TimeSpan.Zero), Utc);
        Assert.Equal(new DateTimeOffset(2026, 10, 6, 9, 15, 0, TimeSpan.Zero), next);
        var evening = cron.Next(new DateTimeOffset(2026, 10, 6, 17, 50, 0, TimeSpan.Zero), Utc);
        Assert.Equal(new DateTimeOffset(2026, 10, 7, 9, 0, 0, TimeSpan.Zero), evening);
    }

    [Fact]
    public void Day_of_month_and_weekday_are_ored_when_both_restricted()
    {
        var cron = CronExpression.Parse("0 0 1 * 0"); // 1st of month OR Sunday
        var next = cron.Next(new DateTimeOffset(2026, 10, 6, 0, 0, 0, TimeSpan.Zero), Utc);
        Assert.Equal(new DateTimeOffset(2026, 10, 11, 0, 0, 0, TimeSpan.Zero), next); // Sunday 11 Oct
    }

    [Theory]
    [InlineData("* * *")]
    [InlineData("61 * * * *")]
    [InlineData("a b c d e")]
    [InlineData("*/0 * * * *")]
    public void Invalid_expressions_are_rejected(string expr) => Assert.False(CronExpression.TryParse(expr, out _));

    [Fact]
    public void Respects_time_zone()
    {
        var jakarta = TimeZoneInfo.CreateCustomTimeZone("WIB", TimeSpan.FromHours(7), "WIB", "WIB");
        var next = CronExpression.Parse("0 8 * * *").Next(new DateTimeOffset(2026, 10, 6, 0, 0, 0, TimeSpan.Zero), jakarta);
        Assert.Equal(new DateTimeOffset(2026, 10, 6, 1, 0, 0, TimeSpan.Zero), next!.Value.ToUniversalTime());
    }
}

public class FrontMatterTests
{
    [Fact]
    public void Parses_scalars_lists_and_nesting()
    {
        var (f, body) = FrontMatter.Parse("""
            ---
            name: dotnet-api-review
            version: 1.2.0
            description: "Review .NET APIs"
            requires:
              tools: [read, grep]
            permissions:
              network: false
              shell: true
            tags:
              - api
              - review
            ---
            # Body
            text
            """);
        Assert.Equal("dotnet-api-review", f["name"]);
        Assert.Equal("Review .NET APIs", f["description"]);
        Assert.Equal("read,grep", f["requires.tools"]);
        Assert.Equal("true", f["permissions.shell"]);
        Assert.Equal("api,review", f["tags"]);
        Assert.StartsWith("# Body", body);
    }

    [Fact]
    public void Text_without_front_matter_is_returned_as_body()
    {
        var (f, body) = FrontMatter.Parse("# Just markdown");
        Assert.Empty(f);
        Assert.Equal("# Just markdown", body);
    }

    [Fact]
    public void Builtin_skills_all_have_names_and_descriptions()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "skills");
        var files = Directory.GetFiles(root, "SKILL.md", SearchOption.AllDirectories);
        Assert.True(files.Length >= 20);
        foreach (var file in files)
        {
            var info = SkillRegistry.Read(file, "x", "x", false);
            Assert.False(string.IsNullOrWhiteSpace(info.Name), file);
            Assert.False(string.IsNullOrWhiteSpace(info.Description), file);
        }
    }
}

public class WorkspacePathTests
{
    [Theory]
    [InlineData("../secret.txt")]
    [InlineData("..\\..\\windows")]
    [InlineData("sub/../../x")]
    public void Traversal_is_blocked(string path)
    {
        var root = Path.Combine(Path.GetTempPath(), "mb-ws");
        Assert.Throws<UnauthorizedAccessException>(() => WorkspacePaths.Resolve(root, path));
    }

    [Fact]
    public void Absolute_paths_outside_are_blocked() =>
        Assert.Throws<UnauthorizedAccessException>(() => WorkspacePaths.Resolve(Path.Combine(Path.GetTempPath(), "mb-ws"), Path.GetTempPath()));

    [Fact]
    public void Relative_paths_resolve_inside()
    {
        var root = Path.Combine(Path.GetTempPath(), "mb-ws");
        Assert.Equal(Path.Combine(Path.GetFullPath(root), "a", "b.txt"), WorkspacePaths.Resolve(root, "a/b.txt"));
    }

    [Fact]
    public void Glob_matches_recursive_patterns()
    {
        Assert.Matches(ListFilesFunction.GlobToRegex("**/*.cs"), "src/app/Program.cs");
        Assert.Matches(ListFilesFunction.GlobToRegex("*.md"), "docs/readme.md");
        Assert.DoesNotMatch(ListFilesFunction.GlobToRegex("src/*.cs"), "src/a/b.cs");
    }
}

public class ContextTests
{
    [Fact]
    public void Orphan_and_interrupted_tool_calls_are_repaired()
    {
        var msgs = new List<ChatMessage>
        {
            new() { Role = "user", Content = "hi", Seq = 1 },
            new() { Role = "assistant", Content = "", Seq = 2, ToolCalls = [new ToolCall { Id = "c1", Name = "read_file" }, new ToolCall { Id = "c2", Name = "grep" }] },
            new() { Role = "tool", ToolCallId = "c1", Content = "ok", Seq = 3 },
            new() { Role = "tool", ToolCallId = "zzz", Content = "orphan", Seq = 4 },
            new() { Role = "user", Content = "next", Seq = 5 },
        };
        var model = ContextManager.ToModelMessages(msgs);
        Assert.Equal(["user", "assistant", "tool", "tool", "user"], model.Select(m => m.Role).ToArray());
        Assert.Equal("c2", model[3].ToolCallId);
        Assert.Contains("interrupted", model[3].Content);
    }

    [Fact]
    public void Older_tool_outputs_are_truncated_but_latest_kept()
    {
        var big = new string('x', 20_000);
        var msgs = new List<ChatMessage>
        {
            new() { Role = "user", Content = "a", Seq = 1 },
            new() { Role = "assistant", Seq = 2, ToolCalls = [new ToolCall { Id = "c1", Name = "read_file" }] },
            new() { Role = "tool", ToolCallId = "c1", Content = big, Seq = 3 },
            new() { Role = "user", Content = "b", Seq = 4 },
            new() { Role = "assistant", Seq = 5, ToolCalls = [new ToolCall { Id = "c2", Name = "read_file" }] },
            new() { Role = "tool", ToolCallId = "c2", Content = big, Seq = 6 },
        };
        var model = ContextManager.ToModelMessages(msgs);
        Assert.True(model[2].Content!.Length < 7_000);
        Assert.Equal(20_000, model[5].Content!.Length);
    }

    [Fact]
    public void System_prompt_includes_persona_skills_memory_and_roster()
    {
        var bot = new BotDefinition { Name = "Atlas", Role = "Researcher", Persona = "Be rigorous." };
        var prompt = ContextManager.BuildSystemPrompt(bot,
            [new SkillInfo { Name = "market-research", Description = "Research markets" }],
            [new MemoryMatch(new MemoryRecord { Content = "User prefers Bahasa Indonesia", Source = "user" }, 1)],
            "Earlier we agreed on X.",
            [new BotDefinition { Id = "alice", Name = "Alice", Role = "Engineer", KernelFunctions = ["files"] }],
            [], delegated: true);
        Assert.Contains("Be rigorous.", prompt);
        Assert.Contains("market-research", prompt);
        Assert.Contains("User prefers Bahasa Indonesia", prompt);
        Assert.Contains("alice", prompt);
        Assert.Contains("Earlier we agreed on X.", prompt);
        Assert.Contains("untrusted", prompt);
        Assert.Contains("Gravicode", prompt);
    }
}

public class ProviderTests
{
    [Fact]
    public void Builds_openai_compatible_request_body()
    {
        var body = OpenAiCompatibleProvider.BuildBody(new ModelRequest
        {
            Model = "gpt-5-mini",
            Messages = [ModelMessage.System("s"), ModelMessage.User("u"), ModelMessage.Assistant(null, [new ToolCall { Id = "c", Name = "f", Arguments = "{\"a\":1}" }]), ModelMessage.Tool("c", "r")],
            Tools = [new ToolSchema("f", "desc", """{"type":"object","properties":{}}""")],
            MaxOutputTokens = 100,
        });
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        Assert.Equal("gpt-5-mini", root.GetProperty("model").GetString());
        Assert.Equal(4, root.GetProperty("messages").GetArrayLength());
        Assert.Equal("c", root.GetProperty("messages")[2].GetProperty("tool_calls")[0].GetProperty("id").GetString());
        Assert.Equal("c", root.GetProperty("messages")[3].GetProperty("tool_call_id").GetString());
        Assert.Equal("f", root.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString());
        Assert.Equal(100, root.GetProperty("max_completion_tokens").GetInt32());
    }

    [Fact]
    public void Parses_tool_calls_and_usage()
    {
        using var doc = JsonDocument.Parse("""
            {"model":"m","usage":{"prompt_tokens":12,"completion_tokens":7},
             "choices":[{"finish_reason":"tool_calls","message":{"content":null,
               "tool_calls":[{"id":"call_1","type":"function","function":{"name":"read_file","arguments":"{\"path\":\"a\"}"}}]}}]}
            """);
        var r = OpenAiCompatibleProvider.Parse(doc.RootElement);
        Assert.Equal("tool_calls", r.FinishReason);
        Assert.Single(r.ToolCalls);
        Assert.Equal("read_file", r.ToolCalls[0].Name);
        Assert.Equal(12, r.Usage.InputTokens);
        Assert.Equal(7, r.Usage.OutputTokens);
    }

    [Fact]
    public async Task Reads_streamed_text_tool_calls_and_usage()
    {
        var sse = string.Join("\n", [
            "data: {\"model\":\"m\",\"choices\":[{\"delta\":{\"content\":\"Hel\"}}]}",
            "",
            "data: {\"choices\":[{\"delta\":{\"content\":\"lo\"}}]}",
            "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"c1\",\"function\":{\"name\":\"read_\",\"arguments\":\"{\\\"pa\"}}]}}]}",
            "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"name\":\"file\",\"arguments\":\"th\\\":\\\"a\\\"}\"}}]}}]}",
            "data: {\"choices\":[{\"finish_reason\":\"tool_calls\",\"delta\":{}}]}",
            "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":11,\"completion_tokens\":4}}",
            "data: [DONE]",
        ]);
        var pieces = new List<string>();
        var r = await OpenAiCompatibleProvider.ReadStreamAsync(new MemoryStream(Encoding.UTF8.GetBytes(sse)), pieces.Add, default);
        Assert.Equal(["Hel", "lo"], pieces);
        Assert.Equal("Hello", r.Content);
        Assert.Equal("tool_calls", r.FinishReason);
        var call = Assert.Single(r.ToolCalls);
        Assert.Equal(("c1", "read_file", "{\"path\":\"a\"}"), (call.Id, call.Name, call.Arguments));
        Assert.Equal(11, r.Usage.InputTokens);
        Assert.Equal(4, r.Usage.OutputTokens);
    }

    [Fact]
    public void Streaming_requests_ask_for_stream_and_usage()
    {
        var body = OpenAiCompatibleProvider.BuildBody(new ModelRequest { Model = "m", Messages = [ModelMessage.User("hi")], OnTextDelta = _ => { } });
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.GetProperty("stream").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
    }

    [Theory]
    [InlineData("azure-openai", "https://x.openai.azure.com/", "https://x.openai.azure.com/openai/v1/chat/completions")]
    [InlineData("openai", "https://api.deepseek.com", "https://api.deepseek.com/chat/completions")]
    [InlineData("openai", "https://api.openai.com", "https://api.openai.com/v1/chat/completions")]
    [InlineData("openai", "http://localhost:11434/v1", "http://localhost:11434/v1/chat/completions")]
    public void Builds_chat_uri(string kind, string endpoint, string expected) =>
        Assert.Equal(expected, OpenAiCompatibleProvider.BuildChatUri(new ProviderConfig { Kind = kind, Endpoint = endpoint }).ToString());
}

public class StorageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mb-test-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly SqliteDatabase _db;

    public StorageTests() => _db = new SqliteDatabase(Path.Combine(_dir, "t.db"));

    [Fact]
    public async Task Memory_search_ranks_and_filters_by_owner()
    {
        var store = new SqliteMemoryStore(_db);
        await store.WriteAsync(new MemoryRecord { Owner = "atlas", Content = "The client prefers reports in Bahasa Indonesia" });
        await store.WriteAsync(new MemoryRecord { Owner = "atlas", Content = "Quarterly revenue target is 2 billion rupiah" });
        await store.WriteAsync(new MemoryRecord { Owner = "alice", Content = "Reports must use Bahasa Indonesia headings" });
        var hits = await store.SearchAsync(new MemoryQuery("what language for reports?", ["atlas"]));
        Assert.NotEmpty(hits);
        Assert.All(hits, h => Assert.Equal("atlas", h.Record.Owner));
        Assert.Contains("Bahasa", hits[0].Record.Content);
    }

    [Fact]
    public async Task Fts_query_is_injection_safe() =>
        Assert.Empty(await new SqliteMemoryStore(_db).SearchAsync(new MemoryQuery("\" OR 1=1 -- NEAR(", ["x"])));

    [Fact]
    public async Task Messages_get_sequential_numbers_per_thread()
    {
        var store = new SqliteMessageStore(_db);
        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => store.AppendAsync(new ChatMessage { ThreadId = "t1", Content = i.ToString() })));
        var list = await store.ListAsync("t1");
        Assert.Equal(Enumerable.Range(1, 20).Select(i => (long)i), list.Select(m => m.Seq));
    }

    [Fact]
    public async Task Events_replay_after_id()
    {
        var store = new SqliteEventStore(_db);
        var first = await store.AppendAsync(new AgentEvent { Type = "A", ThreadId = "t" });
        await store.AppendAsync(new AgentEvent { Type = "B", ThreadId = "t" });
        await store.AppendAsync(new AgentEvent { Type = "C", ThreadId = "other" });
        var after = await store.ListAsync("t", null, first, 10);
        Assert.Single(after);
        Assert.Equal("B", after[0].Type);
        Assert.True(after[0].Id > first);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }
}

public class AutoLearnTests
{
    [Fact]
    public void Near_duplicates_are_detected()
    {
        Assert.True(AutoLearnService.Similarity("User prefers reports in Bahasa Indonesia", "The user prefers reports in Bahasa Indonesia.") >= 0.6);
        Assert.True(AutoLearnService.Similarity("User prefers reports in Bahasa Indonesia", "Quarterly revenue target is 2 billion") < 0.2);
    }

    [Fact]
    public void Extracts_json_from_chatty_output() =>
        Assert.Equal("{\"memories\":[]}", AutoLearnService.ExtractJson("Sure! Here it is: {\"memories\":[]} hope it helps"));
}
