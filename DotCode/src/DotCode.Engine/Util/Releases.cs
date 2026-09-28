using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using DotCode.Abstractions;
using DotCode.Providers.Http;

namespace DotCode.Engine.Util;

public sealed record ReleaseAsset(string Name, string Url, long Size);

public sealed record ReleaseInfo(string Tag, string Version, bool Prerelease, string HtmlUrl, IReadOnlyList<ReleaseAsset> Assets)
{
    public ReleaseAsset? Asset(string name) => Assets.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>DotCode releases on GitHub (tags <c>dotcode-v*</c>; the repository hosts other projects too) and the
/// self-update used by <c>dotcode update</c>: download the archive for this platform, verify it against
/// <c>SHA256SUMS</c>, and swap the executable in place.</summary>
public static class Releases
{
    /// <summary>API base (overridable for tests and mirrors with DOTCODE_RELEASES_API).</summary>
    public static string ApiBase => Environment.GetEnvironmentVariable("DOTCODE_RELEASES_API") is { Length: > 0 } a
        ? a.TrimEnd('/')
        : $"https://api.github.com/repos/{AppInfo.RepositorySlug}";

    /// <summary>The newest DotCode release (optionally a specific version, optionally including prereleases).</summary>
    public static async Task<ReleaseInfo?> FindAsync(string? version, bool includePrerelease, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/releases?per_page=50");
        req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        if (Environment.GetEnvironmentVariable("GITHUB_TOKEN") is { Length: > 0 } token) req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        using var resp = await ProviderHttp.GetClient().SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"GitHub API returned {(int)resp.StatusCode}: {TextUtil.FirstLine(body, 200)}");
        var releases = new List<ReleaseInfo>();
        foreach (var r in DotCodeJson.Parse(body).EnumerateArray())
        {
            var tag = r.GetString("tag_name") ?? "";
            if (!tag.StartsWith(AppInfo.ReleaseTagPrefix, StringComparison.Ordinal) || r.GetBool("draft") == true) continue;
            var assets = r.GetProp("assets") is { ValueKind: JsonValueKind.Array } arr
                ? arr.EnumerateArray().Select(a => new ReleaseAsset(a.GetString("name") ?? "", a.GetString("browser_download_url") ?? "", a.GetProp("size")?.GetInt64() ?? 0)).ToList()
                : [];
            releases.Add(new ReleaseInfo(tag, tag[AppInfo.ReleaseTagPrefix.Length..], r.GetBool("prerelease") == true, r.GetString("html_url") ?? "", assets));
        }
        if (version is { Length: > 0 })
            return releases.FirstOrDefault(r => r.Version == version.TrimStart('v') || r.Tag == version);
        return releases.Where(r => includePrerelease || !r.Prerelease).OrderByDescending(r => ParseVersion(r.Version)).FirstOrDefault();
    }

    /// <summary>Numeric version (prerelease suffix ignored, "1.2.3-beta" &lt; "1.2.3").</summary>
    public static (Version Number, bool Prerelease) ParseVersion(string v)
    {
        var dash = v.IndexOf('-');
        var core = dash >= 0 ? v[..dash] : v;
        return (System.Version.TryParse(core, out var parsed) ? parsed : new Version(0, 0), dash >= 0);
    }

    /// <summary>True when <paramref name="candidate"/> is newer than <paramref name="current"/>.</summary>
    public static bool IsNewer(string candidate, string current)
    {
        var (a, aPre) = ParseVersion(candidate);
        var (b, bPre) = ParseVersion(current);
        var cmp = a.CompareTo(b);
        return cmp > 0 || cmp == 0 && bPre && !aPre;
    }

