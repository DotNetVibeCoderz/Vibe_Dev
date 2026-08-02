// Auto Code — Gravicode Studios (Kang Fadhil)

using AutoCode.Core.Configuration;
using AutoCode.Core.Memory;
using AutoCode.Providers;
using Microsoft.Extensions.AI;
using Xunit;

namespace AutoCode.Tests;

public sealed class EmbeddingOptionsTests
{
    private static ProviderProfile OpenAI() => new()
    {
        Name = "openai",
        Kind = ProviderKind.OpenAICompatible,
        Endpoint = "https://api.openai.com/v1",
        ApiKey = "sk-test",
        Model = "gpt-4.1",
        EmbeddingModel = "text-embedding-3-small",
    };

    private static ProviderProfile Anthropic() => new()
    {
        Name = "anthropic",
        Kind = ProviderKind.Anthropic,
        ApiKey = "sk-ant",
        Model = "claude-sonnet-4-5",
    };

    [Fact]
    public void Auto_follows_an_openai_compatible_chat_provider()
    {
        var resolved = new EmbeddingOptions().Resolve(OpenAI());

        Assert.Equal(EmbeddingProviderKind.OpenAICompatible, resolved.Kind);
        Assert.Equal("text-embedding-3-small", resolved.Model);
        Assert.Equal("https://api.openai.com/v1", resolved.Endpoint);
        Assert.Equal("sk-test", resolved.ResolveApiKey());
    }

    [Fact]
    public void Auto_resolves_to_none_for_a_provider_with_no_embedding_endpoint()
    {
        // This is the gap the separate configuration exists to close: an Anthropic session used to
        // have no semantic index at all, with nothing explaining why.
        var resolved = new EmbeddingOptions().Resolve(Anthropic());

        Assert.Equal(EmbeddingProviderKind.None, resolved.Kind);
    }

    [Fact]
    public void Auto_resolves_to_none_when_the_chat_provider_names_no_embedding_model()
    {
        var profile = OpenAI();
        profile.EmbeddingModel = null;

        Assert.Equal(EmbeddingProviderKind.None, new EmbeddingOptions().Resolve(profile).Kind);
    }

    [Fact]
    public void Ollama_can_back_the_index_while_anthropic_backs_the_chat()
    {
        var resolved = new EmbeddingOptions { Kind = EmbeddingProviderKind.Ollama }.Resolve(Anthropic());

        Assert.Equal(EmbeddingProviderKind.Ollama, resolved.Kind);
        Assert.Equal("http://localhost:11434", resolved.Endpoint);
        Assert.Equal("nomic-embed-text", resolved.Model);
    }

    [Fact]
    public void An_explicit_backend_keeps_its_own_endpoint_and_model()
    {
        var resolved = new EmbeddingOptions
        {
            Kind = EmbeddingProviderKind.Ollama,
            Endpoint = "http://gpu-box:11434",
            Model = "mxbai-embed-large",
        }.Resolve(OpenAI());

        Assert.Equal("http://gpu-box:11434", resolved.Endpoint);
        Assert.Equal("mxbai-embed-large", resolved.Model);
    }

    [Fact]
    public void Dimensions_default_to_zero_so_they_can_be_inferred()
    {
        Assert.Equal(0, new EmbeddingOptions().Resolve(OpenAI()).Dimensions);
    }

    [Fact]
    public void A_profile_dimension_is_carried_across_when_it_was_set_explicitly()
    {
        var profile = OpenAI();
        profile.EmbeddingDimensions = 768;

        Assert.Equal(768, new EmbeddingOptions().Resolve(profile).Dimensions);
    }

    [Fact]
    public void Describe_names_the_backend_for_status_output()
    {
        var ollama = new EmbeddingOptions { Kind = EmbeddingProviderKind.Ollama }.Resolve(Anthropic());
        Assert.Contains("Ollama", ollama.Describe());
        Assert.Contains("nomic-embed-text", ollama.Describe());

        Assert.Equal("disabled", new EmbeddingOptions().Resolve(Anthropic()).Describe());
    }

    [Fact]
    public void Settings_bind_the_embeddings_section()
    {
        using var workspace = new TempWorkspace();

        workspace.Write(".autocode/settings.json",
            """
            {
              "enableSemanticIndex": true,
              "embeddings": {
                "kind": "Onnx",
                "modelPath": "models/minilm.onnx",
                "vocabPath": "models/vocab.txt",
                "maxTokens": 256,
                "lowerCase": false
              }
            }
            """);

        var options = ConfigurationLoader.Load(workspace.Root);

        Assert.True(options.EnableSemanticIndex);
        Assert.Equal(EmbeddingProviderKind.Onnx, options.Embeddings.Kind);
        Assert.Equal("models/minilm.onnx", options.Embeddings.ModelPath);
        Assert.Equal(256, options.Embeddings.MaxTokens);
        Assert.False(options.Embeddings.LowerCase);
    }
}

