using System.Diagnostics;
using System.Text;

namespace DotCode.Providers.Auth;

public sealed record AccessToken(string Token, DateTimeOffset ExpiresAt, string Source);

/// <summary>Caches a bearer token and refreshes it shortly before it expires (one refresh at a time).</summary>
public sealed class TokenCache(Func<CancellationToken, Task<AccessToken>> fetch)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AccessToken? _current;

    public async Task<AccessToken> GetAsync(CancellationToken ct)
    {
        if (_current is { } c && c.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2)) return c;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_current is { } again && again.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2)) return again;
            _current = await fetch(ct).ConfigureAwait(false);
            return _current;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Drops the cached token (after a 401).</summary>
    public void Invalidate() => _current = null;
}

internal static class CliRunner
{
    /// <summary>Runs a CLI (az, gcloud, aws) and returns stdout, or null when it is missing or fails.</summary>
    public static async Task<string?> RunAsync(string exe, string arguments, CancellationToken ct, int timeoutSeconds = 30)
    {
        var path = FindOnPath(exe);
        if (path is null) return null;
        var isScript = path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
        var psi = new ProcessStartInfo(isScript ? "cmd.exe" : path, isScript ? $"/d /c \"\"{path}\" {arguments}\"" : arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        try
        {
            using var p = Process.Start(psi);
            if (p is null) return null;
            var stdout = p.StandardOutput.ReadToEndAsync(ct);
            _ = p.StandardError.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            try { await p.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { try { p.Kill(true); } catch { } return null; }
            return p.ExitCode == 0 ? (await stdout.ConfigureAwait(false)).Trim() : null;
        }
        catch (Exception) { return null; }
    }

    private static string? FindOnPath(string name)
    {
        var exts = OperatingSystem.IsWindows() ? new[] { ".exe", ".cmd", ".bat", "" } : new[] { "" };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var ext in exts)
            {
                var candidate = Path.Combine(dir.Trim('"'), name + ext);
                if (File.Exists(candidate)) return candidate;
            }
        return null;
    }
}
