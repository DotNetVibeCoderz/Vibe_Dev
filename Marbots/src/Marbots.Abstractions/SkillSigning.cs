using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Marbots.Abstractions;

/// <summary>SIGNATURE.json in a skill folder: who signed it, and the signed SHA-256 manifest of every other file.</summary>
public sealed class SkillSignatureFile
{
    public int Version { get; set; } = 1;
    public string Publisher { get; set; } = "";
    /// <summary>First 16 hex characters of the SHA-256 of the publisher's public key (SubjectPublicKeyInfo).</summary>
    public string KeyId { get; set; } = "";
    public string Algorithm { get; set; } = "ES256";
    public DateTimeOffset SignedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<SkillFileHash> Files { get; set; } = [];
    /// <summary>Base64 ECDSA P-256 signature (IEEE P1363) over the canonical manifest.</summary>
    public string Signature { get; set; } = "";
}

public sealed record SkillFileHash(string Path, string Sha256);

/// <summary>A publisher whose skills Marbots trusts.</summary>
public sealed class TrustedSkillPublisher
{
    public string Name { get; set; } = "";
    public string PublicKeyPem { get; set; } = "";
}

public enum SkillSignatureStatus
{
    /// <summary>No SIGNATURE.json.</summary>
    Unsigned,
    /// <summary>Signed by a trusted publisher and every file matches.</summary>
    Verified,
    /// <summary>The signature is valid but the key is not trusted here.</summary>
    UntrustedPublisher,
    /// <summary>Files were added, removed or changed after signing, or the signature is wrong.</summary>
    Invalid,
}

public sealed record SkillPublisherInfo(string Name, string KeyId);

public sealed record SkillVerification(SkillSignatureStatus Status, string? Publisher, string Detail);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(SkillSignatureFile))]
internal sealed partial class SkillSigningJson : JsonSerializerContext;

/// <summary>Signs and verifies skill folders (ECDSA P-256 over a sorted SHA-256 manifest of all files).</summary>
public static class SkillSigning
{
    public const string FileName = "SIGNATURE.json";

    public static string KeyId(ECDsa key) => Convert.ToHexStringLower(SHA256.HashData(key.ExportSubjectPublicKeyInfo()))[..16];

    /// <summary>A new signing key pair (private PKCS#8 PEM, public SPKI PEM).</summary>
    public static (string PrivatePem, string PublicPem) CreateKey()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (key.ExportPkcs8PrivateKeyPem(), key.ExportSubjectPublicKeyInfoPem());
    }

    public static List<SkillFileHash> Hash(string dir) =>
    [
        .. Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(dir, f).Replace('\\', '/'))
            .Where(p => p != FileName && !p.StartsWith(".git/", StringComparison.Ordinal))
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(p => new SkillFileHash(p, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(dir, p)))))),
    ];

    /// <summary>Canonical bytes that are signed: header lines, then "sha256  path" per file.</summary>
    public static byte[] Canonical(SkillSignatureFile s)
    {
        var sb = new StringBuilder();
        sb.Append("marbots-skill-signature/").Append(s.Version).Append('\n');
        sb.Append("publisher:").Append(s.Publisher).Append('\n');
        sb.Append("key:").Append(s.KeyId).Append('\n');
        foreach (var f in s.Files.OrderBy(f => f.Path, StringComparer.Ordinal)) sb.Append(f.Sha256).Append("  ").Append(f.Path).Append('\n');
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    /// <summary>Writes SIGNATURE.json for <paramref name="dir"/>.</summary>
    public static SkillSignatureFile Sign(string dir, string privateKeyPem, string publisher)
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(privateKeyPem);
        var sig = new SkillSignatureFile { Publisher = publisher.Trim(), KeyId = KeyId(key), Files = Hash(dir) };
        if (sig.Files.All(f => f.Path != "SKILL.md")) throw new ArgumentException($"{dir} has no SKILL.md.");
        sig.Signature = Convert.ToBase64String(key.SignData(Canonical(sig), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        File.WriteAllText(Path.Combine(dir, FileName), JsonSerializer.Serialize(sig, SkillSigningJson.Default.SkillSignatureFile));
        return sig;
    }

    /// <summary>Checks a skill folder against trusted publishers.</summary>
    public static SkillVerification Verify(string dir, IEnumerable<TrustedSkillPublisher> trusted)
    {
        var path = Path.Combine(dir, FileName);
        if (!File.Exists(path)) return new(SkillSignatureStatus.Unsigned, null, "not signed");
        SkillSignatureFile? sig;
        try { sig = JsonSerializer.Deserialize(File.ReadAllText(path), SkillSigningJson.Default.SkillSignatureFile); }
        catch (JsonException) { return new(SkillSignatureStatus.Invalid, null, "SIGNATURE.json is not valid JSON"); }
        if (sig is null || sig.Algorithm != "ES256") return new(SkillSignatureStatus.Invalid, sig?.Publisher, "unsupported signature");

        // The files on disk must be exactly the signed ones.
        var actual = Hash(dir);
        var signed = sig.Files.OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
        if (actual.Count != signed.Count || actual.Zip(signed).Any(p => p.First.Path != p.Second.Path || !string.Equals(p.First.Sha256, p.Second.Sha256, StringComparison.OrdinalIgnoreCase)))
        {
            var changed = actual.Select(a => a.Path).Except(signed.Select(s => s.Path))
                .Concat(signed.Select(s => s.Path).Except(actual.Select(a => a.Path)))
                .Concat(actual.Where(a => signed.Any(s => s.Path == a.Path && !string.Equals(s.Sha256, a.Sha256, StringComparison.OrdinalIgnoreCase))).Select(a => a.Path))
                .Distinct().Take(5);
            return new(SkillSignatureStatus.Invalid, sig.Publisher, "files differ from the signed manifest: " + string.Join(", ", changed));
        }

        byte[] signature;
        try { signature = Convert.FromBase64String(sig.Signature); }
        catch (FormatException) { return new(SkillSignatureStatus.Invalid, sig.Publisher, "signature is not base64"); }
        var data = Canonical(sig);
        var matchedKey = false;
        foreach (var p in trusted)
        {
            using var key = ECDsa.Create();
            try { key.ImportFromPem(p.PublicKeyPem); }
            catch (Exception ex) when (ex is ArgumentException or CryptographicException) { continue; }
            if (KeyId(key) != sig.KeyId) continue;
            matchedKey = true;
            if (key.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                return new(SkillSignatureStatus.Verified, p.Name, $"signed by {p.Name}");
        }
        return matchedKey
            ? new(SkillSignatureStatus.Invalid, sig.Publisher, "signature does not match the publisher key")
            : new(SkillSignatureStatus.UntrustedPublisher, sig.Publisher, $"signed by \"{sig.Publisher}\" (key {sig.KeyId}), which is not a trusted publisher");
    }
}
