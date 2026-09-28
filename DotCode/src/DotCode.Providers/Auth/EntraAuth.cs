using System.Globalization;
using System.Text.Json;
using DotCode.Abstractions;
using DotCode.Providers.Http;

namespace DotCode.Providers.Auth;

/// <summary>Microsoft Entra ID (Azure AD) tokens for Azure OpenAI / Azure AI Foundry, like DefaultAzureCredential:
/// client secret (AZURE_CLIENT_ID + AZURE_TENANT_ID + AZURE_CLIENT_SECRET), workload identity (AZURE_FEDERATED_TOKEN_FILE),
/// managed identity (App Service / Container Apps endpoint or the VM IMDS), then the Azure CLI (<c>az login</c>).</summary>
public sealed class EntraAuth
{
    public const string CognitiveServicesScope = "https://cognitiveservices.azure.com/.default";
    private readonly TokenCache _cache;
    private readonly string _scope;
    private readonly string? _tenant, _clientId, _clientSecret;

    public EntraAuth(ProviderConfig? config = null)
    {
        _scope = ConfigValue.Expand(config?.Scope) ?? CognitiveServicesScope;
        _tenant = ConfigValue.Expand(config?.TenantId) ?? Env("AZURE_TENANT_ID");
        _clientId = ConfigValue.Expand(config?.ClientId) ?? Env("AZURE_CLIENT_ID");
        _clientSecret = ConfigValue.Expand(config?.ClientSecret) ?? Env("AZURE_CLIENT_SECRET");
        _cache = new TokenCache(FetchAsync);
    }

    public Task<AccessToken> GetTokenAsync(CancellationToken ct) => _cache.GetAsync(ct);
    public void Invalidate() => _cache.Invalidate();

    private string Resource => _scope.EndsWith("/.default", StringComparison.Ordinal) ? _scope[..^"/.default".Length] : _scope;

    private async Task<AccessToken> FetchAsync(CancellationToken ct)
    {
        var authority = (Env("AZURE_AUTHORITY_HOST") ?? "https://login.microsoftonline.com").TrimEnd('/');
        if (_tenant is not null && _clientId is not null && _clientSecret is not null)
            return await TokenRequestAsync($"{authority}/{_tenant}/oauth2/v2.0/token", new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = _clientId,
                ["client_secret"] = _clientSecret,
                ["scope"] = _scope,
            }, "client secret", ct).ConfigureAwait(false);

        if (_tenant is not null && _clientId is not null && Env("AZURE_FEDERATED_TOKEN_FILE") is { } federated && File.Exists(federated))
            return await TokenRequestAsync($"{authority}/{_tenant}/oauth2/v2.0/token", new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = _clientId,
                ["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
                ["client_assertion"] = (await File.ReadAllTextAsync(federated, ct).ConfigureAwait(false)).Trim(),
                ["scope"] = _scope,
            }, "workload identity", ct).ConfigureAwait(false);

        if (await ManagedIdentityAsync(ct).ConfigureAwait(false) is { } mi) return mi;

        if (await CliRunner.RunAsync("az", $"account get-access-token --resource {Resource} --output json", ct).ConfigureAwait(false) is { Length: > 0 } cli)
        {
            var json = DotCodeJson.Parse(cli);
            var expires = json.GetProp("expires_on") is { ValueKind: JsonValueKind.Number } n ? DateTimeOffset.FromUnixTimeSeconds(n.GetInt64())
                : DateTimeOffset.TryParse(json.GetString("expiresOn"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var e) ? e : DateTimeOffset.UtcNow.AddMinutes(30);
            return new AccessToken(json.GetString("accessToken") ?? "", expires, "Azure CLI");
        }

        throw new ModelProviderException("azure", "auth",
            "No Microsoft Entra credentials found. Run `az login`, set AZURE_TENANT_ID + AZURE_CLIENT_ID + AZURE_CLIENT_SECRET, or use a managed identity.", false);
    }

    private async Task<AccessToken?> ManagedIdentityAsync(CancellationToken ct)
    {
        try
        {
            HttpRequestMessage req;
            // App Service / Functions / Container Apps expose IDENTITY_ENDPOINT + IDENTITY_HEADER.
            if (Env("IDENTITY_ENDPOINT") is { } endpoint && Env("IDENTITY_HEADER") is { } header)
            {
                req = new HttpRequestMessage(HttpMethod.Get, $"{endpoint}?api-version=2019-08-01&resource={Uri.EscapeDataString(Resource)}{(_clientId is null ? "" : "&client_id=" + _clientId)}");
                req.Headers.TryAddWithoutValidation("X-IDENTITY-HEADER", header);
            }
            else if (Env("AZURE_USE_IMDS") == "1" || Env("MSI_ENDPOINT") is not null || File.Exists("/var/lib/waagent/ovf-env.xml") || Env("AZURE_POD_IDENTITY") is not null)
            {
                req = new HttpRequestMessage(HttpMethod.Get, $"http://169.254.169.254/metadata/identity/oauth2/token?api-version=2018-02-01&resource={Uri.EscapeDataString(Resource)}{(_clientId is null ? "" : "&client_id=" + _clientId)}");
                req.Headers.TryAddWithoutValidation("Metadata", "true");
            }
            else return null;
            using (req)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                using var resp = await ProviderHttp.GetClient().SendAsync(req, timeout.Token).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return null;
                return Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false), "managed identity");
            }
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException) { return null; }
    }

    private static async Task<AccessToken> TokenRequestAsync(string uri, Dictionary<string, string> form, string source, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new FormUrlEncodedContent(form) };
        using var resp = await ProviderHttp.GetClient().SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new ModelProviderException("azure", "auth", $"Microsoft Entra token request failed ({(int)resp.StatusCode}): {body}", false);
        return Parse(body, source);
    }

    private static AccessToken Parse(string body, string source)
    {
        var json = DotCodeJson.Parse(body);
        var token = json.GetString("access_token") ?? throw new ModelProviderException("azure", "auth", "Entra token response had no access_token", false);
        // expires_in is a number (v2 endpoint) or a string (managed identity); expires_on is epoch seconds.
        long? seconds = json.GetProp("expires_in") is { } ei ? ei.ValueKind == JsonValueKind.Number ? ei.GetInt64() : long.TryParse(ei.GetString(), out var s) ? s : null : null;
        var expires = seconds is { } sec ? DateTimeOffset.UtcNow.AddSeconds(sec)
            : json.GetString("expires_on") is { } eo && long.TryParse(eo, out var epoch) ? DateTimeOffset.FromUnixTimeSeconds(epoch)
            : DateTimeOffset.UtcNow.AddMinutes(30);
        return new AccessToken(token, expires, source);
    }

    private static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;
}