    /// <summary>Runtime identifier of the running build (win-x64, linux-arm64, osx-arm64…).</summary>
    public static string CurrentRid()
    {
        var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            _ => "x64",
        };
        return $"{os}-{arch}";
    }

    public static string ArchiveName(string rid) => rid.StartsWith("win", StringComparison.Ordinal) ? $"dotcode-{rid}.zip" : $"dotcode-{rid}.tar.gz";

    /// <summary>How DotCode is installed: a native (NativeAOT) binary we can replace, or something else.</summary>
    public static (bool NativeBinary, string? Path, string Hint) InstallKind()
    {
        var path = Environment.ProcessPath;
        var name = Path.GetFileNameWithoutExtension(path ?? "");
        if (path is null || name.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            return (false, path, "DotCode runs on the .NET runtime here. Update the .NET tool with: dotnet tool update -g DotCode.Cli");
        if (path.Contains($"{Path.DirectorySeparatorChar}.dotnet{Path.DirectorySeparatorChar}tools", StringComparison.OrdinalIgnoreCase))
            return (false, path, "DotCode is installed as a .NET tool. Update it with: dotnet tool update -g DotCode.Cli");
        return (true, path, "");
    }

    /// <summary>Downloads the release archive for <paramref name="rid"/>, verifies SHA256SUMS (when published) and
    /// replaces <paramref name="targetExe"/>. Returns a description of what was done.</summary>
    public static async Task<string> InstallAsync(ReleaseInfo release, string rid, string targetExe, Action<string>? progress, CancellationToken ct)
    {
        var archiveName = ArchiveName(rid);
        var asset = release.Asset(archiveName) ?? release.Asset($"dotcode-{rid}.zip")
                    ?? throw new InvalidOperationException($"Release {release.Tag} has no build for {rid} ({archiveName}).");
        var temp = Path.Combine(Path.GetTempPath(), "dotcode-update-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(temp);
        try
        {
            progress?.Invoke($"Downloading {asset.Name} ({asset.Size / 1024.0 / 1024.0:0.0} MB)…");
            var archive = Path.Combine(temp, asset.Name);
            await DownloadAsync(asset.Url, archive, ct).ConfigureAwait(false);

            var verified = false;
            if (release.Asset("SHA256SUMS") is { } sums)
            {
                var sumsPath = Path.Combine(temp, "SHA256SUMS");
                await DownloadAsync(sums.Url, sumsPath, ct).ConfigureAwait(false);
                var expected = File.ReadAllLines(sumsPath).Select(l => l.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries))
                    .Where(p => p.Length == 2 && p[1].TrimStart('*').Trim() == asset.Name).Select(p => p[0].Trim().ToLowerInvariant()).FirstOrDefault()
                    ?? throw new InvalidOperationException($"SHA256SUMS has no entry for {asset.Name}.");
                await using (var fs = File.OpenRead(archive))
                {
                    var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false));
                    if (actual != expected) throw new InvalidOperationException($"Checksum mismatch for {asset.Name}: expected {expected}, got {actual}. Nothing was changed.");
                }
                verified = true;
            }

            var exeName = rid.StartsWith("win", StringComparison.Ordinal) ? "dotcode.exe" : "dotcode";
            var extracted = Path.Combine(temp, "x");
            Directory.CreateDirectory(extracted);
            if (asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) ZipFile.ExtractToDirectory(archive, extracted);
            else
            {
                await using var gz = new GZipStream(File.OpenRead(archive), CompressionMode.Decompress);
                await TarFile.ExtractToDirectoryAsync(gz, extracted, overwriteFiles: true, ct).ConfigureAwait(false);
            }
            var newExe = Directory.EnumerateFiles(extracted, exeName, SearchOption.AllDirectories).FirstOrDefault()
                         ?? throw new InvalidOperationException($"{asset.Name} does not contain {exeName}.");

            Replace(targetExe, newExe);
            return $"Updated to DotCode {release.Version} ({asset.Name}{(verified ? ", SHA-256 verified" : ", no SHA256SUMS published")}).";
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task DownloadAsync(string url, string path, CancellationToken ct)
    {
        using var resp = await ProviderHttp.GetClient().GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"Download failed ({(int)resp.StatusCode}): {url}");
        await using var file = File.Create(path);
        await resp.Content.CopyToAsync(file, ct).ConfigureAwait(false);
    }

    /// <summary>Swaps the executable. A running executable cannot be overwritten on Windows but can be renamed, so the
    /// old one moves aside to <c>.old</c> (removed on the next start); on Unix a rename over the file is atomic.</summary>
    private static void Replace(string targetExe, string newExe)
    {
        var dir = Path.GetDirectoryName(targetExe)!;
        var staged = Path.Combine(dir, Path.GetFileName(targetExe) + ".new");
        File.Copy(newExe, staged, overwrite: true);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(staged, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        if (OperatingSystem.IsWindows())
        {
            var old = targetExe + ".old";
            if (File.Exists(old)) File.Delete(old);
            if (File.Exists(targetExe)) File.Move(targetExe, old);
            File.Move(staged, targetExe);
        }
        else File.Move(staged, targetExe, overwrite: true);
    }

    /// <summary>Removes the previous executable left behind by a Windows self-update.</summary>
    public static void CleanupPreviousUpdate()
    {
        if (Environment.ProcessPath is not { } path) return;
        var old = path + ".old";
        try { if (File.Exists(old)) File.Delete(old); } catch (Exception) { /* still in use: next time */ }
    }
}
