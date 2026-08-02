// Auto Code — Gravicode Studios (Kang Fadhil)

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AutoCode.Core.Configuration;
using AutoCode.Providers;
using Microsoft.Extensions.AI;
using Xunit;

namespace AutoCode.Tests;

/// <summary>
/// A throwaway HTTP server that answers one request with a canned SSE stream and records
/// what was sent — enough to exercise the hand-written provider clients for real.
/// </summary>
public sealed class FakeEndpoint : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Task _serving;

    public FakeEndpoint(string sseBody)
    {
        Port = GetFreePort();
        _listener.Prefixes.Add($"http://localhost:{Port}/");
        _listener.Start();

        _serving = Task.Run(async () =>
        {
            try
            {
                var context = await _listener.GetContextAsync().ConfigureAwait(false);

                RequestPath = context.Request.Url?.PathAndQuery ?? "";
                RequestHeaders = context.Request.Headers.AllKeys
                    .Where(k => k is not null)
                    .ToDictionary(k => k!, k => context.Request.Headers[k] ?? "", StringComparer.OrdinalIgnoreCase);

                using (var reader = new StreamReader(context.Request.InputStream))
                    RequestBody = await reader.ReadToEndAsync().ConfigureAwait(false);

                context.Response.StatusCode = 200;
                context.Response.ContentType = "text/event-stream";

                var bytes = Encoding.UTF8.GetBytes(sseBody);
                await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
                context.Response.Close();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
            {
                // The listener was torn down before a request arrived; that is a normal end.
            }
        });
    }

    public int Port { get; }
    public string RequestBody { get; private set; } = "";
    public string RequestPath { get; private set; } = "";
    public Dictionary<string, string> RequestHeaders { get; private set; } = [];

    public JsonObject ParsedBody => JsonNode.Parse(RequestBody) as JsonObject ?? [];

    public async Task WaitForRequestAsync() =>
        await _serving.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);

    private static int GetFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public void Dispose()
    {
        try { _listener.Stop(); } catch (ObjectDisposedException) { }
        _listener.Close();
    }
}

public sealed class AnthropicClientTests
{
    private const string Stream =
        """
        event: message_start
        data: {"type":"message_start","message":{"id":"msg_1","usage":{"input_tokens":42}}}

        event: content_block_start
        data: {"type":"content_block_start","index":0,"content_block":{"type":"text"}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hello "}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"world"}}

        event: content_block_stop
        data: {"type":"content_block_stop","index":0}

        event: content_block_start
        data: {"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"toolu_1","name":"Read"}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{\"file_path\""}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":":\"a.txt\"}"}}

        event: content_block_stop
        data: {"type":"content_block_stop","index":1}

        event: message_delta
        data: {"type":"message_delta","delta":{"stop_reason":"tool_use"},"usage":{"output_tokens":17}}

        event: message_stop
        data: {"type":"message_stop"}


        """;