public sealed class EmbeddingFactoryTests
{
    [Fact]
    public void An_ollama_backend_is_created_without_a_key()
    {
        var generator = EmbeddingFactory.Create(new EmbeddingOptions
        {
            Kind = EmbeddingProviderKind.Ollama,
            Endpoint = "http://localhost:11434",
            Model = "nomic-embed-text",
        });

        Assert.NotNull(generator);
        generator.Dispose();
    }

    [Fact]
    public void An_ollama_endpoint_written_with_the_openai_suffix_still_works()
    {
        // Users copy http://localhost:11434/v1 across from the chat provider; the native API is
        // rooted one level up, so the suffix has to be trimmed rather than rejected.
        var generator = EmbeddingFactory.Create(new EmbeddingOptions
        {
            Kind = EmbeddingProviderKind.Ollama,
            Endpoint = "http://localhost:11434/v1",
            Model = "nomic-embed-text",
        });

        Assert.NotNull(generator);

        var metadata = generator.GetService(typeof(EmbeddingGeneratorMetadata)) as EmbeddingGeneratorMetadata;
        Assert.DoesNotContain("/v1", metadata?.ProviderUri?.ToString() ?? "");

        generator.Dispose();
    }

    [Fact]
    public void An_openai_compatible_backend_without_a_model_is_rejected_with_a_usable_message()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            EmbeddingFactory.Create(new EmbeddingOptions { Kind = EmbeddingProviderKind.OpenAICompatible }));

        Assert.Contains("needs a model", ex.Message);
    }

    [Fact]
    public void None_produces_no_generator()
    {
        Assert.Null(EmbeddingFactory.Create(new EmbeddingOptions { Kind = EmbeddingProviderKind.None }));
    }
}

public sealed class SemanticIndexDimensionTests
{
    [Fact]
    public async Task The_width_is_learned_from_the_first_batch()
    {
        using var workspace = new TempWorkspace();
        workspace.Write("a.cs", string.Join('\n', Enumerable.Range(1, 40).Select(i => $"// meaningful line {i} of source")));

        using var index = new SemanticCodeIndex(workspace.Root, new StubEmbeddingGenerator(384));

        await index.BuildAsync(null, CancellationToken.None);

        Assert.True(index.IsReady);
        Assert.Equal(384, index.Dimensions);
        Assert.True(index.ChunkCount > 0);
    }

    [Fact]
    public async Task A_configured_width_that_the_model_contradicts_fails_loudly()
    {
        using var workspace = new TempWorkspace();
        workspace.Write("a.cs", string.Join('\n', Enumerable.Range(1, 40).Select(i => $"// meaningful line {i} of source")));

        using var index = new SemanticCodeIndex(workspace.Root, new StubEmbeddingGenerator(384), expectedDimensions: 1536);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => index.BuildAsync(null, CancellationToken.None));

        Assert.Contains("384", ex.Message);
        Assert.Contains("1536", ex.Message);
    }

    [Fact]
    public async Task Search_returns_the_nearest_chunk()
    {
        using var workspace = new TempWorkspace();
        workspace.Write("a.cs", string.Join('\n', Enumerable.Range(1, 40).Select(i => $"// meaningful line {i} of source")));

        using var index = new SemanticCodeIndex(workspace.Root, new StubEmbeddingGenerator(8));

        await index.BuildAsync(null, CancellationToken.None);

        var hits = await index.SearchAsync("anything", 3, CancellationToken.None);

        Assert.NotEmpty(hits);
        Assert.Equal("a.cs", hits[0].RelativePath);
        Assert.True(hits[0].StartLine >= 1);
    }

    /// <summary>Deterministic embeddings of a fixed width, derived from the text's hash.</summary>
    private sealed class StubEmbeddingGenerator(int width) : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var result = new GeneratedEmbeddings<Embedding<float>>();

            foreach (var value in values)
            {
                var vector = new float[width];
                var seed = value.GetHashCode(StringComparison.Ordinal);

                for (var i = 0; i < width; i++)
                    vector[i] = ((seed >> (i % 24)) & 0xFF) / 255f;

                result.Add(new Embedding<float>(vector));
            }

            return Task.FromResult(result);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
