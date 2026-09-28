using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DotCode.Abstractions;
using DotCode.Engine.Configuration;

namespace DotCode.Engine.Observability;

/// <summary>Append-only, hash-chained audit log (JSON Lines). Every entry carries the SHA-256 of the previous entry
/// plus its own content, so deleting, reordering or editing any line is detected by <see cref="Verify"/>.
/// Secrets in tool inputs are redacted before writing.</summary>
public sealed partial class AuditLog
{
    private readonly string _path;
    private readonly Lock _gate = new();
    private string _prevHash;

    public string FilePath => _path;

    private AuditLog(string path)
    {
        _path = path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _prevHash = LastHash(path);
    }

    /// <summary>Creates the log when enabled in settings ("audit": {"enabled": true}) — typically via managed settings.</summary>
    public static AuditLog? Create(Settings settings)
    {
        if (settings.Audit?.Enabled != true) return null;
        var path = settings.Audit.Path is { Length: > 0 } p
            ? DotCodePaths.ExpandHome(Providers.ConfigValue.Expand(p) ?? p)
            : Path.Combine(DotCodePaths.UserDir, "audit", $"audit-{DateTime.UtcNow:yyyy-MM}.jsonl");
        return new AuditLog(path);
    }

    private static string LastHash(string path)
    {
        if (!File.Exists(path)) return new string('0', 64);
        string? last = null;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var reader = new StreamReader(fs))
            while (reader.ReadLine() is { } line) if (line.Length > 0) last = line;
        if (last is null) return new string('0', 64);
        try { return DotCodeJson.Parse(last).GetString("hash") ?? new string('0', 64); }
        catch (JsonException) { return new string('0', 64); }
    }

    public void Write(string eventName, string sessionId, string cwd, Action<Utf8JsonWriter> fields)
    {
        lock (_gate)
        {
            // Canonical body = entry without "hash"; hash = SHA256(prevHash + body).
            var body = DotCodeJson.Build(w =>
            {
                w.WriteStartObject();
                w.WriteString("ts", DateTime.UtcNow.ToString("O"));
                w.WriteString("event", eventName);
                w.WriteString("session", sessionId);
                w.WriteString("user", Environment.UserName);
                w.WriteString("host", Environment.MachineName);
                w.WriteString("cwd", cwd);
                fields(w);
                w.WriteString("prev", _prevHash);
                w.WriteEndObject();
            }).GetRawText();
            var hash = Hash(_prevHash, body);
            var line = body[..^1] + ",\"hash\":\"" + hash + "\"}";
            File.AppendAllText(_path, line + "\n");
            _prevHash = hash;
        }
    }

    private static string Hash(string prev, string body) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(prev + body)));

    public sealed record VerifyResult(bool Ok, int Entries, int? BrokenAtLine, string Message);

    /// <summary>Recomputes the chain; reports the first line whose hash or back-link does not match.</summary>
    public static VerifyResult Verify(string path)
    {
        if (!File.Exists(path)) return new VerifyResult(false, 0, null, $"File not found: {path}");
        var prev = new string('0', 64);
        var n = 0;
        foreach (var line in File.ReadLines(path))
        {
            if (line.Length == 0) continue;
            n++;
            var marker = line.LastIndexOf(",\"hash\":\"", StringComparison.Ordinal);
            if (marker < 0) return new VerifyResult(false, n - 1, n, "missing hash");
            var body = line[..marker] + "}";
            var hash = line[(marker + 9)..^2];
            string? linkedPrev;
            try { linkedPrev = DotCodeJson.Parse(body).GetString("prev"); }
            catch (JsonException) { return new VerifyResult(false, n - 1, n, "malformed entry"); }
            if (linkedPrev != prev) return new VerifyResult(false, n - 1, n, "chain link does not match the previous entry (entry removed or reordered)");
            if (Hash(prev, body) != hash) return new VerifyResult(false, n - 1, n, "content hash mismatch (entry modified)");
            prev = hash;
        }
        return new VerifyResult(true, n, null, $"{n} entries, chain intact");
    }

    [GeneratedRegex(@"(sk-[A-Za-z0-9_\-]{12,}|sk_[A-Za-z0-9]{16,}|ghp_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|xox[abprs]-[A-Za-z0-9\-]{10,}|AKIA[0-9A-Z]{16}|AIza[0-9A-Za-z_\-]{30,}|npm_[A-Za-z0-9]{30,}|pypi-[A-Za-z0-9_\-]{30,}|eyJ[A-Za-z0-9_\-]{20,}\.[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,})")]
    private static partial Regex TokenPattern();

    [GeneratedRegex(@"(?i)((?:api[_-]?key|token|secret|password|passwd|authorization)[""']?\s*[:=]\s*[""']?)([^\s""',;]{6,})")]
    private static partial Regex AssignmentPattern();

    /// <summary>Masks common credential formats (API keys, tokens, JWTs, key=value secrets).</summary>
    public static string Redact(string text)
    {
        text = TokenPattern().Replace(text, m => m.Value[..Math.Min(4, m.Value.Length)] + "***");
        return AssignmentPattern().Replace(text, m => m.Groups[1].Value + "***");
    }
}
