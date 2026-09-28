using System.Net;
using System.Text;
using System.Text.Json;
using DotCode.Abstractions;
using DotCode.Providers;
using DotCode.Providers.Http;

namespace DotCode.Tests;

/// <summary>Contract tests: every adapter must turn its vendor's wire format into the same normalized events.
/// Each test feeds a recorded-style SSE/NDJSON stream through a mock HTTP handler (no network, no cost).</summary>
[Collection("http")]
public sealed class ProviderContractTests : IDisposable
{
    private readonly MockHandler _handler = new();

    public ProviderContractTests() => ProviderHttp.OverrideHandler = _handler;
    public void Dispose() => ProviderHttp.OverrideHandler = null;

    private static ModelRequest Request(string model, bool tools = true) => new()
    {
        Model = model,
        System = [new SystemBlock("You are a test.")],
        Messages = [Message.User("hi")],
        Tools = tools ? [new ToolSchema("Read", "Read a file", DotCodeJson.Parse("""{"type":"object","properties":{"file_path":{"type":"string"},"mode":{"anyOf":[{"type":"string"},{"type":"null"}]}},"required":["file_path"],"additionalProperties":false}"""))] : [],
    };

    private static async Task<List<ModelEvent>> Collect(IModelProvider p, ModelRequest r)
    {
        var list = new List<ModelEvent>();
        await foreach (var e in p.StreamAsync(r, CancellationToken.None)) list.Add(e);
        return list;
    }

