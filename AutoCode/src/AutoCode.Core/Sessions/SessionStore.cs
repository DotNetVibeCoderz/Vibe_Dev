// Auto Code — Gravicode Studios (Kang Fadhil)

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AutoCode.Core.Configuration;
using Microsoft.Extensions.AI;

namespace AutoCode.Core.Sessions;

/// <summary>
/// Persists transcripts under <c>~/.autocode/sessions/&lt;workspace&gt;/</c>.
///
/// EN: sessions are scoped per workspace so <c>--continue</c> in one repository never resumes a
/// conversation from another. The directory name is a hash of the absolute path, with a readable
/// prefix so the folder is still navigable by hand.
/// ID: sesi dipisahkan per workspace sehingga <c>--continue</c> di satu repositori tidak akan pernah
/// melanjutkan percakapan dari repositori lain.
/// </summary>
public sealed class SessionStore
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    private readonly string _root;

    public SessionStore(string workspaceRoot)
    {
        _root = Path.Combine(ConfigurationLoader.UserHome, "sessions", ScopeKey(workspaceRoot));
    }

    /// <summary>Directory this store writes into. Created lazily on first save.</summary>
    public string Directory => _root;

    public async Task SaveAsync(Session session, CancellationToken cancellationToken = default)
    {
        session.UpdatedAt = DateTimeOffset.UtcNow;

        if (string.IsNullOrWhiteSpace(session.Title))
            session.Title = session.DeriveTitle();

        System.IO.Directory.CreateDirectory(_root);

        var path = Path.Combine(_root, $"{session.Id}.json");
        var temporary = path + ".tmp";

        // Write-then-move: a crash mid-write must not leave a truncated transcript behind.
        await using (var stream = File.Create(temporary))
            await JsonSerializer.SerializeAsync(stream, session, SerializerOptions, cancellationToken).ConfigureAwait(false);

        File.Move(temporary, path, overwrite: true);
    }

    public async Task<Session?> LoadAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(_root, $"{sessionId}.json");

        if (!File.Exists(path))
            return null;

        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<Session>(stream, SerializerOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Most recent session for this workspace, or null when there is none.</summary>
    public async Task<Session?> LoadLatestAsync(CancellationToken cancellationToken = default)
    {
        var newest = ListFiles().FirstOrDefault();
        return newest is null ? null : await LoadAsync(Path.GetFileNameWithoutExtension(newest.Name), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Session summaries, newest first, for the <c>/resume</c> picker.</summary>
    public async Task<IReadOnlyList<Session>> ListAsync(int limit = 20, CancellationToken cancellationToken = default)
    {
        var results = new List<Session>();

        foreach (var file in ListFiles().Take(limit))
        {
            var session = await LoadAsync(Path.GetFileNameWithoutExtension(file.Name), cancellationToken).ConfigureAwait(false);
            if (session is not null)
                results.Add(session);
        }

        return results;
    }

    public void Delete(string sessionId)
    {
        var path = Path.Combine(_root, $"{sessionId}.json");
        if (File.Exists(path))
            File.Delete(path);
    }

    private IEnumerable<FileInfo> ListFiles()
    {
        if (!System.IO.Directory.Exists(_root))
            return [];

        return new DirectoryInfo(_root)
            .EnumerateFiles("*.json")
            .OrderByDescending(f => f.LastWriteTimeUtc);
    }

    /// <summary>Readable-prefix + hash, so the folder is both stable and browsable.</summary>
    private static string ScopeKey(string workspaceRoot)
    {
        var full = Path.GetFullPath(workspaceRoot).TrimEnd(Path.DirectorySeparatorChar);
        var name = Path.GetFileName(full);

        if (string.IsNullOrEmpty(name))
            name = "root";

        foreach (var invalid in Path.GetInvalidFileNameChars())
            name = name.Replace(invalid, '-');

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full)))[..8].ToLowerInvariant();
        return $"{name}-{hash}";
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        // AIJsonUtilities carries the polymorphic converters for AIContent; without them a
        // transcript containing tool calls round-trips as empty messages.
        var options = new JsonSerializerOptions(AIJsonUtilities.DefaultOptions)
        {
            WriteIndented = false,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };

        return options;
    }
}
