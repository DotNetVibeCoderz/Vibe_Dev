using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Marbots.Abstractions;
using Microsoft.Data.Sqlite;

namespace Marbots.Storage;

/// <summary>JSON document table keyed by (kind, id). Suited to configuration-like entities.</summary>
public sealed class SqliteDocumentStore<T>(SqliteDatabase db, string kind, JsonTypeInfo<T> typeInfo, Func<T, string> idOf) : IDocumentStore<T>
    where T : class
{
    public async Task<T?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var c = await db.OpenAsync(cancellationToken);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT json FROM documents WHERE kind=$k AND id=$i";
        cmd.Parameters.AddWithValue("$k", kind);
        cmd.Parameters.AddWithValue("$i", id);
        var json = (string?)await cmd.ExecuteScalarAsync(cancellationToken);
        return json is null ? null : JsonSerializer.Deserialize(json, typeInfo);
    }

    public async Task<IReadOnlyList<T>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var c = await db.OpenAsync(cancellationToken);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT json FROM documents WHERE kind=$k ORDER BY updated_at DESC";
        cmd.Parameters.AddWithValue("$k", kind);
        var list = new List<T>();
        await using var r = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await r.ReadAsync(cancellationToken))
        {
            var item = JsonSerializer.Deserialize(r.GetString(0), typeInfo);
            if (item is not null) list.Add(item);
        }
        return list;
    }

    public async Task UpsertAsync(T item, CancellationToken cancellationToken = default)
    {
        await using var c = await db.OpenAsync(cancellationToken);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO documents(kind,id,json,updated_at) VALUES($k,$i,$j,$u)
            ON CONFLICT(kind,id) DO UPDATE SET json=excluded.json, updated_at=excluded.updated_at
            """;
        cmd.Parameters.AddWithValue("$k", kind);
        cmd.Parameters.AddWithValue("$i", idOf(item));
        cmd.Parameters.AddWithValue("$j", JsonSerializer.Serialize(item, typeInfo));
        cmd.Parameters.AddWithValue("$u", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var c = await db.OpenAsync(cancellationToken);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM documents WHERE kind=$k AND id=$i";
        cmd.Parameters.AddWithValue("$k", kind);
        cmd.Parameters.AddWithValue("$i", id);
        return await cmd.ExecuteNonQueryAsync(cancellationToken) > 0;
    }
}

public sealed class SqliteMessageStore(SqliteDatabase db) : IMessageStore, IDisposable
{
    public void Dispose() => _gate.Dispose();

    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<ChatMessage> AppendAsync(ChatMessage message, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(message.Id)) message.Id = Ids.New("msg");
        // Sequence allocation must be atomic per thread; a process-wide gate is enough for a local-first store.
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var c = await db.OpenAsync(cancellationToken);
            await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(cancellationToken);
            await using (var q = c.CreateCommand())
            {
                q.Transaction = tx;
                q.CommandText = "SELECT COALESCE(MAX(seq),0) FROM messages WHERE thread_id=$t";
                q.Parameters.AddWithValue("$t", message.ThreadId);
                message.Seq = (long)(await q.ExecuteScalarAsync(cancellationToken))! + 1;
            }
            await using (var cmd = c.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO messages(thread_id,seq,json) VALUES($t,$s,$j)";
                cmd.Parameters.AddWithValue("$t", message.ThreadId);
                cmd.Parameters.AddWithValue("$s", message.Seq);
                cmd.Parameters.AddWithValue("$j", JsonSerializer.Serialize(message, MarbotsJsonContext.Default.ChatMessage));
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }
            await tx.CommitAsync(cancellationToken);
            return message;
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<ChatMessage>> ListAsync(string threadId, long afterSeq = 0, int limit = 500, CancellationToken cancellationToken = default)
    {
        await using var c = await db.OpenAsync(cancellationToken);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT json FROM messages WHERE thread_id=$t AND seq>$s ORDER BY seq LIMIT $l";
        cmd.Parameters.AddWithValue("$t", threadId);
        cmd.Parameters.AddWithValue("$s", afterSeq);
        cmd.Parameters.AddWithValue("$l", limit);
        var list = new List<ChatMessage>();
        await using var r = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await r.ReadAsync(cancellationToken))
            list.Add(JsonSerializer.Deserialize(r.GetString(0), MarbotsJsonContext.Default.ChatMessage)!);
        return list;
    }

    public async Task<int> CountAsync(string threadId, CancellationToken cancellationToken = default)
    {
        await using var c = await db.OpenAsync(cancellationToken);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM messages WHERE thread_id=$t";
        cmd.Parameters.AddWithValue("$t", threadId);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken));
    }

    public async Task DeleteThreadAsync(string threadId, CancellationToken cancellationToken = default)
    {
        await using var c = await db.OpenAsync(cancellationToken);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM messages WHERE thread_id=$t";
        cmd.Parameters.AddWithValue("$t", threadId);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}

public sealed class SqliteEventStore(SqliteDatabase db) : IEventStore
{
    public async Task<long> AppendAsync(AgentEvent evt, CancellationToken cancellationToken = default)
    {
        await using var c = await db.OpenAsync(cancellationToken);
        await using var cmd = c.CreateCommand();
        // The id column is authoritative; JSON is stored without it and re-attached on read.
        evt.Id = 0;
        cmd.CommandText = "INSERT INTO events(thread_id,task_id,ts,json) VALUES($th,$ta,$ts,$j); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$th", (object?)evt.ThreadId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ta", (object?)evt.TaskId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ts", evt.Timestamp.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$j", JsonSerializer.Serialize(evt, MarbotsJsonContext.Default.AgentEvent));
        evt.Id = (long)(await cmd.ExecuteScalarAsync(cancellationToken))!;
        return evt.Id;
    }

    public async Task<IReadOnlyList<AgentEvent>> ListAsync(string? threadId, string? taskId, long afterId, int limit, CancellationToken cancellationToken = default)
    {
        await using var c = await db.OpenAsync(cancellationToken);
        await using var cmd = c.CreateCommand();
        var sb = new StringBuilder("SELECT id, json FROM events WHERE id>$a");
        if (threadId is not null) { sb.Append(" AND thread_id=$th"); cmd.Parameters.AddWithValue("$th", threadId); }
        if (taskId is not null) { sb.Append(" AND task_id=$ta"); cmd.Parameters.AddWithValue("$ta", taskId); }
        sb.Append(" ORDER BY id LIMIT $l");
        cmd.CommandText = sb.ToString();
        cmd.Parameters.AddWithValue("$a", afterId);
        cmd.Parameters.AddWithValue("$l", limit);
        return await ReadAsync(cmd, cancellationToken);
    }

    public async Task<IReadOnlyList<AgentEvent>> RecentAsync(int limit, CancellationToken cancellationToken = default)
    {
        await using var c = await db.OpenAsync(cancellationToken);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, json FROM (SELECT id, json FROM events ORDER BY id DESC LIMIT $l) ORDER BY id";
        cmd.Parameters.AddWithValue("$l", limit);
        return await ReadAsync(cmd, cancellationToken);
    }

    private static async Task<IReadOnlyList<AgentEvent>> ReadAsync(SqliteCommand cmd, CancellationToken ct)
    {
        var list = new List<AgentEvent>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var e = JsonSerializer.Deserialize(r.GetString(1), MarbotsJsonContext.Default.AgentEvent)!;
            e.Id = r.GetInt64(0);
            list.Add(e);
        }
        return list;
    }
}

/// <summary>
/// Long-term memory backed by SQLite FTS5 (BM25) with owner-based permission filtering,
/// confidence/recency re-ranking and de-duplication. Vector retrieval can be layered on via a separate store.
/// </summary>
public sealed class SqliteMemoryStore(SqliteDatabase db) : IMemoryStore
{
    public async ValueTask WriteAsync(MemoryRecord record, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(record.Id)) record.Id = Ids.New("mem");
        await using var c = await db.OpenAsync(cancellationToken);
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(cancellationToken);
        await using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO memories(id,owner,json,created_at) VALUES($i,$o,$j,$c)
                ON CONFLICT(id) DO UPDATE SET json=excluded.json, owner=excluded.owner;
                SELECT rid FROM memories WHERE id=$i;
                """;
            cmd.Parameters.AddWithValue("$i", record.Id);
            cmd.Parameters.AddWithValue("$o", record.Owner);
            cmd.Parameters.AddWithValue("$j", JsonSerializer.Serialize(record, MarbotsJsonContext.Default.MemoryRecord));
            cmd.Parameters.AddWithValue("$c", record.CreatedAt.ToUnixTimeMilliseconds());
            var rid = (long)(await cmd.ExecuteScalarAsync(cancellationToken))!;
            await using var fts = c.CreateCommand();
            fts.Transaction = tx;
            fts.CommandText = "INSERT OR REPLACE INTO memories_fts(rowid,content,tags) VALUES($r,$c,$t)";
            fts.Parameters.AddWithValue("$r", rid);
            fts.Parameters.AddWithValue("$c", record.Content);
            fts.Parameters.AddWithValue("$t", string.Join(' ', record.Tags));
            await fts.ExecuteNonQueryAsync(cancellationToken);
        }
        await tx.CommitAsync(cancellationToken);
    }

    public async ValueTask<IReadOnlyList<MemoryMatch>> SearchAsync(MemoryQuery query, CancellationToken cancellationToken = default)
    {
        var match = BuildFtsQuery(query.Text);
        if (match.Length == 0 || query.Owners.Count == 0) return [];
        await using var c = await db.OpenAsync(cancellationToken);
        await using var cmd = c.CreateCommand();
        var owners = new StringBuilder();
        for (var i = 0; i < query.Owners.Count; i++)
        {
            if (i > 0) owners.Append(',');
            owners.Append("$o").Append(i);
            cmd.Parameters.AddWithValue("$o" + i, query.Owners[i]);
        }
        cmd.CommandText = $"""
            SELECT m.json, bm25(memories_fts) AS score
            FROM memories_fts JOIN memories m ON m.rid = memories_fts.rowid
            WHERE memories_fts MATCH $q AND m.owner IN ({owners})
            ORDER BY score LIMIT $l
            """;
        cmd.Parameters.AddWithValue("$q", match);
        cmd.Parameters.AddWithValue("$l", query.Limit * 3);
        var now = DateTimeOffset.UtcNow;
        var results = new List<MemoryMatch>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var r = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await r.ReadAsync(cancellationToken))
        {
            var rec = JsonSerializer.Deserialize(r.GetString(0), MarbotsJsonContext.Default.MemoryRecord)!;
            if (rec.ExpiresAt is { } exp && exp < now) continue;
            if (!seen.Add(rec.Content.Trim())) continue;
            var ageDays = Math.Max(0, (now - rec.CreatedAt).TotalDays);
            // bm25 is lower-is-better; convert and blend with confidence and a gentle recency decay.
            var score = -r.GetDouble(1) * (0.5 + rec.Confidence / 2) * (1.0 / (1.0 + ageDays / 90.0));
            results.Add(new MemoryMatch(rec, score));
        }
        return results.OrderByDescending(m => m.Score).Take(query.Limit).ToList();
    }

    public async ValueTask<IReadOnlyList<MemoryRecord>> ListAsync(string owner, int limit = 200, CancellationToken cancellationToken = default)
    {
        await using var c = await db.OpenAsync(cancellationToken);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT json FROM memories WHERE owner=$o ORDER BY created_at DESC LIMIT $l";
        cmd.Parameters.AddWithValue("$o", owner);
        cmd.Parameters.AddWithValue("$l", limit);
        var list = new List<MemoryRecord>();
        await using var r = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await r.ReadAsync(cancellationToken))
            list.Add(JsonSerializer.Deserialize(r.GetString(0), MarbotsJsonContext.Default.MemoryRecord)!);
        return list;
    }

    public async ValueTask<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var c = await db.OpenAsync(cancellationToken);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM memories_fts WHERE rowid=(SELECT rid FROM memories WHERE id=$i); DELETE FROM memories WHERE id=$i;";
        cmd.Parameters.AddWithValue("$i", id);
        return await cmd.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async ValueTask<int> DeleteOwnerAsync(string owner, CancellationToken cancellationToken = default)
    {
        await using var c = await db.OpenAsync(cancellationToken);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM memories_fts WHERE rowid IN (SELECT rid FROM memories WHERE owner=$o); DELETE FROM memories WHERE owner=$o;";
        cmd.Parameters.AddWithValue("$o", owner);
        return await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Turns free text into a safe FTS5 OR-query of quoted terms (no operator injection).</summary>
    public static string BuildFtsQuery(string text)
    {
        var sb = new StringBuilder();
        var terms = 0;
        foreach (var raw in text.Split([' ', '\t', '\n', '\r', ',', '.', ';', ':', '?', '!', '(', ')', '"', '\'', '/', '\\', '-', '_', '*'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (raw.Length < 3 || terms >= 16) continue;
            if (sb.Length > 0) sb.Append(" OR ");
            sb.Append('"').Append(raw.ToLowerInvariant()).Append('"');
            terms++;
        }
        return sb.ToString();
    }
}
