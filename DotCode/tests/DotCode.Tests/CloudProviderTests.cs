using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DotCode.Abstractions;
using DotCode.Engine.Agent;
using DotCode.Engine.Configuration;
using DotCode.Providers;
using DotCode.Providers.Anthropic;
using DotCode.Providers.Auth;
using DotCode.Providers.Http;

namespace DotCode.Tests;

/// <summary>Amazon Bedrock, Google Vertex AI and Microsoft Entra ID: request signing, token flows and wire formats
/// against mocked endpoints (no cloud accounts needed).</summary>
[Collection("http")]
public sealed class CloudProviderTests : IDisposable
{
    private readonly Router _http = new();
    private readonly List<string> _envTouched = [];
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dc-cloud-" + Guid.NewGuid().ToString("n")[..8]);

    public CloudProviderTests()
    {
        Directory.CreateDirectory(_dir);
        ProviderHttp.OverrideHandler = _http;
        // Isolate from the developer's real cloud credentials.
        foreach (var v in new[] { "AWS_ACCESS_KEY_ID", "AWS_SECRET_ACCESS_KEY", "AWS_SESSION_TOKEN", "AWS_BEARER_TOKEN_BEDROCK", "AWS_PROFILE", "AWS_REGION", "AWS_DEFAULT_REGION",
                     "GOOGLE_APPLICATION_CREDENTIALS", "GOOGLE_OAUTH_ACCESS_TOKEN", "GOOGLE_CLOUD_PROJECT", "ANTHROPIC_VERTEX_PROJECT_ID", "CLOUD_ML_REGION",
                     "AZURE_TENANT_ID", "AZURE_CLIENT_ID", "AZURE_CLIENT_SECRET", "AZURE_AUTHORITY_HOST", "AZURE_FEDERATED_TOKEN_FILE", "IDENTITY_ENDPOINT",
                     "CLAUDE_CODE_USE_BEDROCK", "CLAUDE_CODE_USE_VERTEX", "AZURE_OPENAI_ENDPOINT", "AZURE_OPENAI_API_KEY", "AZURE_OPENAI_USE_ENTRA", "ANTHROPIC_API_KEY" })
            Env(v, null);
        Env("AWS_SHARED_CREDENTIALS_FILE", Path.Combine(_dir, "no-credentials"));
        Env("AWS_CONFIG_FILE", Path.Combine(_dir, "no-config"));
    }

