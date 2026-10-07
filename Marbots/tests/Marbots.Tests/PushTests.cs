using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Marbots.Abstractions;
using Marbots.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Marbots.Tests;

/// <summary>Push to FCM (OAuth JWT-bearer + HTTP v1), APNs (ES256 provider token) and ntfy, against a fake upstream.</summary>
public sealed class PushTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mb-push-" + Guid.NewGuid().ToString("N")[..8]);
    private ServiceProvider _sp = default!;
    private WebApplication _upstream = default!;
    private readonly ConcurrentQueue<(string Path, Dictionary<string, string> Headers, string Body)> _calls = new();
    private readonly RSA _google = RSA.Create(2048);
    private readonly ECDsa _apple = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public async Task InitializeAsync()
    {
        var b = WebApplication.CreateSlimBuilder();
        b.WebHost.UseUrls("http://127.0.0.1:0");
        _upstream = b.Build();
        _upstream.Run(async ctx =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var body = await reader.ReadToEndAsync();
            var path = ctx.Request.Path.Value!;
            _calls.Enqueue((path, ctx.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase), body));
            if (path == "/token") await ctx.Response.WriteAsJsonAsync(new { access_token = "ya29.fake", expires_in = 3600 });
            else if (path.EndsWith("messages:send") && body.Contains("gone-token")) { ctx.Response.StatusCode = 404; await ctx.Response.WriteAsync("""{"error":{"status":"NOT_FOUND","details":[{"errorCode":"UNREGISTERED"}]}}"""); }
            else await ctx.Response.WriteAsJsonAsync(new { name = "ok" });
        });
        await _upstream.StartAsync();
        var url = _upstream.Urls.First();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        services.AddMarbotsRuntime(new MarbotsOptions
        {
            DataDirectory = _dir,
            Push = new MarbotsPushOptions
            {
                FcmBaseUrl = url, ApnsBaseUrl = url, NtfyServer = url, ApnsKeyId = "KEY123", ApnsTeamId = "TEAM456", ApnsBundleId = "id.gravicode.marbots",
                PublicUrl = "https://marbots.example",
            },
        });
        _sp = services.BuildServiceProvider();
        var secrets = _sp.GetRequiredService<LocalSecretProvider>();
        secrets.Set("FCM_SERVICE_ACCOUNT", new JsonObject
        {
            ["type"] = "service_account", ["project_id"] = "marbots-test", ["client_email"] = "push@marbots-test.iam.gserviceaccount.com",
            ["private_key"] = _google.ExportPkcs8PrivateKeyPem(), ["token_uri"] = url + "/token",
        }.ToJsonString());
        secrets.Set("APNS_AUTH_KEY", _apple.ExportPkcs8PrivateKeyPem());
        foreach (var hosted in _sp.GetServices<IHostedService>().Where(h => h is MarbotsBootstrapper or PushService))
            await hosted.StartAsync(default);
    }

    public async Task DisposeAsync()
    {
        _sp.GetRequiredService<MarbotsEngine>().Shutdown();
        await _sp.DisposeAsync();
        await _upstream.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private async Task<List<(string Path, Dictionary<string, string> Headers, string Body)>> WaitForAsync(Func<List<(string Path, Dictionary<string, string> Headers, string Body)>, bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline && !done([.. _calls])) await Task.Delay(100);
        return [.. _calls];
    }

    private static JsonNode Part(string jwt, int i) => JsonNode.Parse(Encoding.UTF8.GetString(Jwt.FromB64(jwt.Split('.')[i])))!;

    private static bool Verify(string jwt, Func<byte[], byte[], bool> verify)
    {
        var parts = jwt.Split('.');
        return verify(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), Jwt.FromB64(parts[2]));
    }

    [Fact]
    public async Task Approvals_reach_fcm_apns_and_ntfy_devices()
    {
        var push = _sp.GetRequiredService<PushService>();
        await push.RegisterAsync(new RegisterPushDeviceRequest("fcm", "android-token-1", "Pixel"));
        await push.RegisterAsync(new RegisterPushDeviceRequest("fcm", "gone-token", "Old phone"));
        await push.RegisterAsync(new RegisterPushDeviceRequest("apns", "abc123", "iPhone", [PushTopics.Approvals]));
        await push.RegisterAsync(new RegisterPushDeviceRequest("ntfy", "marbots-team-8f3k2", "Team topic"));
        await Assert.ThrowsAsync<ArgumentException>(() => push.RegisterAsync(new RegisterPushDeviceRequest("ntfy", "bad topic!")));
        await Assert.ThrowsAsync<ArgumentException>(() => push.RegisterAsync(new RegisterPushDeviceRequest("sms", "123")));

        await _sp.GetRequiredService<IEventBus>().PublishAsync(new AgentEvent { Type = EventTypes.ApprovalRequested, BotId = "dina", TaskId = "tsk_1", ThreadId = "thr_1", Message = "run_command: shell access" });
        var calls = await WaitForAsync(c => c.Count(x => x.Path.EndsWith("messages:send")) >= 2 && c.Any(x => x.Path.StartsWith("/3/device/")) && c.Any(x => x.Path == "/marbots-team-8f3k2"));

        // FCM: service-account JWT (RS256) exchanged for an access token, then HTTP v1 send.
        var token = Assert.Single(calls, c => c.Path == "/token");
        var assertion = System.Web.HttpUtility.ParseQueryString(token.Body)["assertion"]!;
        Assert.Equal("urn:ietf:params:oauth:grant-type:jwt-bearer", System.Web.HttpUtility.ParseQueryString(token.Body)["grant_type"]);
        Assert.True(Verify(assertion, (data, sig) => _google.VerifyData(data, sig, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)));
        Assert.Equal("https://www.googleapis.com/auth/firebase.messaging", (string?)Part(assertion, 1)["scope"]);
        var fcm = calls.First(c => c.Path == "/v1/projects/marbots-test/messages:send" && c.Body.Contains("android-token-1"));
        Assert.Equal("Bearer ya29.fake", fcm.Headers["Authorization"]);
        var message = JsonNode.Parse(fcm.Body)!["message"]!;
        Assert.Equal("Approval needed", (string?)message["notification"]!["title"]);
        Assert.Equal("https://marbots.example/approvals", (string?)message["data"]!["link"]);
        Assert.Equal("HIGH", (string?)message["android"]!["priority"]);

        // APNs: ES256 provider token with the key id; alert payload; apns-topic = bundle id.
        var apns = Assert.Single(calls, c => c.Path == "/3/device/abc123");
        var jwt = apns.Headers["Authorization"]["bearer ".Length..];
        Assert.Equal("KEY123", (string?)Part(jwt, 0)["kid"]);
        Assert.Equal("TEAM456", (string?)Part(jwt, 1)["iss"]);
        Assert.True(Verify(jwt, (data, sig) => _apple.VerifyData(data, sig, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)));
        Assert.Equal("id.gravicode.marbots", apns.Headers["apns-topic"]);
        Assert.Equal("10", apns.Headers["apns-priority"]);
        Assert.Contains("run_command", (string?)JsonNode.Parse(apns.Body)!["aps"]!["alert"]!["body"]);

        // ntfy: plain POST to the topic.
        var ntfy = Assert.Single(calls, c => c.Path == "/marbots-team-8f3k2");
        Assert.Equal("Approval needed", ntfy.Headers["Title"]);
        Assert.Equal("high", ntfy.Headers["Priority"]);
        Assert.Equal("https://marbots.example/approvals", ntfy.Headers["Click"]);

        // FCM said UNREGISTERED: that device is gone.
        await WaitForAsync(_ => !push.ListAsync().Result.Any(d => d.Token == "gone-token"));
        Assert.DoesNotContain(await push.ListAsync(), d => d.Token == "gone-token");

        // A finished chat notifies devices subscribed to "completed" (not the approvals-only iPhone).
        while (_calls.TryDequeue(out _)) { }
        _sp.GetRequiredService<ModelRouter>().Mock.EnqueueText("Report is ready.");
        var engine = _sp.GetRequiredService<MarbotsEngine>();
        var thread = await engine.CreateThreadAsync("alice");
        await engine.WaitAsync((await engine.SendAsync(thread.Id, "make the report")).Id, TimeSpan.FromSeconds(20));
        var done = await WaitForAsync(c => c.Any(x => x.Path == "/marbots-team-8f3k2"));
        var finished = Assert.Single(done, c => c.Path == "/marbots-team-8f3k2");
        Assert.Equal("Report is ready.", finished.Body);
        Assert.Equal($"https://marbots.example/chat/{thread.Id}", finished.Headers["Click"]);
        await Task.Delay(300);
        Assert.DoesNotContain(_calls, c => c.Path.StartsWith("/3/device/"));
    }
}
