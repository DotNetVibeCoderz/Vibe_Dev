using System.Data.Common;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Marbots.Abstractions;

namespace Marbots.Storage;

internal static class Db
{
    public static void Add(this DbCommand cmd, string name, object? value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(p);
    }

    public static DbCommand Command(this DbConnection c, string sql, DbTransaction? tx = null)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        return cmd;
    }
}

/// <summary>JSON document table keyed by (tenant, kind, id). Suited to configuration-like entities.</summary>
public sealed class DocumentStore<T>(MarbotsDatabase db, string kind, JsonTypeInfo<T> typeInfo, Func<T, string> idOf) : IDocumentStore<T>
    where T : class
{
    private readonly string _t = db.Dialect.Documents;

    public async Task<T?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var c = await db.OpenAsync(cancellationToken);
        await using var cmd = c.Command($"SELECT json FROM {_t} WHERE tenant=@tenant AND kind=@kind AND id=@id");
        cmd.Add("@tenant", db.Tenant);
        cmd.Add("@kind", kind);
        cmd.Add("@id", id);
        var json = await cmd.ExecuteScalarAsync(cancellationToken) as string;
        return json is null ? null : JsonSerializer.Deserialize(json, typeInfo);
    }

    public async Task<IReadOnlyList<T>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var c = await db.OpenAsync(cancellationToken);
        await using var cmd = c.Command($"SELECT json FROM {_t} WHERE tenant=@tenant AND kind=@kind ORDER BY updated_at DESC");
        cmd.Add("@tenant", db.Tenant);
        cmd.Add("@kind", kind);
        var list = new List<T>();
        await using var r = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await r.ReadAsync(cancellationToken))
            if (JsonSerializer.Deserialize(r.GetString(0), typeInfo) is { } item) list.Add(item);
        return list;
    }

    public async Task UpsertAsync(T item, CancellationToken cancellationToken = default)
    {
        await using var c = await db.OpenAsync(cancellationToken);
        await using var cmd = c.Command(db.Dialect.UpsertDocument);
        cmd.Add("@tenant", db.Tenant);
        cmd.Add("@kind", kind);
        cmd.Add("@id", idOf(item));
        cmd.Add("@json", JsonSerializer.Serialize(item, typeInfo));
        cmd.Add("@updated", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var c = await db.OpenAsync(cancellationToken);
        await using var cmd = c.Command($"DELETE FROM {_t} WHERE tenant=@tenant AND kind=@kind AND id=@id");
        cmd.Add("@tenant", db.Tenant);
        cmd.Add("@kind", kind);
        cmd.Add("@id", id);
        return await cmd.ExecuteNonQueryAsync(cancellationToken) > 0;
    }
}

