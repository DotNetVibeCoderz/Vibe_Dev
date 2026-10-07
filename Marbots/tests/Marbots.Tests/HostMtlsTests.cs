using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Marbots.Abstractions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Marbots.Tests;

/// <summary>Mutual TLS for agent hosts: certificates issued at enrollment, required on connect, renewed and revoked.</summary>
public sealed class HostMtlsTests : IClassFixture<HostMtlsTests.Fixture>
{
    public sealed class Fixture : WebApplicationFactory<Program>
    {
        public string DataDir { get; } = Path.Combine(Path.GetTempPath(), "mb-mtls-" + Guid.NewGuid().ToString("N")[..8]);

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Marbots:DataDirectory", DataDir);
            builder.UseSetting("Marbots:HostSecurity:RequireClientCertificate", "true");
            // TestServer has no TLS handshake; the proxy-header path carries the certificate instead.
            builder.UseSetting("Marbots:HostSecurity:ClientCertificateHeader", "X-Client-Cert");
        }
    }

    private readonly Fixture _fx;
    public HostMtlsTests(Fixture fx) => _fx = fx;

    private static (ECDsa Key, string Csr) Csr()
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (key, new CertificateRequest("CN=whatever", key, HashAlgorithmName.SHA256).CreateSigningRequestPem());
    }

    private async Task<HttpStatusCode> ConnectAsync(string hostId, string secret, string? certPem)
    {
        var ws = _fx.Server.CreateWebSocketClient();
        ws.ConfigureRequest = r =>
        {
            r.Headers[HostProtocol.HostIdHeader] = hostId;
            r.Headers[HostProtocol.HostSecretHeader] = secret;
            if (certPem is not null) r.Headers["X-Client-Cert"] = Uri.EscapeDataString(certPem);
        };
        try
        {
            using var socket = await ws.ConnectAsync(new Uri(_fx.Server.BaseAddress, "api/v1/hosts/connect"), default);
            try { await socket.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "", default); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
            return HttpStatusCode.SwitchingProtocols;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("401")) { return HttpStatusCode.Unauthorized; }
    }

    [Fact]
    public async Task Hosts_need_their_current_certificate_to_connect()
    {
        var http = _fx.CreateClient();
        var token = (await (await http.PostAsJsonAsync("/api/v1/hosts/enrollments", new CreateEnrollmentRequest("gpu-box", 10))).Content.ReadFromJsonAsync<CreateEnrollmentResult>())!.Token;
        var (key, csr) = Csr();
        var hello = new HostHello { Name = "gpu-box", Capabilities = ["shell"] };
        var enrolled = (await (await http.PostAsJsonAsync("/api/v1/hosts/enroll", new HostEnrollmentRequest(token, hello, csr))).Content.ReadFromJsonAsync<HostEnrollmentResult>())!;
        Assert.NotNull(enrolled.Certificate);
        using var cert = X509Certificate2.CreateFromPem(enrolled.Certificate);
        using var ca = X509Certificate2.CreateFromPem(enrolled.CaCertificate!);
        Assert.Equal(enrolled.HostId, cert.GetNameInfo(X509NameType.SimpleName, false));
        Assert.Equal(ca.Subject, cert.Issuer);
        Assert.Contains(cert.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single().EnhancedKeyUsages.Cast<Oid>(), o => o.Value == "1.3.6.1.5.5.7.3.2");
        Assert.Equal(key.ExportSubjectPublicKeyInfo(), cert.PublicKey.ExportSubjectPublicKeyInfo());

        Assert.Equal(HttpStatusCode.Unauthorized, await ConnectAsync(enrolled.HostId, enrolled.Secret, null));
        Assert.Equal(HttpStatusCode.SwitchingProtocols, await ConnectAsync(enrolled.HostId, enrolled.Secret, enrolled.Certificate));

        // A self-signed look-alike with the same subject is refused.
        using var fakeKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var fake = new CertificateRequest($"CN={enrolled.HostId}, O=Marbots Host", fakeKey, HashAlgorithmName.SHA256).CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        Assert.Equal(HttpStatusCode.Unauthorized, await ConnectAsync(enrolled.HostId, enrolled.Secret, fake.ExportCertificatePem()));

        // Renewal: the new certificate works, the old one is revoked.
        var (_, csr2) = Csr();
        var renew = new HttpRequestMessage(HttpMethod.Post, "/api/v1/hosts/renew") { Content = JsonContent.Create(new HostCertificateRenewal(csr2)) };
        renew.Headers.Add(HostProtocol.HostIdHeader, enrolled.HostId);
        renew.Headers.Add(HostProtocol.HostSecretHeader, enrolled.Secret);
        renew.Headers.Add("X-Client-Cert", Uri.EscapeDataString(enrolled.Certificate!));
        var renewed = (await (await http.SendAsync(renew)).Content.ReadFromJsonAsync<HostCertificateResult>())!;
        Assert.Equal(HttpStatusCode.Unauthorized, await ConnectAsync(enrolled.HostId, enrolled.Secret, enrolled.Certificate));
        Assert.Equal(HttpStatusCode.SwitchingProtocols, await ConnectAsync(enrolled.HostId, enrolled.Secret, renewed.Certificate));
        // Another host's valid certificate does not fit this host.
        Assert.Equal(HttpStatusCode.Unauthorized, await ConnectAsync("host-other-0000", enrolled.Secret, renewed.Certificate));

        // A bad CSR is a 400, not a crash.
        var bad = new HttpRequestMessage(HttpMethod.Post, "/api/v1/hosts/renew") { Content = JsonContent.Create(new HostCertificateRenewal("not a csr")) };
        bad.Headers.Add(HostProtocol.HostIdHeader, enrolled.HostId);
        bad.Headers.Add(HostProtocol.HostSecretHeader, enrolled.Secret);
        bad.Headers.Add("X-Client-Cert", Uri.EscapeDataString(renewed.Certificate));
        Assert.Equal(HttpStatusCode.BadRequest, (await http.SendAsync(bad)).StatusCode);
    }
}
