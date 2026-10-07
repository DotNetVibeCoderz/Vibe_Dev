using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Marbots.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Marbots.Runtime;

/// <summary>
/// Remote push settings. Credentials are secret names (Marbots:Secrets / environment / the UI secret store), never values.
/// </summary>
public sealed class MarbotsPushOptions
{
    /// <summary>Secret holding the Firebase service-account JSON (project_id, client_email, private_key).</summary>
    public string FcmServiceAccountSecret { get; set; } = "FCM_SERVICE_ACCOUNT";
    /// <summary>Secret holding the APNs auth key (.p8 PEM).</summary>
    public string ApnsKeySecret { get; set; } = "APNS_AUTH_KEY";
    public string? ApnsKeyId { get; set; }
    public string? ApnsTeamId { get; set; }
    /// <summary>The app's bundle id (apns-topic).</summary>
    public string? ApnsBundleId { get; set; }
    public bool ApnsProduction { get; set; } = true;
    /// <summary>ntfy server (self-hosted or https://ntfy.sh).</summary>
    public string NtfyServer { get; set; } = "https://ntfy.sh";
    /// <summary>Optional secret with an ntfy access token.</summary>
    public string NtfyTokenSecret { get; set; } = "NTFY_TOKEN";
    /// <summary>Public URL of this server, used for "open" links in notifications.</summary>
    public string? PublicUrl { get; set; }

    // Endpoint overrides (tests, proxies, regional endpoints).
    public string FcmBaseUrl { get; set; } = "https://fcm.googleapis.com";
    public string? ApnsBaseUrl { get; set; }
}

public sealed record PushMessage(string Title, string Body, string Topic, string? ThreadId, string? Link, bool Urgent);