public sealed class MessageStore(MarbotsDatabase db) : IMessageStore, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _t = db.Dialect.Messages;

    public void Dispose() => _gate.Dispose();

    public async Task<ChatMessage> AppendAsync(ChatMessage message, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(message.Id)) message.Id = Ids.New("msg");
        // Sequence numbers are allocated as MAX+1 inside a transaction; the (thread_id, seq) key rejects a duplicate when
        // another server instance won the race, and the insert is retried.
        await _gate.WaitAsync(cancellationToken);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    await using var c = await db.OpenAsync(cancellationToken);
                    await using var tx = await c.BeginTransactionAsync(cancellationToken);
                    await using (var q = c.Command($"SELECT COALESCE(MAX(seq),0) FROM {_t} WHERE thread_id=@thread", tx))
                    {
                        q.Add("@thread", message.ThreadId);
                        message.Seq = Convert.ToInt64(await q.ExecuteScalarAsync(cancellationToken)) + 1;
                    }
                    await using (var cmd = c.Command($"INSERT INTO {_t}(tenant,thread_id,seq,json) VALUES(@tenant,@thread,@seq,@json)", tx))
                    {
                        cmd.Add("@tenant", db.Tenant);
                        cmd.Add("@thread", message.ThreadId);
                        cmd.Add("@seq", message.Seq);
                        cmd.Add("@json", JsonSerializer.Serialize(message, MarbotsJsonContext.Default.ChatMessage));
                        await cmd.ExecuteNonQueryAsync(cancellationToken);
                    }
                    await tx.CommitAsync(cancellationToken);
                    return message;
                }
                catch (DbException) when (attempt < 8)
                {
                    await Task.Delay(15 * (attempt + 1), cancellationToken);
                }
            }
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<ChatMessage>> ListAsync(string threadId, long afterSeq = 0, int limit = 500, CancellationToken cancellationToken = default)
    {
        await using var c = await db.OpenAsync(cancellationToken);
        await using var cmd = c.Command($"SELECT json FROM {_t} WHERE tenant=@tenant AND thread_id=@thread AND seq>@after ORDER BY seq" + db.Dialect.Limit("@limit"));
        cmd.Add("@tenant", db.Tenant);
        cmd.Add("@thread", threadId);
        cmd.Add("@after", afterSeq);
        cmd.Add("@limit", limit);
        var list = new List<ChatMessage>();
        await using var r = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await r.ReadAsync(cancellationToken))
            list.Add(JsonSerializer.Deserialize(r.GetString(0), MarbotsJsonContext.Default.ChatMessage)!);
        return list;
    }

    public async Task<int> CountAsync(string threadId, CancellationToken cancellationToken = default)
    {
        await using var c = await db.OpenAsync(cancellationToken);
        await using var cmd = c.Command($"SELECT COUNT(*) FROM {_t} WHERE tenant=@tenant AND thread_id=@thread");
        cmd.Add("@tenant", db.Tenant);
        cmd.Add("@thread", threadId);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken));
    }

    public async Task DeleteThreadAsync(string threadId, CancellationToken cancellationToken = default)
    {
        await using var c = await db.OpenAsync(cancellationToken);
        await using var cmd = c.Command($"DELETE FROM {_t} WHERE tenant=@tenant AND thread_id=@thread");
        cmd.Add("@tenant", db.Tenant);
        cmd.Add("@thread", threadId);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}

public sealed class EventStore(MarbotsDatabase db) : IEventStore
{
    private readonly string _t = db.Dialect.Events;

    public async Task<long> AppendAsync(AgentEvent evt, CancellationToken cancellationToken = default)
    {
        await using var c = await db.OpenAsync(cancellationToken);
        // The id column is authoritative; JSON is stored without it and re-attached on read.
        evt.Id = 0;
        await using var cmd = c.Command(db.Dialect.InsertEvent);
        cmd.Add("@tenant", db.Tenant);
        cmd.Add("@thread", evt.ThreadId);
        cmd.Add("@task", evt.TaskId);
        cmd.Add("@ts", evt.Timestamp.ToUnixTimeMilliseconds());
        cmd.Add("@json", JsonSerializer.Serialize(evt, MarbotsJsonContext.Default.AgentEvent));
        evt.Id = Convert.ToInt64(await cmd.ExecuteScalarAsync(cancellationToken));
        return evt.Id;
    }

    public async Task<IReadOnlyList<AgentEvent>> ListAsync(string? threadId, string? taskId, long afterId, int limit, CancellationToken cancellationToken = default)
    {
        await using var c = await db.OpenAsync(cancellationToken);
        await using var cmd = c.CreateCommand();
        var sb = new StringBuilder($"SELECT id, json FROM {_t} WHERE tenant=@tenant AND id>@after");
        if (threadId is not null) { sb.Append(" AND thread_id=@thread"); cmd.Add("@thread", threadId); }
        if (taskId is not null) { sb.Append(" AND task_id=@task"); cmd.Add("@task", taskId); }
        sb.Append(" ORDER BY id").Append(db.Dialect.Limit("@limit"));
        cmd.CommandText = sb.ToString();
        cmd.Add("@tenant", db.Tenant);
        cmd.Add("@after", afterId);
        cmd.Add("@limit", limit);
        return await ReadAsync(cmd, cancellationToken);
    }

