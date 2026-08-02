// Auto Code — Gravicode Studios (Kang Fadhil)

using AutoCode.Core.Agents;
using AutoCode.Core.Configuration;
using AutoCode.Core.Memory;
using AutoCode.Core.Sessions;
using AutoCode.Core.Skills;
using AutoCode.Core.Utilities;
using Microsoft.Extensions.AI;
using Xunit;

namespace AutoCode.Tests;

public sealed class ProviderProfileTests
{
    [Fact]
    public void An_env_reference_is_resolved_at_read_time()
    {
        Environment.SetEnvironmentVariable("AUTOCODE_TEST_KEY", "secret-value");

        try
        {
            var profile = new ProviderProfile { ApiKey = "env:AUTOCODE_TEST_KEY" };
            Assert.Equal("secret-value", profile.ResolveApiKey());
        }
        finally
        {
            Environment.SetEnvironmentVariable("AUTOCODE_TEST_KEY", null);
        }
    }

    [Fact]
    public void An_unset_env_reference_resolves_to_null_rather_than_the_literal()
    {
        var profile = new ProviderProfile { ApiKey = "env:AUTOCODE_DEFINITELY_NOT_SET" };
        Assert.Null(profile.ResolveApiKey());
    }

    [Fact]
    public void A_literal_key_passes_through()
    {
        var profile = new ProviderProfile { ApiKey = "sk-literal" };
        Assert.Equal("sk-literal", profile.ResolveApiKey());
    }

    [Fact]
    public void Presets_supply_defaults_that_user_values_override()
    {
        var user = new ProviderProfile { Model = "gpt-5-custom" };
        var merged = ProviderPresets.ApplyPreset("openai", user);

        Assert.Equal("gpt-5-custom", merged.Model);
        Assert.Equal("https://api.openai.com/v1", merged.Endpoint);
        Assert.Equal(ProviderKind.OpenAICompatible, merged.Kind);
    }

    [Fact]
    public void An_unknown_provider_name_is_kept_as_a_custom_profile()
    {
        var user = new ProviderProfile { Model = "m", Endpoint = "http://localhost:9999/v1" };
        var merged = ProviderPresets.ApplyPreset("my-gateway", user);

        Assert.Equal("my-gateway", merged.Name);
        Assert.Equal("http://localhost:9999/v1", merged.Endpoint);
    }

    [Fact]
    public void Anthropic_and_gemini_presets_declare_their_native_wire_formats()
    {
        Assert.Equal(ProviderKind.Anthropic, ProviderPresets.All["anthropic"].Kind);
        Assert.Equal(ProviderKind.Gemini, ProviderPresets.All["gemini"].Kind);
        Assert.Equal(ProviderKind.OpenAICompatible, ProviderPresets.All["deepseek"].Kind);
    }
}

public sealed class ConfigurationLoaderTests
{
    [Fact]
    public void Project_settings_override_defaults()
    {
        using var workspace = new TempWorkspace();

        workspace.Write(".autocode/settings.json",
            """
            {
              "activeProvider": "deepseek",
              "permissionMode": "AcceptEdits",
              "maxTurnIterations": 7,
              "providers": { "deepseek": { "model": "deepseek-reasoner", "apiKey": "literal-key" } }
            }
            """);

        var options = ConfigurationLoader.Load(workspace.Root);

        Assert.Equal("deepseek", options.ActiveProvider);
        Assert.Equal(Core.Permissions.PermissionMode.AcceptEdits, options.PermissionMode);
        Assert.Equal(7, options.MaxTurnIterations);

        var profile = options.ResolveActiveProfile();
        Assert.NotNull(profile);
        Assert.Equal("deepseek-reasoner", profile.Model);
        Assert.Equal("https://api.deepseek.com/v1", profile.Endpoint);
    }