    public void Dispose()
    {
        ProviderHttp.OverrideHandler = null;
        foreach (var v in _envTouched) Environment.SetEnvironmentVariable(v, null);
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private void Env(string name, string? value)
    {
        if (!_envTouched.Contains(name)) _envTouched.Add(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    private static ModelRequest Request(string model) => new() { Model = model, System = [new SystemBlock("You are a test.")], Messages = [Message.User("hi")] };

    private static async Task<List<ModelEvent>> Collect(IModelProvider p, ModelRequest r)
    {
        var list = new List<ModelEvent>();
        await foreach (var e in p.StreamAsync(r, CancellationToken.None)) list.Add(e);
        return list;
    }

    private static readonly string[] AnthropicEvents =
    [
        """{"type":"message_start","message":{"id":"msg_1","model":"claude","usage":{"input_tokens":9}}}""",
        """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}""",
        """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hello from the cloud"}}""",
        """{"type":"content_block_stop","index":0}""",
        """{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":4}}""",
        """{"type":"message_stop"}""",
    ];

    private static void AssertHello(List<ModelEvent> events)
    {
        Assert.Equal("Hello from the cloud", Assert.IsType<TextPart>(events.OfType<ContentBlockCompleted>().Single().Part).Text);
        var stop = Assert.Single(events.OfType<MessageStopped>());
        Assert.Equal((9, 4), (stop.Usage!.InputTokens, stop.Usage.OutputTokens));
    }

    // ---------------------------------------------------------------- AWS

    [Fact]
    public void SigV4_matches_the_aws_test_suite_vector()
    {
        // "get-vanilla" from the AWS Signature Version 4 test suite.
        var req = new HttpRequestMessage(HttpMethod.Get, "https://example.amazonaws.com/");
        AwsAuth.Sign(req, [], new AwsCredentials("AKIDEXAMPLE", "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY", null, "test"), "us-east-1", "service",
            new DateTimeOffset(2015, 8, 30, 12, 36, 0, TimeSpan.Zero), includeContentSha256: false);
        Assert.Equal("AWS4-HMAC-SHA256 Credential=AKIDEXAMPLE/20150830/us-east-1/service/aws4_request, SignedHeaders=host;x-amz-date, Signature=5fa00fa31553b73ebf1942676e86291e8372ff2a2260956d9b8aae1d763fbf31",
            req.Headers.GetValues("Authorization").Single());
        // Non-S3 services encode each path segment twice (the request path is already encoded once).
        Assert.Equal("/model/us.anthropic.claude-v1%253A0/invoke", AwsAuth.CanonicalPath("/model/us.anthropic.claude-v1%3A0/invoke", "bedrock"));
    }

    [Fact]
    public async Task Aws_credentials_come_from_the_shared_credentials_file_profile()
    {
        var file = Path.Combine(_dir, "credentials");
        File.WriteAllText(file, "[default]\naws_access_key_id = AKIDDEFAULT\naws_secret_access_key = s1\n\n[work]\naws_access_key_id = AKIDWORK\naws_secret_access_key = s2\naws_session_token = tok\n");
        Env("AWS_SHARED_CREDENTIALS_FILE", file);
        var work = await AwsAuth.ResolveAsync(new ProviderConfig { Type = "bedrock", AwsProfile = "work" }, CancellationToken.None);
        Assert.Equal(("AKIDWORK", "s2", "tok"), (work!.AccessKeyId, work.SecretAccessKey, work.SessionToken));
        Assert.Equal("AKIDDEFAULT", (await AwsAuth.ResolveAsync(new ProviderConfig { Type = "bedrock" }, CancellationToken.None))!.AccessKeyId);
        Env("AWS_ACCESS_KEY_ID", "AKIDENV");
        Env("AWS_SECRET_ACCESS_KEY", "s3");
        Assert.Equal("environment", (await AwsAuth.ResolveAsync(null, CancellationToken.None))!.Source);
    }

    [Fact]
    public async Task Event_stream_frames_round_trip_and_corruption_is_detected()
    {
        var frame = AwsEventStream.Encode(new Dictionary<string, string> { [":message-type"] = "event", [":event-type"] = "chunk" }, Encoding.UTF8.GetBytes("{\"a\":1}"));
        var messages = new List<AwsEventMessage>();
        await foreach (var m in AwsEventStream.ReadAsync(new MemoryStream([.. frame, .. frame]), CancellationToken.None)) messages.Add(m);
        Assert.Equal(2, messages.Count);
        Assert.Equal(("event", "chunk", "{\"a\":1}"), (messages[0].MessageType, messages[0].EventType, Encoding.UTF8.GetString(messages[0].Payload)));

        frame[^6] ^= 0xFF;   // flip a payload byte: the message CRC no longer matches
        await Assert.ThrowsAsync<InvalidDataException>(async () => { await foreach (var _ in AwsEventStream.ReadAsync(new MemoryStream(frame), CancellationToken.None)) { } });
    }

    private static byte[] BedrockBody(params string[] events) =>
        [.. events.SelectMany(e => AwsEventStream.Encode(
            new Dictionary<string, string> { [":message-type"] = "event", [":event-type"] = "chunk", [":content-type"] = "application/json" },
            Encoding.UTF8.GetBytes($$"""{"bytes":"{{Convert.ToBase64String(Encoding.UTF8.GetBytes(e))}}","p":"abc"}""")))];

    [Fact]
    public async Task Bedrock_signs_with_sigv4_and_decodes_the_event_stream()
    {
        Env("AWS_ACCESS_KEY_ID", "AKIDTEST");
        Env("AWS_SECRET_ACCESS_KEY", "secret");
        Env("AWS_SESSION_TOKEN", "session-token");
        HttpRequestMessage? seen = null;
        string? body = null;
        _http.Handle = async (req, ct) =>
        {
            seen = req;
            body = await req.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(BedrockBody(AnthropicEvents)) };
        };
        var p = ProviderFactory.Create("bedrock", new ProviderConfig { Type = "bedrock", Region = "eu-west-1", Betas = ["context-1m-2025-08-07"] });
        var events = await Collect(p, Request("us.anthropic.claude-sonnet-4-5-20250929-v1:0"));

        AssertHello(events);
        Assert.Equal("https://bedrock-runtime.eu-west-1.amazonaws.com/model/us.anthropic.claude-sonnet-4-5-20250929-v1%3A0/invoke-with-response-stream", seen!.RequestUri!.AbsoluteUri);
        var auth = seen.Headers.GetValues("Authorization").Single();
        Assert.StartsWith("AWS4-HMAC-SHA256 Credential=AKIDTEST/", auth);
        Assert.Contains("/eu-west-1/bedrock/aws4_request", auth);
        Assert.Contains("x-amz-security-token", auth);
        Assert.Equal("session-token", seen.Headers.GetValues("x-amz-security-token").Single());
        var json = DotCodeJson.Parse(body!);
        Assert.Equal("bedrock-2023-05-31", json.GetString("anthropic_version"));
        Assert.Null(json.GetProp("model"));
        Assert.Null(json.GetProp("stream"));
        Assert.Equal("context-1m-2025-08-07", json.GetProperty("anthropic_beta")[0].GetString());
    }

    [Fact]
    public async Task Bedrock_api_keys_and_exceptions()
    {
        Env("AWS_BEARER_TOKEN_BEDROCK", "bedrock-key");
        string? auth = null;
        _http.Handle = (req, _) =>
        {
            auth = req.Headers.GetValues("Authorization").Single();
            var error = AwsEventStream.Encode(new Dictionary<string, string> { [":message-type"] = "exception", [":exception-type"] = "throttlingException" },
                Encoding.UTF8.GetBytes("""{"message":"Too many requests, please wait"}"""));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(error) });
        };
        var p = ProviderFactory.Create("bedrock", new ProviderConfig { Type = "bedrock" });
        var ex = await Assert.ThrowsAsync<ModelProviderException>(() => Collect(p, Request("anthropic.claude-haiku-4-5-20251001-v1:0")));
        Assert.Equal("Bearer bedrock-key", auth);
        Assert.Equal("rate_limit", ex.Code);
        Assert.True(ex.Retryable);
    }