    [Fact]
    public async Task Text_tool_calls_and_usage_are_all_decoded_from_the_stream()
    {
        using var endpoint = new FakeEndpoint(Stream);

        var profile = new ProviderProfile
        {
            Name = "anthropic-test",
            Kind = ProviderKind.Anthropic,
            Endpoint = $"http://localhost:{endpoint.Port}/v1",
            ApiKey = "test-key",
            Model = "claude-sonnet-4-5",
        };

        using var client = new AnthropicChatClient(profile);

        var response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "read a.txt")],
            new ChatOptions { Tools = [new StubFunction()] },
            CancellationToken.None);

        await endpoint.WaitForRequestAsync();

        Assert.Equal("Hello world", response.Text);

        var call = response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Single();
        Assert.Equal("Read", call.Name);
        Assert.Equal("toolu_1", call.CallId);
        Assert.Equal("a.txt", call.Arguments?["file_path"]?.ToString());

        Assert.Equal(42, response.Usage?.InputTokenCount);
        Assert.Equal(17, response.Usage?.OutputTokenCount);
    }

    [Fact]
    public async Task The_request_uses_the_messages_endpoint_with_anthropic_headers()
    {
        using var endpoint = new FakeEndpoint(Stream);

        var profile = new ProviderProfile
        {
            Name = "anthropic-test",
            Kind = ProviderKind.Anthropic,
            Endpoint = $"http://localhost:{endpoint.Port}/v1",
            ApiKey = "test-key",
            Model = "claude-sonnet-4-5",
        };

        using var client = new AnthropicChatClient(profile);

        await client.GetResponseAsync(
            [
                new ChatMessage(ChatRole.System, "You are terse."),
                new ChatMessage(ChatRole.User, "hi"),
            ],
            null, CancellationToken.None);

        await endpoint.WaitForRequestAsync();

        Assert.Equal("/v1/messages", endpoint.RequestPath);
        Assert.Equal("test-key", endpoint.RequestHeaders["x-api-key"]);
        Assert.Equal("2023-06-01", endpoint.RequestHeaders["anthropic-version"]);

        var body = endpoint.ParsedBody;

        // The system prompt is hoisted out of the message list, as the Messages API requires.
        Assert.Equal("You are terse.", body["system"]?.GetValue<string>());
        Assert.Single(body["messages"]!.AsArray());
        Assert.Equal("user", body["messages"]![0]!["role"]?.GetValue<string>());
    }

    [Fact]
    public async Task Tool_results_are_folded_into_a_user_turn_and_runs_are_merged()
    {
        using var endpoint = new FakeEndpoint(Stream);

        var profile = new ProviderProfile
        {
            Name = "anthropic-test",
            Kind = ProviderKind.Anthropic,
            Endpoint = $"http://localhost:{endpoint.Port}/v1",
            ApiKey = "k",
            Model = "m",
        };

        using var client = new AnthropicChatClient(profile);

        await client.GetResponseAsync(
            [
                new ChatMessage(ChatRole.User, "read it"),
                new ChatMessage(ChatRole.Assistant, [
                    new FunctionCallContent("c1", "Read", new Dictionary<string, object?> { ["file_path"] = "a.txt" })]),
                new ChatMessage(ChatRole.Tool, [new FunctionResultContent("c1", "contents")]),
            ],
            null, CancellationToken.None);

        await endpoint.WaitForRequestAsync();

        var messages = endpoint.ParsedBody["messages"]!.AsArray();

        Assert.Equal(3, messages.Count);
        Assert.Equal("assistant", messages[1]!["role"]?.GetValue<string>());
        Assert.Equal("tool_use", messages[1]!["content"]![0]!["type"]?.GetValue<string>());

        // A tool result must arrive as a user-role tool_result block, not as its own role.
        Assert.Equal("user", messages[2]!["role"]?.GetValue<string>());
        Assert.Equal("tool_result", messages[2]!["content"]![0]!["type"]?.GetValue<string>());
        Assert.Equal("c1", messages[2]!["content"]![0]!["tool_use_id"]?.GetValue<string>());
    }
}

public sealed class GeminiClientTests
{
    private const string Stream =
        """
        data: {"candidates":[{"content":{"parts":[{"text":"Hello "}],"role":"model"}}]}

        data: {"candidates":[{"content":{"parts":[{"text":"from Gemini"}],"role":"model"}}]}

        data: {"candidates":[{"content":{"parts":[{"functionCall":{"name":"Read","args":{"file_path":"a.txt"}}}],"role":"model"}}]}

        data: {"candidates":[{"finishReason":"STOP"}],"usageMetadata":{"promptTokenCount":11,"candidatesTokenCount":5,"totalTokenCount":16}}


        """;

