using System.Net;
using System.Text;
using System.Threading.Channels;
using DotCode.Abstractions;
using DotCode.Engine.Configuration;
using DotCode.Engine.Mcp;
using DotCode.Providers.Http;

namespace DotCode.Tests;

/// <summary>Legacy HTTP+SSE MCP transport (protocol 2024-11-05) against an in-memory server.</summary>
public sealed class McpSseTests : IDisposable
{
    private readonly LegacySseServer _server = new();

    public McpSseTests() => ProviderHttp.OverrideHandler = _server;

    public void Dispose()
    {
        ProviderHttp.OverrideHandler = null;
        _server.Close();
    }

    [Fact]
    public async Task Sse_transport_uses_the_endpoint_event_and_stream_responses()
    {
        await using var client = await McpClient.ConnectAsync("legacy", new McpServerConfig { Type = "sse", Url = "http://legacy.test/sse", Headers = new() { ["Authorization"] = "Bearer t" } }, ".", CancellationToken.None);
        Assert.Equal("sse", client.Transport);
        Assert.Equal("legacy-demo", client.ServerName);
        Assert.Equal("echo", Assert.Single(client.Tools).Name);
        var (content, isError) = await client.CallToolAsync("echo", DotCodeJson.Parse("""{"text":"hello"}"""), CancellationToken.None);
        Assert.False(isError);
        Assert.Equal("echo: hello", Assert.IsType<TextPart>(Assert.Single(content)).Text);
        Assert.All(_server.Requests, r => Assert.Equal("Bearer t", r.Auth));
        Assert.Contains(_server.Requests, r => r.Method == "POST" && r.Path == "/messages?sessionId=abc");
    }

    [Fact]
    public async Task Http_type_falls_back_to_sse_for_legacy_servers()
    {
        await using var client = await McpClient.ConnectAsync("legacy", new McpServerConfig { Type = "http", Url = "http://legacy.test/sse" }, ".", CancellationToken.None);
        Assert.Equal("sse", client.Transport);
        Assert.Contains(_server.Requests, r => r.Method == "POST" && r.Path == "/sse");   // the Streamable HTTP attempt (405)
        Assert.Single(client.Tools);
    }

    [Fact]
    public async Task Pending_requests_fail_when_the_stream_closes()
    {
        await using var client = await McpClient.ConnectAsync("legacy", new McpServerConfig { Type = "sse", Url = "http://legacy.test/sse" }, ".", CancellationToken.None);
        _server.Silent = true;
        var call = client.CallToolAsync("echo", DotCodeJson.Parse("""{"text":"x"}"""), CancellationToken.None);
        await Task.Delay(100);
        _server.Close();
        var ex = await Assert.ThrowsAsync<McpException>(() => call.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("disconnected", ex.Message);
    }

    private sealed class LegacySseServer : HttpMessageHandler
    {
        private readonly Channel<byte[]> _stream = Channel.CreateUnbounded<byte[]>();
        public List<(string Method, string Path, string? Auth)> Requests { get; } = [];
        public bool Silent { get; set; }

        public void Close() => _stream.Writer.TryComplete();

        private void Emit(string evt, string data) => _stream.Writer.TryWrite(Encoding.UTF8.GetBytes($"event: {evt}\ndata: {data}\n\n"));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.PathAndQuery;
            lock (Requests) Requests.Add((request.Method.Method, path, request.Headers.Authorization?.ToString()));
            if (request.Method == HttpMethod.Get && path == "/sse")
            {
                Emit("endpoint", "/messages?sessionId=abc");
                var content = new StreamContent(new ChannelStream(_stream.Reader));
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            }
            if (request.Method == HttpMethod.Post && path == "/messages?sessionId=abc")
            {
                var msg = DotCodeJson.Parse(await request.Content!.ReadAsStringAsync(ct));
                if (msg.GetProp("id") is { } id && !Silent)
                {
                    var result = msg.GetString("method") switch
                    {
                        "initialize" => """{"protocolVersion":"2024-11-05","capabilities":{"tools":{}},"serverInfo":{"name":"legacy-demo","version":"1.0"}}""",
                        "tools/list" => """{"tools":[{"name":"echo","description":"Echo text","inputSchema":{"type":"object","properties":{"text":{"type":"string"}}}}]}""",
                        "tools/call" => $$"""{"content":[{"type":"text","text":"echo: {{msg.GetProp("params")?.GetProp("arguments")?.GetString("text")}}"}]}""",
                        _ => "{}",
                    };
                    Emit("message", $$"""{"jsonrpc":"2.0","id":{{id.GetRawText()}},"result":{{result}}}""");
                }
                return new HttpResponseMessage(HttpStatusCode.Accepted);
            }
            // Legacy servers do not accept Streamable HTTP POSTs on the SSE URL.
            return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed) { Content = new StringContent("Method Not Allowed") };
        }
    }

    /// <summary>Read-only stream fed by a channel (an endless SSE body until the channel completes).</summary>
    private sealed class ChannelStream(ChannelReader<byte[]> reader) : Stream
    {
        private ReadOnlyMemory<byte> _current;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            while (_current.IsEmpty)
            {
                if (!await reader.WaitToReadAsync(ct)) return 0;
                if (reader.TryRead(out var next)) _current = next;
            }
            var n = Math.Min(buffer.Length, _current.Length);
            _current[..n].CopyTo(buffer);
            _current = _current[n..];
            return n;
        }

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
