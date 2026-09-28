using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DotCode.Abstractions;
using DotCode.Providers.Http;

namespace DotCode.Providers.Auth;

/// <summary>Google OAuth access tokens for Vertex AI from Application Default Credentials: an explicit token
/// (GOOGLE_OAUTH_ACCESS_TOKEN), a service-account key (JWT bearer grant, RS256), an authorized-user file from
/// <c>gcloud auth application-default login</c> (refresh token), the GCE/Cloud Run metadata server, or the gcloud CLI.</summary>
public sealed class GoogleAuth
{
    public const string Scope = "https://www.googleapis.com/auth/cloud-platform";
    private readonly TokenCache _cache;
    private readonly string? _credentialsFile;

    public GoogleAuth(string? credentialsFile = null)
    {
        _credentialsFile = credentialsFile;
        _cache = new TokenCache(FetchAsync);
    }

    public Task<AccessToken> GetTokenAsync(CancellationToken ct) => _cache.GetAsync(ct);
    public void Invalidate() => _cache.Invalidate();

    /// <summary>ADC file: explicit path, GOOGLE_APPLICATION_CREDENTIALS, or gcloud's well-known location.</summary>
    public string? CredentialsPath
    {
        get
        {
            if (ConfigValue.Expand(_credentialsFile) is { Length: > 0 } explicitPath) return explicitPath;
            if (Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS") is { Length: > 0 } env) return env;
            var wellKnown = OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "gcloud", "application_default_credentials.json")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "gcloud", "application_default_credentials.json");
            return File.Exists(wellKnown) ? wellKnown : null;
        }
    }

    /// <summary>Project from the credentials file (service account project_id / quota_project_id), if any.</summary>
    public string? ProjectFromCredentials()
    {
        try
        {
            if (CredentialsPath is not { } path || !File.Exists(path)) return null;
            var json = DotCodeJson.Parse(File.ReadAllText(path));
            return json.GetString("project_id") ?? json.GetString("quota_project_id");
        }
        catch (Exception e) when (e is IOException or JsonException) { return null; }
    }

    private async Task<AccessToken> FetchAsync(CancellationToken ct)
    {
        if (Environment.GetEnvironmentVariable("GOOGLE_OAUTH_ACCESS_TOKEN") is { Length: > 0 } explicitToken)
            return new AccessToken(explicitToken, DateTimeOffset.UtcNow.AddMinutes(50), "GOOGLE_OAUTH_ACCESS_TOKEN");

        if (CredentialsPath is { } path && File.Exists(path))
        {
            var json = DotCodeJson.Parse(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false));
            switch (json.GetString("type"))
            {
                case "service_account":
                    return await ServiceAccountAsync(json, ct).ConfigureAwait(false);
                case "authorized_user":
                    return await TokenRequestAsync("https://oauth2.googleapis.com/token", new Dictionary<string, string>
                    {
                        ["grant_type"] = "refresh_token",
                        ["client_id"] = json.GetString("client_id") ?? "",
                        ["client_secret"] = json.GetString("client_secret") ?? "",
                        ["refresh_token"] = json.GetString("refresh_token") ?? "",
                    }, "application default credentials (user)", ct).ConfigureAwait(false);
                default:
                    throw new ModelProviderException("vertex", "auth", $"Unsupported Google credentials type '{json.GetString("type")}' in {path} (supported: service_account, authorized_user).", false);
            }
        }

        if (await MetadataServerAsync(ct).ConfigureAwait(false) is { } metadata) return metadata;

        if (await CliRunner.RunAsync("gcloud", "auth print-access-token", ct).ConfigureAwait(false) is { Length: > 0 } gcloud)
            return new AccessToken(gcloud.Split('\n')[^1].Trim(), DateTimeOffset.UtcNow.AddMinutes(45), "gcloud CLI");

        throw new ModelProviderException("vertex", "auth",
            "No Google credentials found. Run `gcloud auth application-default login`, set GOOGLE_APPLICATION_CREDENTIALS to a service-account key, or set GOOGLE_OAUTH_ACCESS_TOKEN.", false);
    }

    /// <summary>OAuth 2.0 JWT bearer grant signed with the service account's RSA key.</summary>
    private static async Task<AccessToken> ServiceAccountAsync(JsonElement sa, CancellationToken ct)
    {
        var tokenUri = sa.GetString("token_uri") ?? "https://oauth2.googleapis.com/token";
        var assertion = CreateJwtAssertion(sa.GetString("client_email") ?? "", sa.GetString("private_key") ?? "", tokenUri, sa.GetString("private_key_id"), DateTimeOffset.UtcNow);
        return await TokenRequestAsync(tokenUri, new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
            ["assertion"] = assertion,
        }, "service account " + sa.GetString("client_email"), ct).ConfigureAwait(false);
    }

    public static string CreateJwtAssertion(string clientEmail, string privateKeyPem, string audience, string? keyId, DateTimeOffset now)
    {
        var header = DotCodeJson.Build(w =>
        {
            w.WriteStartObject();
            w.WriteString("alg", "RS256");
            w.WriteString("typ", "JWT");
            if (keyId is not null) w.WriteString("kid", keyId);
            w.WriteEndObject();
        }).GetRawText();
        var iat = now.ToUnixTimeSeconds();
        var claims = DotCodeJson.Build(w =>
        {
            w.WriteStartObject();
            w.WriteString("iss", clientEmail);
            w.WriteString("scope", Scope);
            w.WriteString("aud", audience);
            w.WriteNumber("iat", iat);
            w.WriteNumber("exp", iat + 3600);
            w.WriteEndObject();
        }).GetRawText();
        var signingInput = Base64Url(Encoding.UTF8.GetBytes(header)) + "." + Base64Url(Encoding.UTF8.GetBytes(claims));
        using var rsa = RSA.Create();
        rsa.ImportFromPem(privateKeyPem);
        var signature = rsa.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return signingInput + "." + Base64Url(signature);
    }

    internal static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static async Task<AccessToken?> MetadataServerAsync(CancellationToken ct)
    {
        // Only on Google Cloud compute (or when explicitly pointed at a metadata server) to avoid slow probes elsewhere.
        var host = Environment.GetEnvironmentVariable("GCE_METADATA_HOST");
        var onGoogleCloud = host is not null
            || Environment.GetEnvironmentVariable("K_SERVICE") is not null                                      // Cloud Run / Functions
            || (File.Exists("/sys/class/dmi/id/product_name") && File.ReadAllText("/sys/class/dmi/id/product_name").Contains("Google", StringComparison.Ordinal)); // GCE / GKE
        if (!onGoogleCloud) return null;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"http://{host ?? "metadata.google.internal"}/computeMetadata/v1/instance/service-accounts/default/token");
            req.Headers.TryAddWithoutValidation("Metadata-Flavor", "Google");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            using var resp = await ProviderHttp.GetClient().SendAsync(req, timeout.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            return Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false), "GCE metadata server");
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException) { return null; }
    }

    private static async Task<AccessToken> TokenRequestAsync(string uri, Dictionary<string, string> form, string source, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new FormUrlEncodedContent(form) };
        using var resp = await ProviderHttp.GetClient().SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new ModelProviderException("vertex", "auth", $"Google token request failed ({(int)resp.StatusCode}): {body}", false);
        return Parse(body, source);
    }

    private static AccessToken Parse(string body, string source)
    {
        var json = DotCodeJson.Parse(body);
        var token = json.GetString("access_token") ?? throw new ModelProviderException("vertex", "auth", "Google token response had no access_token", false);
        var expiresIn = json.GetInt("expires_in") ?? 3600;
        return new AccessToken(token, DateTimeOffset.UtcNow.AddSeconds(expiresIn), source);
    }
}
