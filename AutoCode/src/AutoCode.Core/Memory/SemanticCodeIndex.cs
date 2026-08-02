// Auto Code — Gravicode Studios (Kang Fadhil)

using System.Security.Cryptography;
using System.Text;
using AutoCode.Core.Abstractions;
using AutoCode.Core.Utilities;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;

namespace AutoCode.Core.Memory;

/// <summary>
/// Embedding-backed search over the workspace, so the agent can find code by what it does rather
/// than by what it is called.
///
/// EN: files are split on blank lines with an overlap, which keeps a function and its signature in
/// the same chunk far more often than a fixed-size window would. Embedding is batched because
/// per-chunk requests are what make indexing feel slow.
/// ID: berkas dipecah pada baris kosong dengan tumpang tindih agar sebuah fungsi dan tanda tangannya
/// tetap berada dalam satu potongan. Proses embedding dilakukan per batch agar pengindeksan cepat.
/// </summary>
public sealed class SemanticCodeIndex(
    string workspaceRoot,
    IEmbeddingGenerator<string, Embedding<float>> embeddings,
    int expectedDimensions = 0) : ISemanticCodeIndex, IDisposable
{
    /// <summary>
    /// Vector width, learned from the first batch when it was not configured.
    ///
    /// EN: inferring beats requiring. A stated dimension that disagrees with the model is a silent
    /// failure — every similarity comparison is skipped and the index returns nothing, with no error
    /// anywhere. Learning it removes that whole class of misconfiguration, and a configured value is
    /// still honoured as an assertion when the user wants one.
    /// ID: menyimpulkan lebih baik daripada mewajibkan. Dimensi yang salah tulis menyebabkan
    /// kegagalan diam-diam: semua perbandingan kemiripan dilewati dan indeks tidak mengembalikan
    /// apa pun tanpa pesan galat. Nilai yang dikonfigurasi tetap dihormati sebagai penegasan.
    /// </summary>
    public int Dimensions { get; private set; }

    private const int TargetChunkLines = 60;
    private const int OverlapLines = 8;
    private const int BatchSize = 64;
    private const long MaxFileBytes = 512 * 1024;

    private readonly InMemoryCodeChunkCollection _collection = new("code-chunks");
    private readonly SemaphoreSlim _buildGate = new(1, 1);

    public bool IsReady { get; private set; }

    /// <summary>Number of indexed chunks; surfaced by <c>/index</c>.</summary>
    public int ChunkCount => _collection.Count;

    private static readonly HashSet<string> IndexableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".fs", ".vb", ".ts", ".tsx", ".js", ".jsx", ".py", ".go", ".rs", ".java", ".kt",
        ".rb", ".php", ".swift", ".c", ".h", ".cpp", ".hpp", ".cc", ".m", ".scala", ".sql",
        ".sh", ".ps1", ".md", ".json", ".yaml", ".yml", ".toml", ".razor", ".cshtml", ".vue",
    };

    public async Task BuildAsync(IProgress<string>? progress, CancellationToken cancellationToken)
    {
        await _buildGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await _collection.EnsureCollectionDeletedAsync(cancellationToken).ConfigureAwait(false);
            await _collection.EnsureCollectionExistsAsync(cancellationToken).ConfigureAwait(false);

            var pending = new List<CodeChunk>(BatchSize);
            var files = 0;

            foreach (var file in EnumerateIndexableFiles())
            {
                cancellationToken.ThrowIfCancellationRequested();

                string content;
                try
                {
                    content = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                if (content.Length == 0)
                    continue;

                files++;
                var relative = WorkspacePath.Relative(workspaceRoot, file);

                foreach (var chunk in Chunk(relative, content))
                {
                    pending.Add(chunk);

                    if (pending.Count >= BatchSize)
                    {
                        await FlushAsync(pending, cancellationToken).ConfigureAwait(false);
                        progress?.Report($"Indexed {files} files, {_collection.Count} chunks…");
                    }
                }
            }

            if (pending.Count > 0)
                await FlushAsync(pending, cancellationToken).ConfigureAwait(false);

            IsReady = true;
            progress?.Report($"Index ready: {files} files, {_collection.Count} chunks.");
        }
        finally
        {
            _buildGate.Release();
        }
    }

    public async Task<IReadOnlyList<SemanticHit>> SearchAsync(
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        if (!IsReady)
            return [];

        var embedded = await embeddings.GenerateAsync([query], cancellationToken: cancellationToken).ConfigureAwait(false);
        var vector = embedded[0].Vector;

        var hits = new List<SemanticHit>(limit);

        await foreach (var result in _collection
            .SearchAsync(vector, limit, cancellationToken: cancellationToken)
            .ConfigureAwait(false))
        {
            hits.Add(new SemanticHit(
                result.Record.RelativePath,
                result.Record.StartLine,
                result.Record.EndLine,
                result.Record.Text,
                result.Score ?? 0));
        }

        return hits;
    }

    private async Task FlushAsync(List<CodeChunk> pending, CancellationToken cancellationToken)
    {
        var vectors = await embeddings
            .GenerateAsync(pending.Select(c => c.Text), cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (vectors.Count > 0)
            ObserveDimensions(vectors[0].Vector.Length);

        for (var i = 0; i < pending.Count && i < vectors.Count; i++)
            pending[i].Embedding = vectors[i].Vector;

        await _collection.UpsertAsync(pending, cancellationToken).ConfigureAwait(false);
        pending.Clear();
    }

    /// <summary>
    /// Records the vector width on the first batch, and enforces it thereafter. A configured
    /// <c>expectedDimensions</c> is treated as an assertion the model has to satisfy.
    /// </summary>
    private void ObserveDimensions(int observed)
    {
        if (Dimensions == 0)
        {
            if (expectedDimensions > 0 && observed != expectedDimensions)
            {
                throw new InvalidOperationException(
                    $"The embedding model returned {observed}-dimensional vectors but settings declare " +
                    $"{expectedDimensions}. Correct embeddings.dimensions, or remove it and let Auto Code " +
                    "infer the width.");
            }

            Dimensions = observed;
            return;
        }

        if (observed != Dimensions)
        {
            // Mixing widths in one collection means every later comparison is silently skipped.
            throw new InvalidOperationException(
                $"The embedding model returned {observed}-dimensional vectors after previously returning " +
                $"{Dimensions}. The index cannot mix widths; rebuild it with a single model.");
        }
    }

    private IEnumerable<string> EnumerateIndexableFiles()
    {
        var stack = new Stack<string>();
        stack.Push(workspaceRoot);

        while (stack.Count > 0)
        {
            var current = stack.Pop();

            string[] files;
            string[] directories;

            try
            {
                files = Directory.GetFiles(current);
                directories = Directory.GetDirectories(current);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                if (!IndexableExtensions.Contains(Path.GetExtension(file)))
                    continue;

                if (new FileInfo(file).Length > MaxFileBytes)
                    continue;

                yield return file;
            }

            foreach (var directory in directories)
            {
                if (!WorkspacePath.IgnoredDirectories.Contains(Path.GetFileName(directory)))
                    stack.Push(directory);
            }
        }
    }

    /// <summary>
    /// Splits a file into overlapping chunks, preferring to break on a blank line so a declaration
    /// and its body stay together.
    /// </summary>
    internal static IEnumerable<CodeChunk> Chunk(string relativePath, string content)
    {
        var lines = content.ReplaceLineEndings("\n").Split('\n');
        var start = 0;

        while (start < lines.Length)
        {
            var end = Math.Min(lines.Length, start + TargetChunkLines);

            // Nudge the boundary back to the nearest blank line, when there is one nearby.
            if (end < lines.Length)
            {
                for (var probe = end; probe > start + TargetChunkLines / 2; probe--)
                {
                    if (lines[probe - 1].Trim().Length == 0)
                    {
                        end = probe;
                        break;
                    }
                }
            }

            var text = string.Join('\n', lines[start..end]).Trim();

            if (text.Length >= 40)
            {
                yield return new CodeChunk
                {
                    Id = MakeId(relativePath, start),
                    RelativePath = relativePath,
                    StartLine = start + 1,
                    EndLine = end,
                    Text = text,
                };
            }

            if (end >= lines.Length)
                break;

            start = Math.Max(end - OverlapLines, start + 1);
        }
    }

    private static string MakeId(string relativePath, int startLine)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{relativePath}:{startLine}"));
        return Convert.ToHexString(hash)[..24].ToLowerInvariant();
    }

    public void Dispose()
    {
        _collection.Dispose();
        _buildGate.Dispose();
    }
}