    public async Task<IReadOnlyList<AgentEvent>> RecentAsync(int limit, CancellationToken cancellationToken = default)
    {
        await using var c = await db.OpenAsync(cancellationToken);
        await using var cmd = c.Command($"SELECT id, json FROM {_t} WHERE tenant=@tenant ORDER BY id DESC" + db.Dialect.Limit("@limit"));
        cmd.Add("@tenant", db.Tenant);
        cmd.Add("@limit", limit);
        var list = await ReadAsync(cmd, cancellationToken);
        return list.OrderBy(e => e.Id).ToList();
    }

    private static async Task<List<AgentEvent>> ReadAsync(DbCommand cmd, CancellationToken ct)
    {
        var list = new List<AgentEvent>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var e = JsonSerializer.Deserialize(r.GetString(1), MarbotsJsonContext.Default.AgentEvent)!;
            e.Id = Convert.ToInt64(r.GetValue(0));
            list.Add(e);
        }
        return list;
    }
}

/// <summary>
/// Long-term memory with hybrid retrieval: lexical BM25 (SQLite FTS5, or BM25 computed over database candidates on
/// PostgreSQL / SQL Server / MySQL) fused with vector similarity (reciprocal rank fusion), then re-ranked by confidence
/// and recency. Owner filtering keeps bots to the memories they may read.
/// </summary>
public sealed class MemoryStore(MarbotsDatabase db, IEmbeddingProvider? embedder = null) : IMemoryStore
{
    private readonly string _t = db.Dialect.Memories;
    private bool Fts => db.Provider == DatabaseProvider.Sqlite;

