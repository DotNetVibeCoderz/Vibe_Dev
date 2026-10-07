using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;

namespace Marbots.Runtime;

/// <summary>Agent-host transport security (mutual TLS).</summary>
public sealed class HostSecurityOptions
{
    /// <summary>Refuse host connections without a valid client certificate (in addition to the host secret).</summary>
    public bool RequireClientCertificate { get; set; }
    /// <summary>Ask for client certificates in the TLS handshake (Kestrel HTTPS). On by default when they are required.</summary>
    public bool? AcceptTlsClientCertificates { get; set; }
    /// <summary>
    /// When TLS ends at a reverse proxy, the header carrying the client certificate (PEM, URL-encoded), e.g.
    /// X-Client-Cert (nginx $ssl_client_escaped_cert) or X-ARR-ClientCert (Azure). Only set this behind a proxy that
    /// strips the header from client requests.
    /// </summary>
    public string? ClientCertificateHeader { get; set; }
    /// <summary>Lifetime of issued host certificates; hosts renew when less than a third is left.</summary>
    public int CertificateDays { get; set; } = 365;
}

/// <summary>
/// A small certificate authority per tenant for agent hosts: it signs a host's CSR at enrollment (and renewal) with
/// CN = host id and the clientAuth usage. The CA key lives in the data directory, encrypted with Data Protection.
/// Only the latest certificate of a host is accepted (its thumbprint is stored), so renewal and removal revoke old ones.
/// </summary>
public sealed class HostCertificateAuthority(MarbotsOptions options, IDataProtectionProvider dataProtection)
{
    private readonly Lock _lock = new();
    private X509Certificate2? _ca;
    private readonly IDataProtector _protector = dataProtection.CreateProtector("Marbots.HostCA.v1");

    private string CertPath => options.DataPath("host-ca.crt");
    private string KeyPath => options.DataPath("host-ca.key");

    /// <summary>The CA certificate (public), created on first use.</summary>
    public X509Certificate2 Certificate
    {
        get
        {
            lock (_lock) return _ca ??= LoadOrCreate();
        }
    }

    public string CertificatePem => Certificate.ExportCertificatePem();

    private X509Certificate2 LoadOrCreate()
    {
        if (File.Exists(CertPath) && File.Exists(KeyPath))
        {
            var cert = X509Certificate2.CreateFromPem(File.ReadAllText(CertPath), _protector.Unprotect(File.ReadAllText(KeyPath)));
            if (cert.NotAfter > DateTime.UtcNow.AddDays(30)) return cert;
        }
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest($"CN=Marbots Host CA ({options.TenantId}), O=Marbots", key, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        var ca = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(10));
        Directory.CreateDirectory(Path.GetDirectoryName(CertPath)!);
        File.WriteAllText(CertPath, ca.ExportCertificatePem());
        File.WriteAllText(KeyPath, _protector.Protect(key.ExportPkcs8PrivateKeyPem()));
        return X509Certificate2.CreateFromPem(ca.ExportCertificatePem(), key.ExportPkcs8PrivateKeyPem());
    }

    /// <summary>Issues a client certificate for <paramref name="hostId"/> from a PEM CSR (the CSR's subject is ignored).</summary>
    public X509Certificate2 Issue(string csrPem, string hostId)
    {
        var csr = CertificateRequest.LoadSigningRequestPem(csrPem, HashAlgorithmName.SHA256, CertificateRequestLoadOptions.Default, RSASignaturePadding.Pkcs1);
        var req = new CertificateRequest(new X500DistinguishedName($"CN={hostId}, O=Marbots Host"), csr.PublicKey, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.2")], true));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        var ca = Certificate;
        req.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(ca, true, false));
        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F;
        var notAfter = DateTimeOffset.UtcNow.AddDays(Math.Clamp(options.HostSecurity.CertificateDays, 1, 3650));
        if (notAfter > ca.NotAfter) notAfter = ca.NotAfter;
        using var caKey = ca.GetECDsaPrivateKey() ?? throw new InvalidOperationException("Host CA has no private key.");
        return req.Create(ca.SubjectName, X509SignatureGenerator.CreateForECDsa(caKey), DateTimeOffset.UtcNow.AddMinutes(-5), notAfter, serial);
    }

    /// <summary>Null when the certificate was issued by this CA for <paramref name="hostId"/>, is in date and has the expected thumbprint; otherwise why not.</summary>
    public string? Validate(X509Certificate2 cert, string hostId, string? expectedThumbprint)
    {
        var now = DateTime.UtcNow;
        if (now < cert.NotBefore.ToUniversalTime() || now > cert.NotAfter.ToUniversalTime()) return "certificate expired or not yet valid";
        if (cert.GetNameInfo(X509NameType.SimpleName, false) != hostId) return "certificate is for another host";
        if (expectedThumbprint is null || !string.Equals(cert.Thumbprint, expectedThumbprint, StringComparison.OrdinalIgnoreCase)) return "certificate was replaced or revoked";
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(Certificate);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.2"));
        return chain.Build(cert) ? null : "certificate is not signed by this server's host CA";
    }

    /// <summary>A client certificate from a proxy header (URL-encoded or raw PEM, or base64 DER).</summary>
    public static X509Certificate2? FromHeader(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            var text = Uri.UnescapeDataString(value).Trim();
            return text.Contains("BEGIN CERTIFICATE", StringComparison.Ordinal)
                ? X509Certificate2.CreateFromPem(text)
                : X509CertificateLoader.LoadCertificate(Convert.FromBase64String(text));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException) { return null; }
    }
}
