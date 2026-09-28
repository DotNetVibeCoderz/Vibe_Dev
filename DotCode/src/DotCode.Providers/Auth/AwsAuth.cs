using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DotCode.Abstractions;

namespace DotCode.Providers.Auth;

public sealed record AwsCredentials(string AccessKeyId, string SecretAccessKey, string? SessionToken, string Source, DateTimeOffset? Expiration = null);

/// <summary>AWS credentials (environment, shared credentials/config files with profiles, <c>credential_process</c>,
/// and the AWS CLI for SSO) plus Signature Version 4 request signing. Written from the public SigV4 specification.</summary>
public static class AwsAuth
{
    public static string? Region(ProviderConfig? config, string? profile = null) =>
        ConfigValue.Expand(config?.Region)
        ?? Env("AWS_REGION") ?? Env("AWS_DEFAULT_REGION")
        ?? ReadIni(ConfigPath, ProfileSection(profile ?? Profile(config), config: true))?.GetValueOrDefault("region");

    public static string Profile(ProviderConfig? config) => ConfigValue.Expand(config?.AwsProfile) ?? Env("AWS_PROFILE") ?? "default";

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static string CredentialsPath => Env("AWS_SHARED_CREDENTIALS_FILE") ?? Path.Combine(Home, ".aws", "credentials");
    private static string ConfigPath => Env("AWS_CONFIG_FILE") ?? Path.Combine(Home, ".aws", "config");

    /// <summary>Resolves credentials in the usual AWS order: environment, credentials file, config file
    /// (static keys or <c>credential_process</c>), then <c>aws configure export-credentials</c> (SSO, assumed roles).</summary>
    public static async Task<AwsCredentials?> ResolveAsync(ProviderConfig? config, CancellationToken ct)
    {
        if (Env("AWS_ACCESS_KEY_ID") is { } id && Env("AWS_SECRET_ACCESS_KEY") is { } secret)
            return new AwsCredentials(id, secret, Env("AWS_SESSION_TOKEN"), "environment");

        var profile = Profile(config);
        if (ReadIni(CredentialsPath, profile) is { } creds && creds.TryGetValue("aws_access_key_id", out var cid) && creds.TryGetValue("aws_secret_access_key", out var csecret))
            return new AwsCredentials(cid, csecret, creds.GetValueOrDefault("aws_session_token"), $"credentials file [{profile}]");

        var cfg = ReadIni(ConfigPath, ProfileSection(profile, config: true));
        if (cfg is not null && cfg.TryGetValue("aws_access_key_id", out var kid) && cfg.TryGetValue("aws_secret_access_key", out var ksecret))
            return new AwsCredentials(kid, ksecret, cfg.GetValueOrDefault("aws_session_token"), $"config file [{profile}]");
        if (cfg?.GetValueOrDefault("credential_process") is { Length: > 0 } process)
        {
            var space = process.IndexOf(' ');
            var output = await CliRunner.RunAsync(space > 0 ? process[..space] : process, space > 0 ? process[(space + 1)..] : "", ct).ConfigureAwait(false);
            if (ParseProcessOutput(output, "credential_process") is { } fromProcess) return fromProcess;
        }
        // SSO / role profiles: let the AWS CLI do the work.
        var exported = await CliRunner.RunAsync("aws", $"configure export-credentials --format process --profile {profile}", ct).ConfigureAwait(false);
        return ParseProcessOutput(exported, "aws cli");
    }