    public async ValueTask WriteAsync(MemoryRecord record, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(record.Id)) record.Id = Ids.New("mem");
        var vector = embedder is null ? null : await embedder.EmbedAsync(record.Content, cancellationToken);
        await using var c = await db.OpenAsync(cancellationToken);
        await using var tx = await c.BeginTransactionAsync(cancellationToken);
        long rid;
        await using (var cmd = c.Command(db.Dialect.UpsertMemory, tx))
        {
            cmd.Add("@id", record.Id);
            cmd.Add("@tenant", db.Tenant);
            cmd.Add("@owner", record.Owner);
            cmd.Add("@content", record.Content);
            cmd.Add("@json", JsonSerializer.Serialize(record, MarbotsJsonContext.Default.MemoryRecord));
            cmd.Add("@created", record.CreatedAt.ToUnixTimeMilliseconds());
            cmd.Add("@embedding", vector is null ? null : Vectors.Encode(embedder!.Model, vector));
            rid = Convert.ToInt64(await cmd.ExecuteScalarAsync(cancellationToken));
        }
        if (Fts)
        {
            await using var fts = c.Command("INSERT OR REPLACE INTO memories_fts(rowid,content,tags) VALUES(@rid,@content,@tags)", tx);
            fts.Add("@rid", rid);
            fts.Add("@content", record.Content);
            fts.Add("@tags", string.Join(' ', record.Tags));
            await fts.ExecuteNonQueryAsync(cancellationToken);
        }
        await tx.CommitAsync(cancellationToken);
    }

    public async ValueTask<IReadOnlyList<MemoryMatch>> SearchAsync(MemoryQuery query, CancellationToken cancellationToken = default)
    {
        var terms = Terms(query.Text);
        if (query.Owners.Count == 0 || terms.Count == 0 && embedder is null) return [];
        await using var c = await db.OpenAsync(cancellationToken);
        var lexical = terms.Count == 0 ? [] : Fts
            ? await FtsCandidatesAsync(c, terms, query, cancellationToken)
            : await LikeCandidatesAsync(c, terms, query, cancellationToken);
        var semantic = embedder is null ? [] : await VectorCandidatesAsync(c, query, cancellationToken);

        // Reciprocal rank fusion of both lists, then confidence and a gentle recency decay.
        var fused = new Dictionary<string, (MemoryRecord Record, double Score)>();
        void Fuse(List<MemoryRecord> ranked)
        {
            for (var i = 0; i < ranked.Count; i++)
            {
                var rec = ranked[i];
                var add = 1.0 / (60 + i + 1);
                fused[rec.Id] = fused.TryGetValue(rec.Id, out var cur) ? (cur.Record, cur.Score + add) : (rec, add);
            }
        }
        Fuse(lexical);
        Fuse(semantic);
        var now = DateTimeOffset.UtcNow;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return fused.Values
            .Where(x => x.Record.ExpiresAt is not { } exp || exp >= now)
            .Select(x =>
            {
                var ageDays = Math.Max(0, (now - x.Record.CreatedAt).TotalDays);
                return new MemoryMatch(x.Record, x.Score * 100 * (0.5 + x.Record.Confidence / 2) * (1.0 / (1.0 + ageDays / 90.0)));
            })
            .OrderByDescending(m => m.Score)
            .Where(m => seen.Add(m.Record.Content.Trim()))
            .Take(query.Limit)
            .ToList();
    }

    private string OwnerList(DbCommand cmd, IReadOnlyList<string> owners)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < owners.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("@o").Append(i);
            cmd.Add("@o" + i, owners[i]);
        }
        return sb.ToString();
    }

    private async Task<List<MemoryRecord>> FtsCandidatesAsync(DbConnection c, List<string> terms, MemoryQuery query, CancellationToken ct)
    {
        await using var cmd = c.CreateCommand();
        var owners = OwnerList(cmd, query.Owners);
        cmd.CommandText = $"""
            SELECT m.json FROM memories_fts JOIN memories m ON m.rid = memories_fts.rowid
            WHERE memories_fts MATCH @q AND m.tenant=@tenant AND m.owner IN ({owners})
            ORDER BY bm25(memories_fts) LIMIT @limit
            """;
        cmd.Add("@q", string.Join(" OR ", terms.Select(t => $"\"{t}\"")));
        cmd.Add("@tenant", db.Tenant);
        cmd.Add("@limit", query.Limit * 4);
        return await ReadRecordsAsync(cmd, ct);
    }

    /// <summary>Databases without FTS5: fetch rows containing any term, then rank them with BM25 in process.</summary>
    private async Task<List<MemoryRecord>> LikeCandidatesAsync(DbConnection c, List<string> terms, MemoryQuery query, CancellationToken ct)
    {
        await using var cmd = c.CreateCommand();
        var owners = OwnerList(cmd, query.Owners);
        var likes = new StringBuilder();
        for (var i = 0; i < terms.Count; i++)
        {
            if (i > 0) likes.Append(" OR ");
            likes.Append(db.Dialect.LikeLower("content", "@t" + i));
            cmd.Add("@t" + i, "%" + terms[i] + "%");
        }
        cmd.CommandText = $"SELECT json FROM {_t} WHERE tenant=@tenant AND owner IN ({owners}) AND ({likes}) ORDER BY created_at DESC" + db.Dialect.Limit("@limit");
        cmd.Add("@tenant", db.Tenant);
        cmd.Add("@limit", 400);
        var candidates = await ReadRecordsAsync(cmd, ct);
        return Bm25.Rank(candidates, terms).Take(query.Limit * 4).ToList();
    }

    private async Task<List<MemoryRecord>> VectorCandidatesAsync(DbConnection c, MemoryQuery query, CancellationToken ct)
    {
        var q = await embedder!.EmbedAsync(query.Text, ct);
        if (q is null) return [];
        await using var cmd = c.CreateCommand();
        var owners = OwnerList(cmd, query.Owners);
        cmd.CommandText = $"SELECT json, embedding FROM {_t} WHERE tenant=@tenant AND owner IN ({owners}) AND embedding IS NOT NULL ORDER BY created_at DESC" + db.Dialect.Limit("@limit");
        cmd.Add("@tenant", db.Tenant);
        cmd.Add("@limit", 5000);
        var scored = new List<(MemoryRecord, double)>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            if (Vectors.Decode((byte[])r.GetValue(1), embedder.Model) is not { } v || v.Length != q.Length) continue;
            var sim = Vectors.Cosine(q, v);
            if (sim >= Vectors.MinSimilarity(embedder.Model)) scored.Add((JsonSerializer.Deserialize(r.GetString(0), MarbotsJsonContext.Default.MemoryRecord)!, sim));
        }
        return scored.OrderByDescending(x => x.Item2).Take(query.Limit * 4).Select(x => x.Item1).ToList();
    }

    private static async Task<List<MemoryRecord>> ReadRecordsAsync(DbCommand cmd, CancellationToken ct)
    {
        var list = new List<MemoryRecord>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(JsonSerializer.Deserialize(r.GetString(0), MarbotsJsonContext.Default.MemoryRecord)!);
        return list;
    }

    public async ValueTask<IReadOnlyList<MemoryRecord>> ListAsync(string owner, int limit = 200, CancellationToken cancellationToken = default)
    {
        await using var c = await db.OpenAsync(cancellationToken);
        await using var cmd = c.Command($"SELECT json FROM {_t} WHERE tenant=@tenant AND owner=@owner ORDER BY created_at DESC" + db.Dialect.Limit("@limit"));
        cmd.Add("@tenant", db.Tenant);
        cmd.Add("@owner", owner);
        cmd.Add("@limit", limit);
        return await ReadRecordsAsync(cmd, cancellationToken);
    }

    public async ValueTask<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var c = await db.OpenAsync(cancellationToken);
        if (Fts)
        {
            await using var f = c.Command("DELETE FROM memories_fts WHERE rowid=(SELECT rid FROM memories WHERE id=@id AND tenant=@tenant)");
            f.Add("@id", id);
            f.Add("@tenant", db.Tenant);
            await f.ExecuteNonQueryAsync(cancellationToken);
        }
        await using var cmd = c.Command($"DELETE FROM {_t} WHERE id=@id AND tenant=@tenant");
        cmd.Add("@id", id);
        cmd.Add("@tenant", db.Tenant);
        return await cmd.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async ValueTask<int> DeleteOwnerAsync(string owner, CancellationToken cancellationToken = default)
    {
        await using var c = await db.OpenAsync(cancellationToken);
        if (Fts)
        {
            await using var f = c.Command("DELETE FROM memories_fts WHERE rowid IN (SELECT rid FROM memories WHERE owner=@owner AND tenant=@tenant)");
            f.Add("@owner", owner);
            f.Add("@tenant", db.Tenant);
            await f.ExecuteNonQueryAsync(cancellationToken);
        }
        await using var cmd = c.Command($"DELETE FROM {_t} WHERE owner=@owner AND tenant=@tenant");
        cmd.Add("@owner", owner);
        cmd.Add("@tenant", db.Tenant);
        return await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Lower-cased search terms (3+ characters, at most 16). Quoted for FTS5, so operators cannot be injected.</summary>
    public static List<string> Terms(string text) =>
        text.Split([' ', '\t', '\n', '\r', ',', '.', ';', ':', '?', '!', '(', ')', '"', '\'', '/', '\\', '-', '_', '*', '%'], StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length >= 3).Select(t => t.ToLowerInvariant()).Distinct().Take(16).ToList();

    /// <summary>The FTS5 query built from <paramref name="text"/> (kept for callers and tests).</summary>
    public static string BuildFtsQuery(string text) => string.Join(" OR ", Terms(text).Select(t => $"\"{t}\""));
}