    [Fact]
    public async Task Text_function_calls_and_usage_are_decoded()
    {
        using var endpoint = new FakeEndpoint(Stream);

        var profile = new ProviderProfile
        {
            Name = "gemini-test",
            Kind = ProviderKind.Gemini,
            Endpoint = $"http://localhost:{endpoint.Port}/v1beta",
            ApiKey = "test-key",
            Model = "gemini-2.5-pro",
        };

        using var client = new GeminiChatClient(profile);

        var response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "read a.txt")],
            new ChatOptions { Tools = [new StubFunction()] },
            CancellationToken.None);

        await endpoint.WaitForRequestAsync();

        Assert.Equal("Hello from Gemini", response.Text);

        var call = response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Single();
        Assert.Equal("Read", call.Name);
        Assert.Equal("a.txt", call.Arguments?["file_path"]?.ToString());

        Assert.Equal(11, response.Usage?.InputTokenCount);
        Assert.Equal(5, response.Usage?.OutputTokenCount);
    }

    [Fact]
    public async Task The_request_targets_stream_generate_content_and_hoists_the_system_prompt()
    {
        using var endpoint = new FakeEndpoint(Stream);

        var profile = new ProviderProfile
        {
            Name = "gemini-test",
            Kind = ProviderKind.Gemini,
            Endpoint = $"http://localhost:{endpoint.Port}/v1beta",
            ApiKey = "test-key",
            Model = "gemini-2.5-pro",
        };

        using var client = new GeminiChatClient(profile);

        await client.GetResponseAsync(
            [
                new ChatMessage(ChatRole.System, "Be brief."),
                new ChatMessage(ChatRole.User, "hi"),
            ],
            null, CancellationToken.None);

        await endpoint.WaitForRequestAsync();

        Assert.Contains("/v1beta/models/gemini-2.5-pro:streamGenerateContent", endpoint.RequestPath);
        Assert.Contains("alt=sse", endpoint.RequestPath);
        Assert.Equal("test-key", endpoint.RequestHeaders["x-goog-api-key"]);

        var body = endpoint.ParsedBody;
        Assert.Equal("Be brief.", body["systemInstruction"]!["parts"]![0]!["text"]?.GetValue<string>());
        Assert.Equal("user", body["contents"]![0]!["role"]?.GetValue<string>());
    }

    [Fact]
    public async Task Tool_schemas_are_sanitised_into_the_subset_gemini_accepts()
    {
        using var endpoint = new FakeEndpoint(Stream);

        var profile = new ProviderProfile
        {
            Name = "gemini-test",
            Kind = ProviderKind.Gemini,
            Endpoint = $"http://localhost:{endpoint.Port}/v1beta",
            ApiKey = "k",
            Model = "m",
        };

        using var client = new GeminiChatClient(profile);

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")],
            new ChatOptions { Tools = [new StubFunction()] },
            CancellationToken.None);

        await endpoint.WaitForRequestAsync();

        var parameters = endpoint.ParsedBody["tools"]![0]!["functionDeclarations"]![0]!["parameters"]!.AsObject();

        Assert.False(parameters.ContainsKey("$schema"));
        Assert.False(parameters.ContainsKey("additionalProperties"));
        Assert.True(parameters.ContainsKey("properties"));
        Assert.Equal("object", parameters["type"]?.GetValue<string>());
    }

    [Fact]
    public async Task A_function_response_is_paired_back_to_its_call_by_name()
    {
        using var endpoint = new FakeEndpoint(Stream);

        var profile = new ProviderProfile
        {
            Name = "gemini-test",
            Kind = ProviderKind.Gemini,
            Endpoint = $"http://localhost:{endpoint.Port}/v1beta",
            ApiKey = "k",
            Model = "m",
        };

        using var client = new GeminiChatClient(profile);

        await client.GetResponseAsync(
            [
                new ChatMessage(ChatRole.User, "read it"),
                new ChatMessage(ChatRole.Assistant, [
                    new FunctionCallContent("c1", "Read", new Dictionary<string, object?> { ["file_path"] = "a.txt" })]),
                new ChatMessage(ChatRole.Tool, [new FunctionResultContent("c1", "contents")]),
            ],
            null, CancellationToken.None);

        await endpoint.WaitForRequestAsync();

        var contents = endpoint.ParsedBody["contents"]!.AsArray();
        var response = contents[2]!["parts"]![0]!["functionResponse"]!;

        // Gemini gives calls no id, so the name is the only thing that can pair them up.
        Assert.Equal("Read", response["name"]?.GetValue<string>());
        Assert.Equal("contents", response["response"]!["result"]?.GetValue<string>());
    }
}

