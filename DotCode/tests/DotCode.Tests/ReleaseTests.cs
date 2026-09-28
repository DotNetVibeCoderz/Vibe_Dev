using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using DotCode.Engine.Util;
using DotCode.Providers.Http;

namespace DotCode.Tests;

/// <summary>Release discovery and `dotcode update` against a mocked GitHub.</summary>
public sealed class ReleaseTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dc-rel-" + Guid.NewGuid().ToString("n")[..8]);
    private readonly FakeGitHub _github = new();

    public ReleaseTests()
    {
        Directory.CreateDirectory(_dir);
        ProviderHttp.OverrideHandler = _github;
        Environment.SetEnvironmentVariable("DOTCODE_RELEASES_API", "https://api.test/repos/o/r");
    }

    public void Dispose()
    {
        ProviderHttp.OverrideHandler = null;
        Environment.SetEnvironmentVariable("DOTCODE_RELEASES_API", null);
        try { Directory.Delete(_dir, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    [Theory]
    [InlineData("0.2.0", "0.1.0", true)]
    [InlineData("0.1.0", "0.1.0", false)]
    [InlineData("0.10.0", "0.9.9", true)]
    [InlineData("0.2.0", "0.2.0-beta.1", true)]
    [InlineData("0.2.0-beta.1", "0.2.0", false)]
    [InlineData("0.1.9", "0.2.0", false)]
    public void Compares_versions(string candidate, string current, bool newer) => Assert.Equal(newer, Releases.IsNewer(candidate, current));

    [Fact]
    public async Task Finds_the_newest_stable_dotcode_release_among_other_projects()
    {
        _github.Releases = """
            [
              {"tag_name":"dotcode-v0.3.0-beta.1","prerelease":true,"draft":false,"html_url":"h3","assets":[]},
              {"tag_name":"otherapp-v9.0.0","prerelease":false,"draft":false,"html_url":"x","assets":[]},
              {"tag_name":"dotcode-java-v0.1.2","prerelease":false,"draft":false,"html_url":"j","assets":[]},
              {"tag_name":"dotcode-v0.2.0","prerelease":false,"draft":false,"html_url":"h2","assets":[{"name":"SHA256SUMS","browser_download_url":"u","size":1}]},
              {"tag_name":"dotcode-v0.1.0","prerelease":false,"draft":false,"html_url":"h1","assets":[]}
            ]
            """;
        var latest = await Releases.FindAsync(null, includePrerelease: false, CancellationToken.None);
        Assert.Equal(("dotcode-v0.2.0", "0.2.0"), (latest!.Tag, latest.Version));
        Assert.NotNull(latest.Asset("sha256sums"));
        Assert.Equal("0.3.0-beta.1", (await Releases.FindAsync(null, includePrerelease: true, CancellationToken.None))!.Version);
        Assert.Equal("h1", (await Releases.FindAsync("v0.1.0", false, CancellationToken.None))!.HtmlUrl);
    }

    [Theory]
    [InlineData("win-x64")]
    [InlineData("linux-x64")]
    public async Task Update_verifies_the_checksum_and_replaces_the_executable(string rid)
    {
        var exeName = rid.StartsWith("win") ? "dotcode.exe" : "dotcode";
        var archiveName = Releases.ArchiveName(rid);
        var archive = BuildArchive(archiveName, exeName, "NEW BINARY");
        _github.Files[$"/dl/{archiveName}"] = archive;
        _github.Files["/dl/SHA256SUMS"] = Encoding.ASCII.GetBytes($"{Convert.ToHexStringLower(SHA256.HashData(archive))}  {archiveName}\n");
        var release = new ReleaseInfo("dotcode-v0.2.0", "0.2.0", false, "",
            [new ReleaseAsset(archiveName, "https://dl.test/dl/" + archiveName, archive.Length), new ReleaseAsset("SHA256SUMS", "https://dl.test/dl/SHA256SUMS", 100)]);

        var target = Path.Combine(_dir, exeName);
        File.WriteAllText(target, "OLD BINARY");
        var message = await Releases.InstallAsync(release, rid, target, null, CancellationToken.None);
        Assert.Contains("SHA-256 verified", message);
        Assert.Equal("NEW BINARY", File.ReadAllText(target));
        if (OperatingSystem.IsWindows()) Assert.Equal("OLD BINARY", File.ReadAllText(target + ".old"));

        // A tampered download is rejected and leaves the installed binary alone.
        _github.Files["/dl/SHA256SUMS"] = Encoding.ASCII.GetBytes($"{new string('0', 64)}  {archiveName}\n");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Releases.InstallAsync(release, rid, target, null, CancellationToken.None));
        Assert.Contains("Checksum mismatch", ex.Message);
        Assert.Equal("NEW BINARY", File.ReadAllText(target));
    }

    private static byte[] BuildArchive(string archiveName, string exeName, string content)
    {
        using var ms = new MemoryStream();
        if (archiveName.EndsWith(".zip"))
        {
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                using (var s = zip.CreateEntry(exeName).Open()) s.Write(Encoding.UTF8.GetBytes(content));
                zip.CreateEntry("README.md").Open().Dispose();
            }
            return ms.ToArray();
        }
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gz, TarEntryFormat.Pax, leaveOpen: true))
        {
            var entry = new PaxTarEntry(TarEntryType.RegularFile, exeName) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)), Mode = (UnixFileMode)0b111_101_101 };
            tar.WriteEntry(entry);
        }
        return ms.ToArray();
    }

    private sealed class FakeGitHub : HttpMessageHandler
    {
        public string Releases { get; set; } = "[]";
        public Dictionary<string, byte[]> Files { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            if (uri.Host == "api.test")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Releases, Encoding.UTF8, "application/json") });
            return Task.FromResult(Files.TryGetValue(uri.AbsolutePath, out var bytes)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