/// <summary>BM25 over a candidate set (k1 = 1.2, b = 0.75), used where the database has no BM25 of its own.</summary>
public static class Bm25
{
    public static IEnumerable<MemoryRecord> Rank(IReadOnlyList<MemoryRecord> docs, IReadOnlyList<string> terms)
    {
        if (docs.Count == 0) return [];
        var tokens = docs.Select(d => Tokenize(d.Content + " " + string.Join(' ', d.Tags))).ToList();
        var avg = Math.Max(1, tokens.Average(t => t.Count));
        var df = terms.ToDictionary(t => t, t => tokens.Count(doc => doc.Any(w => w.Contains(t, StringComparison.Ordinal))));
        return docs.Select((d, i) =>
        {
            var words = tokens[i];
            double score = 0;
            foreach (var t in terms)
            {
                var tf = words.Count(w => w.Contains(t, StringComparison.Ordinal));
                if (tf == 0) continue;
                var idf = Math.Log(1 + (docs.Count - df[t] + 0.5) / (df[t] + 0.5));
                score += idf * tf * 2.2 / (tf + 1.2 * (0.25 + 0.75 * words.Count / avg));
            }
            return (d, score);
        }).Where(x => x.score > 0).OrderByDescending(x => x.score).Select(x => x.d);
    }