    [Fact]
    public async Task Anthropic_streams_text_thinking_and_tool_use()
    {
        _handler.Respond(Sse(
            ("message_start", """{"type":"message_start","message":{"id":"msg_1","model":"claude-sonnet-4-5","usage":{"input_tokens":12,"cache_read_input_tokens":3}}}"""),
            ("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":""}}"""),
            ("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"Let me read."}}"""),
            ("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"sig123"}}"""),
            ("content_block_stop", """{"type":"content_block_stop","index":0}"""),
            ("content_block_start", """{"type":"content_block_start","index":1,"content_block":{"type":"text","text":""}}"""),
            ("content_block_delta", """{"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"Reading "}}"""),
            ("content_block_delta", """{"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"now."}}"""),
            ("content_block_stop", """{"type":"content_block_stop","index":1}"""),
            ("content_block_start", """{"type":"content_block_start","index":2,"content_block":{"type":"tool_use","id":"toolu_1","name":"Read","input":{}}}"""),
            ("content_block_delta", """{"type":"content_block_delta","index":2,"delta":{"type":"input_json_delta","partial_json":"{\"file_pa"}}"""),
            ("content_block_delta", """{"type":"content_block_delta","index":2,"delta":{"type":"input_json_delta","partial_json":"th\":\"a.txt\"}"}}"""),
            ("content_block_stop", """{"type":"content_block_stop","index":2}"""),
            ("message_delta", """{"type":"message_delta","delta":{"stop_reason":"tool_use"},"usage":{"output_tokens":40}}"""),
            ("message_stop", """{"type":"message_stop"}""")));
        var p = ProviderFactory.Create("anthropic", new ProviderConfig { Type = "anthropic", ApiKey = "k" });
        var events = await Collect(p, Request("claude-sonnet-4-5") with { Reasoning = new ReasoningOptions(ReasoningEffort.Medium) });

        var parts = events.OfType<ContentBlockCompleted>().Select(c => c.Part).ToList();
        var thinking = Assert.IsType<ThinkingPart>(parts[0]);
        Assert.Equal("Let me read.", thinking.Text);
        Assert.Equal("sig123", thinking.Opaque!.Payload.GetString("signature"));
        Assert.Equal("Reading now.", Assert.IsType<TextPart>(parts[1]).Text);
        var tool = Assert.IsType<ToolUsePart>(parts[2]);
        Assert.Equal("a.txt", tool.Input.GetString("file_path"));
        var stop = Assert.Single(events.OfType<MessageStopped>());
        Assert.Equal(StopReason.ToolUse, stop.Reason);
        Assert.Equal(12, stop.Usage!.InputTokens);
        Assert.Equal(3, stop.Usage.CacheReadTokens);
        Assert.Equal(40, stop.Usage.OutputTokens);

        // Request shape: cache breakpoints, thinking budget, x-api-key auth.
        var body = _handler.LastBody!;
        Assert.Equal("ephemeral", body.Value.GetProperty("system")[0].GetProperty("cache_control").GetString("type"));
        Assert.Equal("enabled", body.Value.GetProperty("thinking").GetString("type"));
        Assert.True(body.Value.GetProperty("tools")[0].TryGetProperty("cache_control", out _));
        Assert.Equal("k", _handler.LastRequest!.Headers.GetValues("x-api-key").Single());
        Assert.Equal("https://api.anthropic.com/v1/messages", _handler.LastRequest.RequestUri!.ToString());
    }

    [Fact]
    public async Task OpenAI_chat_assembles_streamed_tool_call_arguments_and_reasoning()
    {
        _handler.Respond(Sse(
            (null, """{"id":"c1","model":"deepseek-chat","choices":[{"index":0,"delta":{"role":"assistant","reasoning_content":"think"}}]}"""),
            (null, """{"id":"c1","choices":[{"index":0,"delta":{"content":"Sure"}}]}"""),
            (null, """{"id":"c1","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call_9","type":"function","function":{"name":"Read","arguments":""}}]}}]}"""),
            (null, """{"id":"c1","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"file_path\":"}}]}}]}"""),
            (null, """{"id":"c1","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"\"b.cs\"}"}}]}}]}"""),
            (null, """{"id":"c1","choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}"""),
            (null, """{"id":"c1","choices":[],"usage":{"prompt_tokens":100,"completion_tokens":20,"prompt_tokens_details":{"cached_tokens":60}}}"""),
            (null, "[DONE]")));
        var p = ProviderFactory.Create("deepseek", new ProviderConfig { Type = "deepseek", ApiKey = "k" });
        var events = await Collect(p, Request("deepseek-chat"));
        var parts = events.OfType<ContentBlockCompleted>().Select(c => c.Part).ToList();
        Assert.Equal("think", Assert.IsType<ThinkingPart>(parts[0]).Text);
        Assert.Equal("Sure", Assert.IsType<TextPart>(parts[1]).Text);
        var tool = Assert.IsType<ToolUsePart>(parts[2]);
        Assert.Equal("call_9", tool.Id);
        Assert.Equal("b.cs", tool.Input.GetString("file_path"));
        var stop = events.OfType<MessageStopped>().Single();
        Assert.Equal(StopReason.ToolUse, stop.Reason);
        Assert.Equal(40, stop.Usage!.InputTokens);
        Assert.Equal(60, stop.Usage.CacheReadTokens);
        Assert.Equal("https://api.deepseek.com/chat/completions", _handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal("system", _handler.LastBody!.Value.GetProperty("messages")[0].GetString("role"));
    }

    [Fact]
    public async Task OpenAI_chat_sends_tool_results_as_tool_messages_and_reasoning_back_within_turn()
    {
        _handler.Respond(Sse((null, """{"choices":[{"index":0,"delta":{"content":"done"},"finish_reason":"stop"}]}"""), (null, "[DONE]")));
        var p = ProviderFactory.Create("deepseek", new ProviderConfig { Type = "deepseek", ApiKey = "k" });
        var call = new ToolUsePart("call_1", "Read", DotCodeJson.Parse("""{"file_path":"x"}"""));
        var request = Request("deepseek-chat") with
        {
            Messages =
            [
                Message.User("read x"),
                new Message { Role = Role.Assistant, ProviderId = "deepseek", Content = [new ThinkingPart("need to read"), call] },
                Message.User([ToolResultPart.FromText("call_1", "contents")]),
            ],
        };
        await Collect(p, request);
        var messages = _handler.LastBody!.Value.GetProperty("messages");
        var assistant = messages[2];
        Assert.Equal("assistant", assistant.GetString("role"));
        Assert.Equal("need to read", assistant.GetString("reasoning_content"));
        Assert.Equal("call_1", assistant.GetProperty("tool_calls")[0].GetString("id"));
        var tool = messages[3];
        Assert.Equal("tool", tool.GetString("role"));
        Assert.Equal("contents", tool.GetString("content"));
    }

    [Fact]
    public async Task OpenAI_responses_maps_function_calls_reasoning_and_usage()
    {
        _handler.Respond(Sse(
            ("response.created", """{"type":"response.created","response":{"id":"resp_1","model":"gpt-5"}}"""),
            ("response.reasoning_summary_text.delta", """{"type":"response.reasoning_summary_text.delta","delta":"planning"}"""),
            ("response.output_item.done", """{"type":"response.output_item.done","item":{"type":"reasoning","id":"rs_1","summary":[{"type":"summary_text","text":"planning"}],"encrypted_content":"ENC"}}"""),
            ("response.output_text.delta", """{"type":"response.output_text.delta","delta":"Hi"}"""),
            ("response.output_item.done", """{"type":"response.output_item.done","item":{"type":"message","content":[{"type":"output_text","text":"Hi"}]}}"""),
            ("response.output_item.added", """{"type":"response.output_item.added","item":{"type":"function_call","id":"fc_1","call_id":"call_1","name":"Read"}}"""),
            ("response.function_call_arguments.delta", """{"type":"response.function_call_arguments.delta","item_id":"fc_1","delta":"{\"file_path\":\"c.md\"}"}"""),
            ("response.output_item.done", """{"type":"response.output_item.done","item":{"type":"function_call","id":"fc_1","call_id":"call_1","name":"Read","arguments":"{\"file_path\":\"c.md\"}"}}"""),
            ("response.completed", """{"type":"response.completed","response":{"usage":{"input_tokens":50,"output_tokens":9,"input_tokens_details":{"cached_tokens":10},"output_tokens_details":{"reasoning_tokens":4}}}}""")));
        var p = ProviderFactory.Create("azure", new ProviderConfig { Type = "azure", BaseUrl = "https://x.openai.azure.com/openai/v1", ApiKey = "az" });
        var events = await Collect(p, Request("gpt-5-mini"));
        var parts = events.OfType<ContentBlockCompleted>().Select(c => c.Part).ToList();
        var thinking = Assert.IsType<ThinkingPart>(parts[0]);
        Assert.Equal("ENC", thinking.Opaque!.Payload.GetString("encrypted_content"));
        Assert.Equal("Hi", Assert.IsType<TextPart>(parts[1]).Text);
        Assert.Equal("c.md", Assert.IsType<ToolUsePart>(parts[2]).Input.GetString("file_path"));
        var stop = events.OfType<MessageStopped>().Single();
        Assert.Equal(StopReason.ToolUse, stop.Reason);
        Assert.Equal(40, stop.Usage!.InputTokens);
        Assert.Equal(4, stop.Usage.ReasoningTokens);
        Assert.Equal("az", _handler.LastRequest!.Headers.GetValues("api-key").Single());
        Assert.False(_handler.LastBody!.Value.GetProperty("store").GetBoolean());
        Assert.Equal("reasoning.encrypted_content", _handler.LastBody!.Value.GetProperty("include")[0].GetString());
    }

    [Fact]
    public async Task Gemini_maps_function_calls_signatures_and_sanitizes_schema()
    {
        _handler.Respond(Sse(
            (null, """{"candidates":[{"content":{"role":"model","parts":[{"text":"hmm","thought":true},{"text":"Let me check"}]}}]}"""),
            (null, """{"candidates":[{"content":{"role":"model","parts":[{"functionCall":{"name":"Read","args":{"file_path":"d.go"}},"thoughtSignature":"SIG"}]},"finishReason":"STOP"}],"usageMetadata":{"promptTokenCount":30,"candidatesTokenCount":7,"thoughtsTokenCount":2}}""")));
        var p = ProviderFactory.Create("gemini", new ProviderConfig { Type = "gemini", ApiKey = "g" });
        var events = await Collect(p, Request("gemini-2.5-pro"));
        var parts = events.OfType<ContentBlockCompleted>().Select(c => c.Part).ToList();
        Assert.Equal("hmm", Assert.IsType<ThinkingPart>(parts[0]).Text);
        Assert.Equal("Let me check", Assert.IsType<TextPart>(parts[1]).Text);
        var call = Assert.IsType<ToolUsePart>(parts[2]);
        Assert.Equal("SIG", call.Opaque!.Payload.GetString("thoughtSignature"));
        Assert.Equal(StopReason.ToolUse, events.OfType<MessageStopped>().Single().Reason);

        var decl = _handler.LastBody!.Value.GetProperty("tools")[0].GetProperty("functionDeclarations")[0].GetProperty("parameters");
        Assert.False(decl.TryGetProperty("additionalProperties", out _));
        Assert.True(decl.GetProperty("properties").GetProperty("mode").GetProperty("nullable").GetBoolean());
        Assert.Contains(":streamGenerateContent?alt=sse", _handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal("g", _handler.LastRequest.Headers.GetValues("x-goog-api-key").Single());
    }

    [Fact]
    public async Task Ollama_reads_ndjson_and_raises_num_ctx()
    {
        _handler.Respond("""
            {"message":{"role":"assistant","content":"Hel"},"done":false}
            {"message":{"role":"assistant","content":"lo","tool_calls":[{"function":{"name":"Read","arguments":{"file_path":"e.py"}}}]},"done":false}
            {"message":{"role":"assistant","content":""},"done":true,"done_reason":"stop","prompt_eval_count":22,"eval_count":5}
            """, "application/x-ndjson", onPath: "/api/chat");
        _handler.Respond("""{"capabilities":["completion","tools"],"model_info":{"qwen3.context_length":40960}}""", "application/json", onPath: "/api/show");
        var p = ProviderFactory.Create("ollama", new ProviderConfig { Type = "ollama" });
        var events = await Collect(p, Request("qwen3:8b"));
        var parts = events.OfType<ContentBlockCompleted>().Select(c => c.Part).ToList();
        Assert.Equal("Hello", Assert.IsType<TextPart>(parts[0]).Text);
        Assert.Equal("e.py", Assert.IsType<ToolUsePart>(parts[1]).Input.GetString("file_path"));
        Assert.Equal(40960, _handler.BodyFor("/api/chat")!.Value.GetProperty("options").GetProperty("num_ctx").GetInt32());
    }

    [Fact]
    public async Task Http_errors_are_mapped_to_retryable_codes()
    {
        _handler.Respond("""{"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}""", "application/json", status: (HttpStatusCode)529);
        var p = ProviderFactory.Create("anthropic", new ProviderConfig { Type = "anthropic", ApiKey = "k" });
        var ex = await Assert.ThrowsAsync<ModelProviderException>(() => Collect(p, Request("claude-sonnet-4-5")));
        Assert.Equal("overloaded", ex.Code);
        Assert.True(ex.Retryable);

        _handler.Respond("""{"error":{"message":"prompt is too long: 300000 tokens > 200000"}}""", "application/json", status: HttpStatusCode.BadRequest);
        var ex2 = await Assert.ThrowsAsync<ModelProviderException>(() => Collect(p, Request("claude-sonnet-4-5")));
        Assert.Equal("context_length", ex2.Code);
        Assert.False(ex2.Retryable);
    }

    [Fact]
    public async Task Text_tool_protocol_parses_tool_calls_for_models_without_function_calling()
    {
        var inner = new Providers.Testing.ScriptedProvider("mock", [new() { Text = "I'll read it.\n<tool_call>{\"name\": \"Read\", \"input\": {\"file_path\": \"f.txt\"}}</tool_call>" }]);
        var p = new TextToolProtocolProvider(inner);
        var events = await Collect(p, Request("x"));
        var call = events.OfType<ContentBlockCompleted>().Select(c => c.Part).OfType<ToolUsePart>().Single();
        Assert.Equal("Read", call.Name);
        Assert.Equal("f.txt", call.Input.GetString("file_path"));
        Assert.Equal(StopReason.ToolUse, events.OfType<MessageStopped>().Single().Reason);
        Assert.DoesNotContain(events.OfType<TextDelta>(), d => d.Text.Contains("<tool_call>"));
    }

    private static string Sse(params (string? Event, string Data)[] events)
    {
        var sb = new StringBuilder();
        foreach (var (e, d) in events)
        {
            if (e is not null) sb.Append("event: ").Append(e).Append('\n');
            sb.Append("data: ").Append(d).Append("\n\n");
        }
        return sb.ToString();
    }

    private sealed class MockHandler : HttpMessageHandler
    {
        private readonly List<(string? Path, string Body, string ContentType, HttpStatusCode Status)> _responses = [];
        private readonly Dictionary<string, JsonElement> _bodies = [];
        public HttpRequestMessage? LastRequest { get; private set; }
        public JsonElement? LastBody { get; private set; }
        public JsonElement? BodyFor(string path) => _bodies.TryGetValue(path, out var b) ? b : null;

        public void Respond(string body, string contentType = "text/event-stream", HttpStatusCode status = HttpStatusCode.OK, string? onPath = null)
        {
            _responses.RemoveAll(r => r.Path == onPath);
            _responses.Add((onPath, body, contentType, status));
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            JsonElement? body = null;
            if (request.Content is not null)
            {
                var text = await request.Content.ReadAsStringAsync(ct);
                if (text.Length > 0) body = DotCodeJson.Parse(text);
            }
            if (!path.EndsWith("/api/show", StringComparison.Ordinal)) { LastRequest = request; LastBody = body; }
            if (body is { } b) _bodies[path] = b;
            var match = _responses.FirstOrDefault(r => r.Path is not null && path.EndsWith(r.Path, StringComparison.Ordinal));
            if (match.Body is null) match = _responses.First(r => r.Path is null);
            return new HttpResponseMessage(match.Status) { Content = new StringContent(match.Body, Encoding.UTF8, match.ContentType) };
        }
    }
}

[CollectionDefinition("http", DisableParallelization = true)]
public sealed class HttpCollection;
