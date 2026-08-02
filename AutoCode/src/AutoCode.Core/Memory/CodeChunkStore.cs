// Auto Code — Gravicode Studios (Kang Fadhil)

using System.Linq.Expressions;
using System.Numerics.Tensors;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.VectorData;

namespace AutoCode.Core.Memory;

/// <summary>One indexed slice of a source file.</summary>
public sealed class CodeChunk
{
    [VectorStoreKey]
    public string Id { get; set; } = "";

    [VectorStoreData(IsIndexed = true)]
    public string RelativePath { get; set; } = "";

    [VectorStoreData]
    public int StartLine { get; set; }

    [VectorStoreData]
    public int EndLine { get; set; }

    [VectorStoreData]
    public string Text { get; set; } = "";

    [VectorStoreVector(1536, DistanceFunction = DistanceFunction.CosineSimilarity)]
    public ReadOnlyMemory<float> Embedding { get; set; }
}

/// <summary>
/// A process-local <see cref="VectorStoreCollection{TKey,TRecord}"/>.
///
/// EN: Auto Code implements the Microsoft.Extensions.VectorData contract itself rather than taking a
/// connector dependency, for two reasons. First, a code index is disposable — it is cheaper to
/// rebuild it than to manage a database alongside a CLI. Second, and decisively, the available
/// in-memory connectors pin an older Abstractions version than Microsoft Agent Framework requires,
/// so the two cannot coexist. Implementing the contract keeps the seam intact: swapping in Qdrant,
/// Postgres or Azure AI Search later is a one-line change at the call site.
/// ID: Auto Code mengimplementasikan kontrak Microsoft.Extensions.VectorData sendiri, bukan memakai
/// connector, karena indeks kode murah dibangun ulang dan karena connector in-memory yang tersedia
/// menuntut versi Abstractions yang berbenturan dengan Microsoft Agent Framework. Dengan menjaga
/// kontraknya, mengganti ke Qdrant atau Postgres cukup satu baris di sisi pemanggil.
/// </summary>
public sealed class InMemoryCodeChunkCollection(string name) : VectorStoreCollection<string, CodeChunk>
{
    private readonly Dictionary<string, CodeChunk> _records = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private bool _exists;

    public override string Name => name;

    public override Task<bool> CollectionExistsAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
            return Task.FromResult(_exists);
    }

    public override Task EnsureCollectionExistsAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
            _exists = true;

        return Task.CompletedTask;
    }

    public override Task EnsureCollectionDeletedAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _records.Clear();
            _exists = false;
        }

        return Task.CompletedTask;
    }

    public override Task UpsertAsync(CodeChunk record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        lock (_gate)
        {
            _exists = true;
            _records[record.Id] = record;
        }

        return Task.CompletedTask;
    }

    public override Task UpsertAsync(IEnumerable<CodeChunk> records, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(records);

        lock (_gate)
        {
            _exists = true;
            foreach (var record in records)
                _records[record.Id] = record;
        }

        return Task.CompletedTask;
    }

    public override Task<CodeChunk?> GetAsync(
        string key,
        RecordRetrievalOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
            return Task.FromResult(_records.GetValueOrDefault(key));
    }

    public override async IAsyncEnumerable<CodeChunk> GetAsync(
        IEnumerable<string> keys,
        RecordRetrievalOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var key in keys)
        {
            CodeChunk? record;
            lock (_gate)
                record = _records.GetValueOrDefault(key);

            if (record is not null)
                yield return record;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    public override async IAsyncEnumerable<CodeChunk> GetAsync(
        Expression<Func<CodeChunk, bool>> filter,
        int top,
        FilteredRecordRetrievalOptions<CodeChunk>? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var predicate = filter.Compile();

        List<CodeChunk> snapshot;
        lock (_gate)
            snapshot = [.. _records.Values];

        foreach (var record in snapshot.Where(predicate).Take(top))
            yield return record;

        await Task.CompletedTask.ConfigureAwait(false);
    }

    public override Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        lock (_gate)
            _records.Remove(key);

        return Task.CompletedTask;
    }

    public override Task DeleteAsync(IEnumerable<string> keys, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            foreach (var key in keys)
                _records.Remove(key);
        }

        return Task.CompletedTask;
    }

    public override async IAsyncEnumerable<VectorSearchResult<CodeChunk>> SearchAsync<TInput>(
        TInput searchValue,
        int top,
        VectorSearchOptions<CodeChunk>? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (searchValue is not ReadOnlyMemory<float> query)
        {
            throw new NotSupportedException(
                $"{nameof(InMemoryCodeChunkCollection)} searches on a pre-computed ReadOnlyMemory<float>. " +
                $"Embed the query first; '{typeof(TInput).Name}' cannot be embedded here.");
        }

        List<CodeChunk> snapshot;
        lock (_gate)
            snapshot = [.. _records.Values];

        var filter = options?.Filter?.Compile();

        var ranked = snapshot
            .Where(chunk => chunk.Embedding.Length == query.Length && (filter is null || filter(chunk)))
            .Select(chunk => new VectorSearchResult<CodeChunk>(
                chunk,
                TensorPrimitives.CosineSimilarity(query.Span, chunk.Embedding.Span)))
            .OrderByDescending(result => result.Score)
            .Skip(options?.Skip ?? 0)
            .Take(top);

        foreach (var result in ranked)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return result;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    public override object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
    }

    /// <summary>Number of indexed chunks.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
                return _records.Count;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (_gate)
                _records.Clear();
        }

        base.Dispose(disposing);
    }
}