    private static List<string> Tokenize(string text) =>
        text.ToLowerInvariant().Split([' ', '\t', '\n', '\r', ',', '.', ';', ':', '?', '!', '(', ')', '"', '\'', '/', '\\', '-', '_'], StringSplitOptions.RemoveEmptyEntries).ToList();
}

/// <summary>Vector encoding: a small header naming the embedding model, then little-endian float32 values.</summary>
public static class Vectors
{
    /// <summary>Below this cosine a vector match is noise: hashing embeddings sit near 0 for unrelated text, neural ones higher.</summary>
    public static double MinSimilarity(string model) => model.StartsWith("hash", StringComparison.Ordinal) ? 0.08 : 0.25;

    public static byte[] Encode(string model, float[] vector)
    {
        var name = Encoding.UTF8.GetBytes(model);
        var bytes = new byte[1 + name.Length + vector.Length * 4];
        bytes[0] = (byte)Math.Min(name.Length, 255);
        name.AsSpan(0, bytes[0]).CopyTo(bytes.AsSpan(1));
        MemoryMarshal.AsBytes(vector.AsSpan()).CopyTo(bytes.AsSpan(1 + bytes[0]));
        return bytes;
    }

    /// <summary>The vector, or null when it was made by a different model.</summary>
    public static float[]? Decode(byte[] bytes, string model)
    {
        if (bytes.Length < 1) return null;
        var n = bytes[0];
        if (bytes.Length < 1 + n || Encoding.UTF8.GetString(bytes, 1, n) != model) return null;
        return MemoryMarshal.Cast<byte, float>(bytes.AsSpan(1 + n)).ToArray();
    }

    public static double Cosine(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        return na == 0 || nb == 0 ? 0 : dot / Math.Sqrt(na * nb);
    }
}

// Names kept for code written against the SQLite-only storage.
public sealed class SqliteMemoryStore(MarbotsDatabase db, IEmbeddingProvider? embedder = null) : IMemoryStore
{
    private readonly MemoryStore _inner = new(db, embedder);
    public ValueTask WriteAsync(MemoryRecord record, CancellationToken cancellationToken = default) => _inner.WriteAsync(record, cancellationToken);
    public ValueTask<IReadOnlyList<MemoryMatch>> SearchAsync(MemoryQuery query, CancellationToken cancellationToken = default) => _inner.SearchAsync(query, cancellationToken);
    public ValueTask<IReadOnlyList<MemoryRecord>> ListAsync(string owner, int limit = 200, CancellationToken cancellationToken = default) => _inner.ListAsync(owner, limit, cancellationToken);
    public ValueTask<bool> DeleteAsync(string id, CancellationToken cancellationToken = default) => _inner.DeleteAsync(id, cancellationToken);
    public ValueTask<int> DeleteOwnerAsync(string owner, CancellationToken cancellationToken = default) => _inner.DeleteOwnerAsync(owner, cancellationToken);
    public static string BuildFtsQuery(string text) => MemoryStore.BuildFtsQuery(text);
}