    private static AwsCredentials? ParseProcessOutput(string? json, string source)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var e = DotCodeJson.Parse(json);
            if (e.GetString("AccessKeyId") is not { } id || e.GetString("SecretAccessKey") is not { } secret) return null;
            DateTimeOffset? expires = DateTimeOffset.TryParse(e.GetString("Expiration"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var x) ? x : null;
            return new AwsCredentials(id, secret, e.GetString("SessionToken"), source, expires);
        }
        catch (JsonException) { return null; }
    }

    private static string ProfileSection(string profile, bool config) => config && profile != "default" ? "profile " + profile : profile;

    /// <summary>Minimal INI reader for ~/.aws files: [section] then key = value.</summary>
    internal static Dictionary<string, string>? ReadIni(string path, string section)
    {
        if (!File.Exists(path)) return null;
        Dictionary<string, string>? current = null;
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is '#' or ';') continue;
            if (line[0] == '[' && line[^1] == ']')
            {
                if (current is not null) return current;
                if (line[1..^1].Trim() == section) current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                continue;
            }
            if (current is null) continue;
            var eq = line.IndexOf('=');
            if (eq > 0) current[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        return current;
    }

    // ---------------------------------------------------------------- Signature Version 4

    /// <summary>Signs a request with AWS SigV4 (headers: host, x-amz-date, x-amz-content-sha256, x-amz-security-token).</summary>
    public static void Sign(HttpRequestMessage request, byte[] body, AwsCredentials credentials, string region, string service, DateTimeOffset? now = null, bool includeContentSha256 = true)
    {
        var time = (now ?? DateTimeOffset.UtcNow).ToUniversalTime();
        var amzDate = time.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var date = amzDate[..8];
        var uri = request.RequestUri!;
        var payloadHash = Hex(SHA256.HashData(body));

        request.Headers.Remove("x-amz-date");
        request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        if (includeContentSha256) request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);
        if (credentials.SessionToken is { Length: > 0 } token) request.Headers.TryAddWithoutValidation("x-amz-security-token", token);

        var headers = new SortedDictionary<string, string>(StringComparer.Ordinal) { ["host"] = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}" };
        foreach (var h in request.Headers) headers[h.Key.ToLowerInvariant()] = string.Join(",", h.Value.Select(v => v.Trim()));
        if (request.Content is not null)
            foreach (var h in request.Content.Headers) headers[h.Key.ToLowerInvariant()] = string.Join(",", h.Value.Select(v => v.Trim()));
        headers.Remove("authorization");
        headers.Remove("user-agent");

        var signedHeaders = string.Join(';', headers.Keys);
        var canonical = new StringBuilder()
            .Append(request.Method.Method.ToUpperInvariant()).Append('\n')
            .Append(CanonicalPath(uri.AbsolutePath, service)).Append('\n')
            .Append(CanonicalQuery(uri.Query)).Append('\n');
        foreach (var (k, v) in headers) canonical.Append(k).Append(':').Append(v).Append('\n');
        canonical.Append('\n').Append(signedHeaders).Append('\n').Append(payloadHash);

        var scope = $"{date}/{region}/{service}/aws4_request";
        var stringToSign = $"AWS4-HMAC-SHA256\n{amzDate}\n{scope}\n{Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))}";
        var key = Hmac(Hmac(Hmac(Hmac(Encoding.UTF8.GetBytes("AWS4" + credentials.SecretAccessKey), date), region), service), "aws4_request");
        var signature = Hex(Hmac(key, stringToSign));
        request.Headers.TryAddWithoutValidation("Authorization", $"AWS4-HMAC-SHA256 Credential={credentials.AccessKeyId}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}");
    }

    /// <summary>Canonical URI: every path segment URI-encoded twice for all services except S3 (the request path
    /// is already encoded once, so it is encoded once more here).</summary>
    public static string CanonicalPath(string absolutePath, string service)
    {
        if (string.IsNullOrEmpty(absolutePath)) return "/";
        if (service == "s3") return absolutePath;
        return string.Join('/', absolutePath.Split('/').Select(UriEncode));
    }

    private static string CanonicalQuery(string query)
    {
        if (string.IsNullOrEmpty(query) || query == "?") return "";
        return string.Join('&', query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p =>
            {
                var eq = p.IndexOf('=');
                var k = Uri.UnescapeDataString(eq < 0 ? p : p[..eq]);
                var v = eq < 0 ? "" : Uri.UnescapeDataString(p[(eq + 1)..]);
                return (Key: UriEncode(k), Value: UriEncode(v));
            })
            .OrderBy(p => p.Key, StringComparer.Ordinal).ThenBy(p => p.Value, StringComparer.Ordinal)
            .Select(p => p.Key + "=" + p.Value));
    }

    /// <summary>RFC 3986 encoding with SigV4's unreserved set (A-Z a-z 0-9 - _ . ~).</summary>
    internal static string UriEncode(string value)
    {
        var sb = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var c = (char)b;
            if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_' or '.' or '~') sb.Append(c);
            else sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    private static byte[] Hmac(byte[] key, string data) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));
    private static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);
    private static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;
}