/// <summary>Sends notifications to registered devices when bots need approval or finish chat tasks.</summary>
public sealed class PushService(
    IDocumentStore<PushDevice> devices,
    IDocumentStore<TaskRecord> tasks,
    IEventBus bus,
    ISecretProvider secrets,
    IHttpClientFactory httpFactory,
    MarbotsOptions options,
    ILogger<PushService> log) : IHostedService
{
    public const string HttpClientName = "marbots-push";
    private IDisposable? _subscription;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private (string Token, DateTimeOffset Expires)? _fcmToken;
    private (string Jwt, DateTimeOffset Issued)? _apnsJwt;

    private MarbotsPushOptions P => options.Push;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = bus.Subscribe(OnEvent);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        return Task.CompletedTask;
    }

    public async Task<PushDevice> RegisterAsync(RegisterPushDeviceRequest req, CancellationToken ct = default)
    {
        var platform = (req.Platform ?? "").Trim().ToLowerInvariant();
        if (!PushPlatforms.All.Contains(platform)) throw new ArgumentException($"Platform must be one of: {string.Join(", ", PushPlatforms.All)}.");
        var token = (req.Token ?? "").Trim();
        if (token.Length is < 3 or > 4096) throw new ArgumentException("A device token (or ntfy topic) is required.");
        if (platform == PushPlatforms.Ntfy && !System.Text.RegularExpressions.Regex.IsMatch(token, "^[A-Za-z0-9_-]{1,64}$"))
            throw new ArgumentException("ntfy topics may contain letters, digits, '-' and '_' (max 64). Use a long random topic: anyone who knows it can read it.");
        var topics = (req.Topics is { Count: > 0 } t ? t : [.. PushTopics.All]).Where(PushTopics.All.Contains).Distinct().ToList();
        // One record per platform+token: registering again updates it.
        var id = "dev_" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(platform + ":" + token)))[..20];
        var device = await devices.GetAsync(id, ct) ?? new PushDevice { Id = id };
        device.Platform = platform;
        device.Token = token;
        device.Name = string.IsNullOrWhiteSpace(req.Name) ? platform : req.Name.Trim();
        device.Topics = topics;
        await devices.UpsertAsync(device, ct);
        return device;
    }

    public PushConfig Config() => new(
        secrets.Get(P.FcmServiceAccountSecret) is { Length: > 0 },
        secrets.Get(P.ApnsKeySecret) is { Length: > 0 } && P.ApnsKeyId is not null && P.ApnsTeamId is not null && P.ApnsBundleId is not null,
        P.NtfyServer.TrimEnd('/'));

    public Task<IReadOnlyList<PushDevice>> ListAsync(CancellationToken ct = default) => devices.ListAsync(ct);
    public Task<bool> RemoveAsync(string id, CancellationToken ct = default) => devices.DeleteAsync(id, ct);

    public async Task<PushTestResult> TestAsync(CancellationToken ct = default)
    {
        var msg = new PushMessage("Marbots", "Test notification — push is working.", PushTopics.Completed, null, Link(null), false);
        return await DispatchAsync(msg, all: true, ct);
    }

    private void OnEvent(AgentEvent e)
    {
        if (e.Type == EventTypes.ApprovalRequested)
            _ = Task.Run(() => NotifyAsync(new PushMessage("Approval needed", $"{e.BotId}: {e.Message}", PushTopics.Approvals, e.ThreadId, Link("/approvals"), true)));
        else if (e.Type == EventTypes.TaskStateChanged && e.TaskId is not null && e.Data is "Completed" or "Failed" or "Cancelled" or "TimedOut")
            _ = Task.Run(() => NotifyTaskAsync(e.TaskId, e.Data!));
    }

    private async Task NotifyTaskAsync(string taskId, string state)
    {
        var task = await tasks.GetAsync(taskId);
        // Only chats people started (not delegated sub-tasks, not channel conversations that already get a reply).
        if (task is null || task.Depth > 0 || task.AssignedBy.StartsWith("channel:", StringComparison.Ordinal)) return;
        var ok = state == "Completed";
        var body = AgentRuntime.Preview(ok ? task.Result : task.Error ?? state, 180);
        await NotifyAsync(new PushMessage(ok ? $"{task.BotId} finished" : $"{task.BotId}: {state}", body,
            ok ? PushTopics.Completed : PushTopics.Failed, task.ThreadId, Link(task.ThreadId is null ? null : "/chat/" + task.ThreadId), !ok));
    }

    private async Task NotifyAsync(PushMessage msg)
    {
        try { await DispatchAsync(msg, all: false, CancellationToken.None); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { log.LogWarning(ex, "Push dispatch failed"); }
    }

    private string? Link(string? path)
    {
        if (P.PublicUrl is not { Length: > 0 } baseUrl) return null;
        var root = Tenants.ServerUrlFor(baseUrl, Tenants.Default);
        return root + (path ?? "/");
    }

    private async Task<PushTestResult> DispatchAsync(PushMessage msg, bool all, CancellationToken ct)
    {
        var targets = (await devices.ListAsync(ct)).Where(d => all || d.Topics.Contains(msg.Topic)).ToList();
        int sent = 0, failed = 0;
        var errors = new List<string>();
        foreach (var d in targets)
        {
            try
            {
                var gone = d.Platform switch
                {
                    PushPlatforms.Fcm => await SendFcmAsync(d, msg, ct),
                    PushPlatforms.Apns => await SendApnsAsync(d, msg, ct),
                    _ => await SendNtfyAsync(d, msg, ct),
                };
                if (gone)
                {
                    // The platform says this token is no longer valid.
                    await devices.DeleteAsync(d.Id, ct);
                    failed++;
                    errors.Add($"{d.Name}: token expired, device removed");
                    continue;
                }
                d.LastSentAt = DateTimeOffset.UtcNow;
                d.LastError = null;
                sent++;
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or JsonException or CryptographicException or ArgumentException or TaskCanceledException)
            {
                d.LastError = ex.Message;
                failed++;
                errors.Add($"{d.Name}: {ex.Message}");
                log.LogWarning("Push to {Device} ({Platform}) failed: {Error}", d.Name, d.Platform, ex.Message);
            }
            await devices.UpsertAsync(d, ct);
        }
        return new PushTestResult(sent, failed, errors);
    }

    private HttpClient Http() => httpFactory.CreateClient(HttpClientName);

    // ---------------- ntfy ----------------

    private async Task<bool> SendNtfyAsync(PushDevice d, PushMessage m, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{P.NtfyServer.TrimEnd('/')}/{d.Token}") { Content = new StringContent(m.Body, Encoding.UTF8, "text/plain") };
        // ntfy headers are ASCII; RFC 2047 encoding keeps non-ASCII titles intact.
        req.Headers.TryAddWithoutValidation("Title", m.Title.All(c => c < 128) ? m.Title : $"=?UTF-8?B?{Convert.ToBase64String(Encoding.UTF8.GetBytes(m.Title))}?=");
        req.Headers.TryAddWithoutValidation("Priority", m.Urgent ? "high" : "default");
        req.Headers.TryAddWithoutValidation("Tags", m.Topic == PushTopics.Approvals ? "raised_hand" : m.Topic == PushTopics.Failed ? "warning" : "white_check_mark");
        if (m.Link is not null) req.Headers.TryAddWithoutValidation("Click", m.Link);
        if (secrets.Get(P.NtfyTokenSecret) is { Length: > 0 } token) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var resp = await Http().SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"ntfy returned {(int)resp.StatusCode}");
        return false;
    }

    // ---------------- FCM HTTP v1 ----------------

    private async Task<bool> SendFcmAsync(PushDevice d, PushMessage m, CancellationToken ct)
    {
        var account = secrets.Get(P.FcmServiceAccountSecret) is { Length: > 0 } json
            ? JsonNode.Parse(json) ?? throw new InvalidOperationException("FCM service account is not JSON.")
            : throw new InvalidOperationException($"Secret {P.FcmServiceAccountSecret} (Firebase service-account JSON) is not set.");
        var project = (string?)account["project_id"] ?? throw new InvalidOperationException("project_id missing in the service account.");
        var token = await FcmAccessTokenAsync(account, ct);
        var data = new JsonObject { ["topic"] = m.Topic };
        if (m.ThreadId is not null) data["threadId"] = m.ThreadId;
        if (m.Link is not null) data["link"] = m.Link;
        var body = new JsonObject
        {
            ["message"] = new JsonObject
            {
                ["token"] = d.Token,
                ["notification"] = new JsonObject { ["title"] = m.Title, ["body"] = m.Body },
                ["data"] = data,
                ["android"] = new JsonObject { ["priority"] = m.Urgent ? "HIGH" : "NORMAL" },
            },
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{P.FcmBaseUrl.TrimEnd('/')}/v1/projects/{Uri.EscapeDataString(project)}/messages:send")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var resp = await Http().SendAsync(req, ct);
        if (resp.IsSuccessStatusCode) return false;
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (resp.StatusCode == HttpStatusCode.NotFound || text.Contains("UNREGISTERED", StringComparison.Ordinal)) return true;
        throw new HttpRequestException($"FCM returned {(int)resp.StatusCode}: {AgentRuntime.Preview(text, 200)}");
    }

    /// <summary>OAuth 2.0 JWT-bearer grant with the service account (RS256), cached until shortly before expiry.</summary>
    private async Task<string> FcmAccessTokenAsync(JsonNode account, CancellationToken ct)
    {
        await _tokenLock.WaitAsync(ct);
        try
        {
            if (_fcmToken is { } cached && cached.Expires > DateTimeOffset.UtcNow.AddMinutes(5)) return cached.Token;
            var email = (string?)account["client_email"] ?? throw new InvalidOperationException("client_email missing in the service account.");
            var tokenUri = (string?)account["token_uri"] ?? "https://oauth2.googleapis.com/token";
            using var rsa = RSA.Create();
            rsa.ImportFromPem((string?)account["private_key"] ?? throw new InvalidOperationException("private_key missing in the service account."));
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var jwt = Jwt.Sign(new JsonObject { ["alg"] = "RS256", ["typ"] = "JWT" },
                new JsonObject { ["iss"] = email, ["scope"] = "https://www.googleapis.com/auth/firebase.messaging", ["aud"] = tokenUri, ["iat"] = now, ["exp"] = now + 3600 },
                data => rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
            using var resp = await Http().PostAsync(tokenUri, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer", ["assertion"] = jwt,
            }), ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"Google token endpoint returned {(int)resp.StatusCode}: {AgentRuntime.Preview(text, 200)}");
            var doc = JsonNode.Parse(text)!;
            var access = (string?)doc["access_token"] ?? throw new InvalidOperationException("No access_token from the token endpoint.");
            _fcmToken = (access, DateTimeOffset.UtcNow.AddSeconds((int?)doc["expires_in"] ?? 3600));
            return access;
        }
        finally { _tokenLock.Release(); }
    }

    // ---------------- APNs ----------------

    private async Task<bool> SendApnsAsync(PushDevice d, PushMessage m, CancellationToken ct)
    {
        if (P.ApnsKeyId is null || P.ApnsTeamId is null || P.ApnsBundleId is null)
            throw new InvalidOperationException("Set Marbots:Push:ApnsKeyId, ApnsTeamId and ApnsBundleId for APNs.");
        var jwt = ApnsJwt();
        var baseUrl = P.ApnsBaseUrl ?? (P.ApnsProduction ? "https://api.push.apple.com" : "https://api.sandbox.push.apple.com");
        var payload = new JsonObject
        {
            ["aps"] = new JsonObject
            {
                ["alert"] = new JsonObject { ["title"] = m.Title, ["body"] = m.Body },
                ["sound"] = "default",
                ["thread-id"] = m.ThreadId ?? "marbots",
                ["interruption-level"] = m.Urgent ? "time-sensitive" : "active",
            },
            ["topic"] = m.Topic,
        };
        if (m.Link is not null) payload["link"] = m.Link;
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/3/device/{Uri.EscapeDataString(d.Token)}")
        {
            // APNs speaks HTTP/2 only; the fallback lets test doubles answer over HTTP/1.1.
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("bearer", jwt);
        req.Headers.TryAddWithoutValidation("apns-topic", P.ApnsBundleId);
        req.Headers.TryAddWithoutValidation("apns-push-type", "alert");
        req.Headers.TryAddWithoutValidation("apns-priority", m.Urgent ? "10" : "5");
        using var resp = await Http().SendAsync(req, ct);
        if (resp.IsSuccessStatusCode) return false;
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (resp.StatusCode == HttpStatusCode.Gone || text.Contains("BadDeviceToken", StringComparison.Ordinal) || text.Contains("Unregistered", StringComparison.Ordinal)) return true;
        throw new HttpRequestException($"APNs returned {(int)resp.StatusCode}: {AgentRuntime.Preview(text, 200)}");
    }

    /// <summary>ES256 provider token; Apple accepts one for up to an hour, so it is reused for 50 minutes.</summary>
    private string ApnsJwt()
    {
        if (_apnsJwt is { } cached && DateTimeOffset.UtcNow - cached.Issued < TimeSpan.FromMinutes(50)) return cached.Jwt;
        var pem = secrets.Get(P.ApnsKeySecret) is { Length: > 0 } k ? k : throw new InvalidOperationException($"Secret {P.ApnsKeySecret} (APNs .p8 key) is not set.");
        using var ec = ECDsa.Create();
        ec.ImportFromPem(pem);
        var jwt = Jwt.Sign(new JsonObject { ["alg"] = "ES256", ["kid"] = P.ApnsKeyId }, new JsonObject { ["iss"] = P.ApnsTeamId, ["iat"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() },
            data => ec.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        _apnsJwt = (jwt, DateTimeOffset.UtcNow);
        return jwt;
    }
}

/// <summary>Minimal compact JWS (header.payload.signature) for service tokens.</summary>
public static class Jwt
{
    public static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] FromB64(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }

    public static string Sign(JsonObject header, JsonObject payload, Func<byte[], byte[]> sign)
    {
        var input = B64(Encoding.UTF8.GetBytes(header.ToJsonString())) + "." + B64(Encoding.UTF8.GetBytes(payload.ToJsonString()));
        return input + "." + B64(sign(Encoding.ASCII.GetBytes(input)));
    }
}