public sealed class OpenAICompatibleTests
{
    [Fact]
    public async Task A_deepseek_style_endpoint_is_driven_through_the_openai_wire_format()
    {
        const string stream =
            """
            data: {"id":"1","object":"chat.completion.chunk","choices":[{"index":0,"delta":{"role":"assistant","content":"Hi"}}]}

            data: {"id":"1","object":"chat.completion.chunk","choices":[{"index":0,"delta":{"content":" there"}}]}

            data: {"id":"1","object":"chat.completion.chunk","choices":[{"index":0,"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":9,"completion_tokens":3,"total_tokens":12}}

            data: [DONE]


            """;

        using var endpoint = new FakeEndpoint(stream);

        var profile = new ProviderProfile
        {
            Name = "deepseek-test",
            Kind = ProviderKind.OpenAICompatible,
            Endpoint = $"http://localhost:{endpoint.Port}/v1",
            ApiKey = "test-key",
            Model = "deepseek-chat",
        };

        using var client = ProviderFactory.CreateChatClient(profile);

        var text = new StringBuilder();

        await foreach (var update in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")], null, CancellationToken.None))
        {
            text.Append(update.Text);
        }

        await endpoint.WaitForRequestAsync();

        Assert.Equal("Hi there", text.ToString());
        Assert.Contains("/v1/chat/completions", endpoint.RequestPath);
        Assert.Equal("Bearer test-key", endpoint.RequestHeaders["Authorization"]);
        Assert.Equal("deepseek-chat", endpoint.ParsedBody["model"]?.GetValue<string>());
    }

    [Fact]
    public void The_factory_routes_each_provider_kind_to_its_client()
    {
        using var openai = ProviderFactory.CreateChatClient(new ProviderProfile
        {
            Kind = ProviderKind.OpenAICompatible, Model = "m", ApiKey = "k", Endpoint = "http://localhost:1/v1",
        });

        using var anthropic = ProviderFactory.CreateChatClient(new ProviderProfile
        {
            Kind = ProviderKind.Anthropic, Model = "m", ApiKey = "k",
        });

        using var gemini = ProviderFactory.CreateChatClient(new ProviderProfile
        {
            Kind = ProviderKind.Gemini, Model = "m", ApiKey = "k",
        });

        Assert.IsType<AnthropicChatClient>(anthropic);
        Assert.IsType<GeminiChatClient>(gemini);
        Assert.IsNotType<AnthropicChatClient>(openai);
    }

    [Fact]
    public void A_profile_without_a_model_is_rejected_with_a_usable_message()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ProviderFactory.CreateChatClient(new ProviderProfile { Name = "broken", Model = "" }));

        Assert.Contains("no model configured", ex.Message);
    }
}

/// <summary>A tool declaration whose schema carries the keywords Gemini rejects.</summary>
internal sealed class StubFunction : AIFunction
{
    public override string Name => "Read";

    public override string Description => "Reads a file.";

    public override System.Text.Json.JsonElement JsonSchema { get; } =
        System.Text.Json.JsonDocument.Parse(
            """
            {
              "$schema": "https://json-schema.org/draft/2020-12/schema",
              "type": "object",
              "additionalProperties": false,
              "properties": { "file_path": { "type": ["string", "null"] } },
              "required": ["file_path"]
            }
            """).RootElement.Clone();

    protected override ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult<object?>("");
}
