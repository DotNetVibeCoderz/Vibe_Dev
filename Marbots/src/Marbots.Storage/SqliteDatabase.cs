using Microsoft.Data.Sqlite;

namespace Marbots.Storage;

/// <summary>Owns the SQLite connection string and schema. Connections are pooled by Microsoft.Data.Sqlite.</summary>
public sealed class SqliteDatabase
{
    public string ConnectionString { get; }
    public string FilePath { get; }

    public SqliteDatabase(string filePath)
    {
        FilePath = Path.GetFullPath(filePath);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        ConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = FilePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
            DefaultTimeout = 30,
        }.ToString();
        Migrate();
    }

    public SqliteConnection Open()
    {
        var c = new SqliteConnection(ConnectionString);
        c.Open();
        return c;
    }

    public async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var c = new SqliteConnection(ConnectionString);
        await c.OpenAsync(ct).ConfigureAwait(false);
        return c;
    }

    private void Migrate()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            CREATE TABLE IF NOT EXISTS documents(
                kind TEXT NOT NULL,
                id TEXT NOT NULL,
                json TEXT NOT NULL,
                updated_at INTEGER NOT NULL,
                PRIMARY KEY(kind, id)) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS messages(
                thread_id TEXT NOT NULL,
                seq INTEGER NOT NULL,
                json TEXT NOT NULL,
                PRIMARY KEY(thread_id, seq)) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS events(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                thread_id TEXT,
                task_id TEXT,
                ts INTEGER NOT NULL,
                json TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_events_thread ON events(thread_id, id);
            CREATE INDEX IF NOT EXISTS ix_events_task ON events(task_id, id);
            CREATE TABLE IF NOT EXISTS memories(
                rid INTEGER PRIMARY KEY AUTOINCREMENT,
                id TEXT NOT NULL UNIQUE,
                owner TEXT NOT NULL,
                json TEXT NOT NULL,
                created_at INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_memories_owner ON memories(owner);
            CREATE VIRTUAL TABLE IF NOT EXISTS memories_fts USING fts5(content, tags, tokenize='unicode61 remove_diacritics 2');
            """;
        cmd.ExecuteNonQuery();
    }
}