    [Fact]
    public void Local_settings_win_over_committed_settings()
    {
        using var workspace = new TempWorkspace();

        workspace.Write(".autocode/settings.json",
            """{"providers":{"openai":{"model":"gpt-4.1","apiKey":"committed"}}}""");

        workspace.Write(".autocode/settings.local.json",
            """{"providers":{"openai":{"model":"gpt-4.1","apiKey":"personal"}}}""");

        var options = ConfigurationLoader.Load(workspace.Root);

        Assert.Equal("personal", options.Providers["openai"].ResolveApiKey());
    }

    [Fact]
    public void Workspace_root_discovery_walks_up_to_a_marker()
    {
        using var workspace = new TempWorkspace();
        workspace.Write(".autocode/settings.json", "{}");
        var nested = Path.Combine(workspace.Root, "src", "deep");
        Directory.CreateDirectory(nested);

        var discovered = ConfigurationLoader.DiscoverWorkspaceRoot(nested);

        Assert.Equal(Path.GetFullPath(workspace.Root), Path.GetFullPath(discovered));
    }
}

public sealed class FrontMatterTests
{
    [Fact]
    public void Fields_and_body_are_separated()
    {
        var (fields, body) = FrontMatter.Parse(
            """
            ---
            name: deploy
            description: Ship the service
            ---

            Run the deploy checklist.
            """);

        Assert.Equal("deploy", fields["name"]);
        Assert.Equal("Ship the service", fields["description"]);
        Assert.Equal("Run the deploy checklist.", body);
    }

    [Fact]
    public void List_values_are_read_in_both_spellings()
    {
        var (inline, _) = FrontMatter.Parse("---\ntools: [Read, Grep]\n---\nbody");
        var (block, _) = FrontMatter.Parse("---\ntools:\n  - Read\n  - Grep\n---\nbody");

        Assert.Equal(["Read", "Grep"], FrontMatter.AsList(inline, "tools"));
        Assert.Equal(["Read", "Grep"], FrontMatter.AsList(block, "tools"));
    }

    [Fact]
    public void A_document_without_front_matter_is_all_body()
    {
        var (fields, body) = FrontMatter.Parse("just the body");

        Assert.Empty(fields);
        Assert.Equal("just the body", body);
    }
}

public sealed class SkillAndAgentTests
{
    [Fact]
    public void A_skill_is_parsed_from_its_manifest()
    {
        var skill = SkillLoader.Parse(
            """
            ---
            name: release
            description: Cut a release
            allowed-tools: [Bash, Read]
            ---

            1. Run the tests.
            2. Tag the commit.
            """,
            @"C:\skills\release");

        Assert.NotNull(skill);
        Assert.Equal("release", skill.Name);
        Assert.Equal("Cut a release", skill.Description);
        Assert.Equal(["Bash", "Read"], skill.AllowedTools);
        Assert.Contains("Tag the commit", skill.Instructions);
    }

    [Fact]
    public async Task A_non_templated_skill_appends_its_arguments()
    {
        var skill = SkillLoader.Parse("---\nname: s\n---\nDo the thing.", "dir")!;

        var rendered = await skill.RenderAsync("with this input", CancellationToken.None);

        Assert.Contains("Do the thing.", rendered);
        Assert.Contains("with this input", rendered);
    }

    [Fact]
    public void An_agent_definition_is_parsed_from_its_markdown()
    {
        var agent = AgentDefinitionLoader.Parse(
            """
            ---
            name: auditor
            description: Audits dependencies
            tools: [Read, Bash]
            max-iterations: 12
            ---

            You audit dependencies for known vulnerabilities.
            """,
            "fallback");

        Assert.NotNull(agent);
        Assert.Equal("auditor", agent.Name);
        Assert.Equal(12, agent.MaxIterations);
        Assert.Equal(["Read", "Bash"], agent.Tools);
    }

    [Fact]
    public void Built_in_agents_are_available_when_a_workspace_defines_none()
    {
        using var workspace = new TempWorkspace();

        var agents = AgentDefinitionLoader.Discover(workspace.Root, new AutoCodeOptions());

        Assert.Contains("explorer", agents.Keys);
        Assert.Contains("reviewer", agents.Keys);
        Assert.Contains("tester", agents.Keys);
    }
}