    // ---------------------------------------------------------------- Google

    [Fact]
    public async Task Vertex_claude_uses_the_publisher_endpoint_and_an_oauth_token()
    {
        Env("GOOGLE_OAUTH_ACCESS_TOKEN", "ya29.test");
        HttpRequestMessage? seen = null;
        string? body = null;
        _http.Handle = async (req, ct) =>
        {
            seen = req;
            body = await req.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(string.Concat(AnthropicEvents.Select(e => $"data: {e}\n\n"))) };
        };
        var p = ProviderFactory.Create("vertex", new ProviderConfig { Type = "vertex", Project = "my-proj", Region = "us-east5" });
        AssertHello(await Collect(p, Request("claude-sonnet-4-5@20250929")));
        Assert.Equal("https://us-east5-aiplatform.googleapis.com/v1/projects/my-proj/locations/us-east5/publishers/anthropic/models/claude-sonnet-4-5@20250929:streamRawPredict", seen!.RequestUri!.ToString());
        Assert.Equal("Bearer ya29.test", seen.Headers.GetValues("Authorization").Single());
        var json = DotCodeJson.Parse(body!);
        Assert.Equal("vertex-2023-10-16", json.GetString("anthropic_version"));
        Assert.True(json.GetBool("stream"));
        Assert.Null(json.GetProp("model"));
    }

    [Fact]
    public async Task Service_account_jwt_is_exchanged_once_and_used_for_gemini_on_vertex()
    {
        using var rsa = RSA.Create(2048);
        var keyFile = Path.Combine(_dir, "sa.json");
        File.WriteAllText(keyFile, JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["type"] = "service_account", ["project_id"] = "sa-project", ["private_key_id"] = "kid1",
            ["private_key"] = rsa.ExportPkcs8PrivateKeyPem(), ["client_email"] = "bot@sa-project.iam.gserviceaccount.com", ["token_uri"] = "https://oauth.test/token",
        }, TestJson.Default.DictionaryStringString));
        Env("GOOGLE_APPLICATION_CREDENTIALS", keyFile);

        var tokenCalls = 0;
        var authHeaders = new List<string>();
        string? streamUrl = null;
        _http.Handle = async (req, ct) =>
        {
            if (req.RequestUri!.Host == "oauth.test")
            {
                tokenCalls++;
                var form = (await req.Content!.ReadAsStringAsync(ct)).Split('&').Select(kv => kv.Split('=')).ToDictionary(kv => kv[0], kv => Uri.UnescapeDataString(kv[1]));
                Assert.Equal("urn:ietf:params:oauth:grant-type:jwt-bearer", form["grant_type"]);
                var parts = form["assertion"].Split('.');
                var signature = Convert.FromBase64String(Pad(parts[2].Replace('-', '+').Replace('_', '/')));
                Assert.True(rsa.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1), "JWT signed with the service account key");
                var claims = DotCodeJson.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(Pad(parts[1].Replace('-', '+').Replace('_', '/')))));
                Assert.Equal(("bot@sa-project.iam.gserviceaccount.com", "https://oauth.test/token", GoogleAuth.Scope), (claims.GetString("iss"), claims.GetString("aud"), claims.GetString("scope")));
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"access_token":"sa-token","expires_in":3599,"token_type":"Bearer"}""") };
            }
            streamUrl = req.RequestUri.ToString();
            authHeaders.Add(req.Headers.GetValues("Authorization").Single());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("data: {\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"Gemini on Vertex\"}]},\"finishReason\":\"STOP\"}],\"usageMetadata\":{\"promptTokenCount\":5,\"candidatesTokenCount\":3}}\n\n"),
            };
        };
        var p = ProviderFactory.Create("vg", new ProviderConfig { Type = "gemini", Platform = "vertex", Region = "europe-west4" });
        var first = await Collect(p, Request("gemini-2.5-flash"));
        await Collect(p, Request("gemini-2.5-flash"));

        Assert.Equal("Gemini on Vertex", Assert.IsType<TextPart>(first.OfType<ContentBlockCompleted>().Single().Part).Text);
        Assert.Equal("https://europe-west4-aiplatform.googleapis.com/v1/projects/sa-project/locations/europe-west4/publishers/google/models/gemini-2.5-flash:streamGenerateContent?alt=sse", streamUrl);
        Assert.All(authHeaders, h => Assert.Equal("Bearer sa-token", h));
        Assert.Equal(1, tokenCalls);   // cached until shortly before expiry
    }

    private static string Pad(string b64) => b64 + new string('=', (4 - b64.Length % 4) % 4);

    // ---------------------------------------------------------------- Microsoft Entra ID

    [Fact]
    public async Task Azure_with_entra_uses_a_client_credentials_token_instead_of_the_api_key()
    {
        Env("AZURE_TENANT_ID", "tenant-1");
        Env("AZURE_CLIENT_ID", "client-1");
        Env("AZURE_CLIENT_SECRET", "s3cret");
        Env("AZURE_AUTHORITY_HOST", "https://login.test");
        Env("AZURE_OPENAI_API_KEY", "should-not-be-sent");
        Dictionary<string, string>? form = null;
        HttpRequestMessage? api = null;
        _http.Handle = async (req, ct) =>
        {
            if (req.RequestUri!.Host == "login.test")
            {
                Assert.Equal("/tenant-1/oauth2/v2.0/token", req.RequestUri.AbsolutePath);
                form = (await req.Content!.ReadAsStringAsync(ct)).Split('&').Select(kv => kv.Split('=')).ToDictionary(kv => kv[0], kv => Uri.UnescapeDataString(kv[1]));
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"token_type":"Bearer","expires_in":3599,"access_token":"entra-token"}""") };
            }
            api = req;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("data: {\"id\":\"c1\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"Entra ok\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n"),
            };
        };
        var p = ProviderFactory.Create("azure", new ProviderConfig { Type = "azure", Api = "chat", Auth = "entra", BaseUrl = "https://res.openai.azure.com/openai/v1" });
        var events = await Collect(p, Request("gpt-5-mini"));

        Assert.Equal("Entra ok", Assert.IsType<TextPart>(events.OfType<ContentBlockCompleted>().Single().Part).Text);
        Assert.Equal(("client_credentials", "client-1", "s3cret", EntraAuth.CognitiveServicesScope), (form!["grant_type"], form["client_id"], form["client_secret"], form["scope"]));
        Assert.Equal("Bearer entra-token", api!.Headers.GetValues("Authorization").Single());
        Assert.False(api.Headers.Contains("api-key"));
    }

    [Fact]
    public void Environment_switches_configure_bedrock_vertex_and_entra()
    {
        Env("CLAUDE_CODE_USE_BEDROCK", "1");
        Env("AZURE_OPENAI_ENDPOINT", "https://res.openai.azure.com");
        Env("AZURE_OPENAI_USE_ENTRA", "1");
        var providers = ProviderFactory.FromEnvironment();
        Assert.Equal("bedrock", providers["bedrock"].Type);
        Assert.Equal("entra", providers["azure"].Auth);
        Assert.Equal("https://res.openai.azure.com/openai/v1", providers["azure"].BaseUrl);

        var router = new ModelRouter(new Settings());
        var sonnet = router.Resolve("sonnet");
        Assert.Equal(("bedrock", "us.anthropic.claude-sonnet-4-5-20250929-v1:0"), (sonnet.ProviderName, sonnet.Model));
        Assert.Equal(AnthropicPlatform.Bedrock, Assert.IsType<AnthropicProvider>(sonnet.Provider).Platform);
        Assert.Equal("claude-opus-4-5@20251101", AnthropicProvider.PlatformModelId("vertex", "opus"));
        var explicitAlias = router.Resolve("bedrock:haiku");
        Assert.Equal("us.anthropic.claude-haiku-4-5-20251001-v1:0", explicitAlias.Model);
    }

    /// <summary>Routes requests to a test lambda.</summary>
    private sealed class Router : HttpMessageHandler
    {
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Handle { get; set; } = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Handle(request, ct);
    }
}
