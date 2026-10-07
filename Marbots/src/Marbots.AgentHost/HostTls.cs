using System.Net.Http.Json;
using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Marbots.Abstractions;

namespace Marbots.AgentHost;

/// <summary>
/// Mutual TLS for the host: a P-256 key made on this computer (it never leaves it), a client certificate issued by the
/// server's host CA, renewal before expiry, and optional trust of a private CA for the server's own certificate.
/// </summary>
internal static class HostTls
{
    /// <summary>A new key and a CSR for it (the server sets the subject to the host id).</summary>
    public static (string KeyPem, string CsrPem) NewKeyAndCsr(string name)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest($"CN={Sanitize(name)}", key, HashAlgorithmName.SHA256);
        return (key.ExportPkcs8PrivateKeyPem(), req.CreateSigningRequestPem());
    }

    private static string Sanitize(string s) => new([.. s.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.')]);

    /// <summary>The client certificate with its key, in a form SChannel (Windows) can use for TLS client auth.</summary>
    public static X509Certificate2? ClientCertificate(HostConfig c)
    {
        if (string.IsNullOrEmpty(c.CertificatePem) || string.IsNullOrEmpty(c.KeyPem)) return null;
        using var pem = X509Certificate2.CreateFromPem(c.CertificatePem, c.KeyPem);
        // Ephemeral PEM keys cannot be used by SChannel; a PKCS#12 round trip gives the key a provider.
        return OperatingSystem.IsWindows() ? X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pkcs12), null) : X509Certificate2.CreateFromPem(c.CertificatePem, c.KeyPem);
    }

    public static DateTimeOffset? Expires(HostConfig c) =>
        string.IsNullOrEmpty(c.CertificatePem) ? null : X509Certificate2.CreateFromPem(c.CertificatePem).NotAfter.ToUniversalTime();

    /// <summary>Accepts the server certificate if the OS trusts it, or if it chains to the configured private CA.</summary>
    public static bool ValidateServer(X509Certificate? cert, SslPolicyErrors errors, string? serverCaPem)
    {
        if (errors == SslPolicyErrors.None) return true;
        if (cert is null || string.IsNullOrEmpty(serverCaPem) || errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch)) return false;
        using var ca = X509Certificate2.CreateFromPem(serverCaPem);
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(ca);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        return chain.Build(X509CertificateLoader.LoadCertificate(cert.GetRawCertData()));
    }

    public static HttpClient Http(HostConfig c, Uri baseUri)
    {
        var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, cert, _, errors) => ValidateServer(cert, errors, c.ServerCaPem) };
        if (ClientCertificate(c) is { } cert) handler.ClientCertificates.Add(cert);
        return new HttpClient(handler) { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(30) };
    }

    public static void Configure(ClientWebSocketOptions o, HostConfig c)
    {
        if (ClientCertificate(c) is { } cert) o.ClientCertificates.Add(cert);
        o.RemoteCertificateValidationCallback = (_, cert, _, errors) => ValidateServer(cert, errors, c.ServerCaPem);
    }

    /// <summary>Renews a certificate with less than a third of its life left; <paramref name="force"/> also gets one for hosts enrolled before mutual TLS.</summary>
    public static async Task<bool> RenewIfDueAsync(HostConfig c, bool force, CancellationToken ct)
    {
        if (!force && Expires(c) is { } expires)
        {
            var cert = X509Certificate2.CreateFromPem(c.CertificatePem!);
            var life = cert.NotAfter - cert.NotBefore;
            if (expires - DateTimeOffset.UtcNow > life / 3) return false;
        }
        else if (!force) return false;
        var (keyPem, csr) = NewKeyAndCsr(Environment.MachineName);
        using var http = Http(c, new Uri(c.Server));
        using var req = new HttpRequestMessage(HttpMethod.Post, "api/v1/hosts/renew") { Content = JsonContent.Create(new HostCertificateRenewal(csr), MarbotsJsonContext.Default.HostCertificateRenewal) };
        req.Headers.Add(HostProtocol.HostIdHeader, c.HostId);
        req.Headers.Add(HostProtocol.HostSecretHeader, c.Secret);
        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"Certificate renewal failed: HTTP {(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync(ct)}");
        var result = (await resp.Content.ReadFromJsonAsync(MarbotsJsonContext.Default.HostCertificateResult, ct))!;
        c.CertificatePem = result.Certificate;
        c.KeyPem = keyPem;
        c.HostCaPem = result.CaCertificate;
        c.Save();
        return true;
    }
}