public sealed class SessionStoreTests
{
    [Fact]
    public async Task A_transcript_with_tool_calls_round_trips()
    {
        using var workspace = new TempWorkspace();
        var store = new SessionStore(workspace.Root);

        var session = new Session
        {
            WorkspaceRoot = workspace.Root,
            ModelId = "test-model",
            Messages =
            [
                new ChatMessage(ChatRole.User, "read a.txt"),
                new ChatMessage(ChatRole.Assistant, [
                    new TextContent("Reading it now."),
                    new FunctionCallContent("c1", "Read", new Dictionary<string, object?> { ["file_path"] = "a.txt" })]),
                new ChatMessage(ChatRole.Tool, [new FunctionResultContent("c1", "contents")]),
            ],
        };

        await store.SaveAsync(session, CancellationToken.None);

        var loaded = await store.LoadAsync(session.Id, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(3, loaded.Messages.Count);

        var call = loaded.Messages[1].Contents.OfType<FunctionCallContent>().Single();
        Assert.Equal("Read", call.Name);

        var result = loaded.Messages[2].Contents.OfType<FunctionResultContent>().Single();
        Assert.Equal("c1", result.CallId);

        store.Delete(session.Id);
        Assert.Null(await store.LoadAsync(session.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Sessions_are_scoped_per_workspace()
    {
        using var a = new TempWorkspace();
        using var b = new TempWorkspace();

        var storeA = new SessionStore(a.Root);
        var storeB = new SessionStore(b.Root);

        await storeA.SaveAsync(new Session { WorkspaceRoot = a.Root, Messages = [new ChatMessage(ChatRole.User, "hi")] },
            CancellationToken.None);

        Assert.NotNull(await storeA.LoadLatestAsync(CancellationToken.None));
        Assert.Null(await storeB.LoadLatestAsync(CancellationToken.None));

        // Leave no residue in the user's real session directory.
        if (Directory.Exists(storeA.Directory)) Directory.Delete(storeA.Directory, recursive: true);
    }

    [Fact]
    public void A_title_is_derived_from_the_first_user_message()
    {
        var session = new Session
        {
            Messages = [new ChatMessage(ChatRole.User, "Fix the broken CI pipeline\nand explain why")],
        };

        Assert.Equal("Fix the broken CI pipeline and explain why", session.DeriveTitle());
    }
}

public sealed class SemanticIndexTests
{
    [Fact]
    public void Chunking_produces_overlapping_slices_with_line_numbers()
    {
        var content = string.Join('\n', Enumerable.Range(1, 200).Select(i => $"line {i} of source code here"));

        var chunks = SemanticCodeIndex.Chunk("src/a.cs", content).ToList();

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c => Assert.Equal("src/a.cs", c.RelativePath));
        Assert.Equal(1, chunks[0].StartLine);

        // Consecutive chunks must overlap, so a declaration split across the boundary is still findable.
        Assert.True(chunks[1].StartLine < chunks[0].EndLine);
    }

    [Fact]
    public void Trivially_short_content_produces_no_chunks() =>
        Assert.Empty(SemanticCodeIndex.Chunk("a.cs", "x"));

    [Fact]
    public async Task The_vector_collection_ranks_by_cosine_similarity()
    {
        using var collection = new InMemoryCodeChunkCollection("test");
        await collection.EnsureCollectionExistsAsync(CancellationToken.None);

        await collection.UpsertAsync(
        [
            new CodeChunk { Id = "near", Text = "near", RelativePath = "a.cs", Embedding = new float[] { 1f, 0f, 0f } },
            new CodeChunk { Id = "far", Text = "far", RelativePath = "b.cs", Embedding = new float[] { 0f, 1f, 0f } },
        ], CancellationToken.None);

        var hits = new List<string>();

        await foreach (var hit in collection.SearchAsync(
            new ReadOnlyMemory<float>([1f, 0f, 0f]), 2, cancellationToken: CancellationToken.None))
        {
            hits.Add(hit.Record.Id);
        }

        Assert.Equal(["near", "far"], hits);
    }
}
