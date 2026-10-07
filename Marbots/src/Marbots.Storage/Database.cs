using System.Data.Common;
using Microsoft.Data.Sqlite;

namespace Marbots.Storage;

/// <summary>Supported databases. SQLite is the local-first default; the others suit shared and multi-tenant servers.</summary>
public enum DatabaseProvider { Sqlite, PostgreSql, SqlServer, MySql }

/// <summary>Where Marbots keeps its data. Every row is stamped with <see cref="Tenant"/>.</summary>
public sealed class DatabaseOptions
{
    /// <summary>sqlite (default), postgresql, sqlserver or mysql.</summary>
    public string Provider { get; set; } = "sqlite";
    /// <summary>ADO.NET connection string; for SQLite this may be empty (the data directory's marbots.db is used).</summary>
    public string? ConnectionString { get; set; }

    public static DatabaseProvider Parse(string? provider) => (provider ?? "sqlite").Trim().ToLowerInvariant() switch
    {
        "" or "sqlite" => DatabaseProvider.Sqlite,
        "postgres" or "postgresql" or "npgsql" or "pg" => DatabaseProvider.PostgreSql,
        "sqlserver" or "mssql" or "sql-server" or "azuresql" => DatabaseProvider.SqlServer,
        "mysql" or "mariadb" => DatabaseProvider.MySql,
        var other => throw new ArgumentException($"Unknown database provider '{other}'. Use sqlite, postgresql, sqlserver or mysql."),
    };
}

/// <summary>
/// One Marbots database (connection factory + dialect + schema). All stores are tenant-scoped: they only ever read and
/// write rows whose <c>tenant</c> column equals <see cref="Tenant"/>.
/// </summary>
public class MarbotsDatabase
{
    public MarbotsDatabase(DatabaseProvider provider, string connectionString, string tenant = "default")
    {
        Provider = provider;
        ConnectionString = connectionString;
        Tenant = string.IsNullOrWhiteSpace(tenant) ? "default" : tenant;
        Dialect = SqlDialect.For(provider);
        Factory = provider switch
        {
            DatabaseProvider.Sqlite => SqliteFactory.Instance,
            DatabaseProvider.PostgreSql => Npgsql.NpgsqlFactory.Instance,
            DatabaseProvider.SqlServer => Microsoft.Data.SqlClient.SqlClientFactory.Instance,
            _ => MySqlConnector.MySqlConnectorFactory.Instance,
        };
        Migrate();
    }

    /// <summary>A database for <paramref name="options"/>; SQLite defaults to <paramref name="defaultSqliteFile"/>.</summary>
    public static MarbotsDatabase Create(DatabaseOptions options, string defaultSqliteFile, string tenant = "default")
    {
        var provider = DatabaseOptions.Parse(options.Provider);
        if (provider == DatabaseProvider.Sqlite)
            return new SqliteDatabase(string.IsNullOrWhiteSpace(options.ConnectionString) ? defaultSqliteFile : SqliteFile(options.ConnectionString), tenant);
        if (string.IsNullOrWhiteSpace(options.ConnectionString))
            throw new ArgumentException($"Marbots:Database:ConnectionString is required for {provider}.");
        return new MarbotsDatabase(provider, options.ConnectionString, tenant);
    }

    private static string SqliteFile(string cs) =>
        cs.Contains('=', StringComparison.Ordinal) ? new SqliteConnectionStringBuilder(cs).DataSource : cs;

    public DatabaseProvider Provider { get; }
    public string ConnectionString { get; }
    public string Tenant { get; }
    public SqlDialect Dialect { get; }
    public DbProviderFactory Factory { get; }

    public async Task<DbConnection> OpenAsync(CancellationToken ct)
    {
        var c = Factory.CreateConnection()!;
        c.ConnectionString = ConnectionString;
        await c.OpenAsync(ct).ConfigureAwait(false);
        return c;
    }

    public DbConnection Open()
    {
        var c = Factory.CreateConnection()!;
        c.ConnectionString = ConnectionString;
        c.Open();
        return c;
    }

    protected virtual void Migrate()
    {
        using var c = Open();
        foreach (var statement in Dialect.Schema())
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = statement;
            try { cmd.ExecuteNonQuery(); }
            catch (DbException ex) when (Dialect.IsAlreadyExists(ex)) { }
        }
    }
}

/// <summary>SQLite (WAL) database file; also migrates databases created before multi-tenancy.</summary>
public sealed class SqliteDatabase(string filePath, string tenant = "default") : MarbotsDatabase(DatabaseProvider.Sqlite, BuildConnectionString(filePath), tenant)
{
    public string FilePath { get; } = Path.GetFullPath(filePath);

    private static string BuildConnectionString(string filePath)
    {
        var full = Path.GetFullPath(filePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        return new SqliteConnectionStringBuilder
        {
            DataSource = full, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Private, Pooling = true, DefaultTimeout = 30,
        }.ToString();
    }

    protected override void Migrate()
    {
        using var c = Open();
        void Exec(string sql)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
        bool HasTable(string t)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$t";
            cmd.Parameters.Add(new SqliteParameter("$t", t));
            return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
        }
        bool HasColumn(string t, string col)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{t}') WHERE name=$c";
            cmd.Parameters.Add(new SqliteParameter("$c", col));
            return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
        }

        Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;");
        // Databases from before multi-tenancy: documents need tenant in the key; other tables get a column.
        if (HasTable("documents") && !HasColumn("documents", "tenant"))
        {
            Exec("""
                ALTER TABLE documents RENAME TO documents_v1;
                CREATE TABLE documents(tenant TEXT NOT NULL, kind TEXT NOT NULL, id TEXT NOT NULL, json TEXT NOT NULL, updated_at INTEGER NOT NULL,
                    PRIMARY KEY(tenant, kind, id)) WITHOUT ROWID;
                INSERT INTO documents(tenant, kind, id, json, updated_at) SELECT 'default', kind, id, json, updated_at FROM documents_v1;
                DROP TABLE documents_v1;
                """);
        }
        if (HasTable("messages") && !HasColumn("messages", "tenant")) Exec("ALTER TABLE messages ADD COLUMN tenant TEXT NOT NULL DEFAULT 'default'");
        if (HasTable("events") && !HasColumn("events", "tenant")) Exec("ALTER TABLE events ADD COLUMN tenant TEXT NOT NULL DEFAULT 'default'");
        if (HasTable("memories"))
        {
            if (!HasColumn("memories", "tenant")) Exec("ALTER TABLE memories ADD COLUMN tenant TEXT NOT NULL DEFAULT 'default'");
            if (!HasColumn("memories", "content"))
            {
                Exec("ALTER TABLE memories ADD COLUMN content TEXT");
                Exec("UPDATE memories SET content = json_extract(json, '$.content')");
            }
            if (!HasColumn("memories", "embedding")) Exec("ALTER TABLE memories ADD COLUMN embedding BLOB");
        }
        foreach (var statement in Dialect.Schema()) Exec(statement);
    }
}

/// <summary>The SQL that differs between databases. Parameters are always written as <c>@name</c>.</summary>
public abstract class SqlDialect
{
    public abstract DatabaseProvider Provider { get; }
    public virtual string Documents => "mb_documents";
    public virtual string Messages => "mb_messages";
    public virtual string Events => "mb_events";
    public virtual string Memories => "mb_memories";

    public abstract IEnumerable<string> Schema();

    /// <summary>A schema statement failed only because the object already exists (MySQL: duplicate index name).</summary>
    public virtual bool IsAlreadyExists(DbException ex) => false;

    /// <summary>Insert or replace a document row (@tenant, @kind, @id, @json, @updated).</summary>
    public abstract string UpsertDocument { get; }

    /// <summary>Insert an event and return its id (@tenant, @thread, @task, @ts, @json).</summary>
    public abstract string InsertEvent { get; }

    /// <summary>Insert or update a memory row (@id, @tenant, @owner, @content, @json, @created, @embedding).</summary>
    public abstract string UpsertMemory { get; }

    /// <summary>Row limit appended after ORDER BY.</summary>
    public virtual string Limit(string parameter) => $" LIMIT {parameter}";

    /// <summary>Case-insensitive LIKE on a column.</summary>
    public virtual string LikeLower(string column, string parameter) => $"LOWER({column}) LIKE {parameter}";

    public static SqlDialect For(DatabaseProvider p) => p switch
    {
        DatabaseProvider.Sqlite => new SqliteDialect(),
        DatabaseProvider.PostgreSql => new PostgreSqlDialect(),
        DatabaseProvider.SqlServer => new SqlServerDialect(),
        _ => new MySqlDialect(),
    };
}

internal sealed class SqliteDialect : SqlDialect
{
    public override DatabaseProvider Provider => DatabaseProvider.Sqlite;
    // SQLite keeps the original table names so existing local databases continue to work.
    public override string Documents => "documents";
    public override string Messages => "messages";
    public override string Events => "events";
    public override string Memories => "memories";

    public override IEnumerable<string> Schema() =>
    [
        """
        CREATE TABLE IF NOT EXISTS documents(tenant TEXT NOT NULL, kind TEXT NOT NULL, id TEXT NOT NULL, json TEXT NOT NULL, updated_at INTEGER NOT NULL,
            PRIMARY KEY(tenant, kind, id)) WITHOUT ROWID;
        CREATE TABLE IF NOT EXISTS messages(tenant TEXT NOT NULL DEFAULT 'default', thread_id TEXT NOT NULL, seq INTEGER NOT NULL, json TEXT NOT NULL,
            PRIMARY KEY(thread_id, seq)) WITHOUT ROWID;
        CREATE TABLE IF NOT EXISTS events(id INTEGER PRIMARY KEY AUTOINCREMENT, tenant TEXT NOT NULL DEFAULT 'default', thread_id TEXT, task_id TEXT,
            ts INTEGER NOT NULL, json TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS ix_events_thread ON events(thread_id, id);
        CREATE INDEX IF NOT EXISTS ix_events_task ON events(task_id, id);
        CREATE INDEX IF NOT EXISTS ix_events_tenant ON events(tenant, id);
        CREATE TABLE IF NOT EXISTS memories(rid INTEGER PRIMARY KEY AUTOINCREMENT, id TEXT NOT NULL UNIQUE, tenant TEXT NOT NULL DEFAULT 'default',
            owner TEXT NOT NULL, content TEXT, json TEXT NOT NULL, created_at INTEGER NOT NULL, embedding BLOB);
        CREATE INDEX IF NOT EXISTS ix_memories_owner ON memories(tenant, owner);
        CREATE INDEX IF NOT EXISTS ix_memories_recent ON memories(tenant, owner, created_at);
        CREATE VIRTUAL TABLE IF NOT EXISTS memories_fts USING fts5(content, tags, tokenize='unicode61 remove_diacritics 2');
        """,
    ];

    public override string UpsertDocument => """
        INSERT INTO documents(tenant,kind,id,json,updated_at) VALUES(@tenant,@kind,@id,@json,@updated)
        ON CONFLICT(tenant,kind,id) DO UPDATE SET json=excluded.json, updated_at=excluded.updated_at
        """;

    public override string InsertEvent => "INSERT INTO events(tenant,thread_id,task_id,ts,json) VALUES(@tenant,@thread,@task,@ts,@json); SELECT last_insert_rowid();";

    public override string UpsertMemory => """
        INSERT INTO memories(id,tenant,owner,content,json,created_at,embedding) VALUES(@id,@tenant,@owner,@content,@json,@created,@embedding)
        ON CONFLICT(id) DO UPDATE SET json=excluded.json, owner=excluded.owner, content=excluded.content, embedding=excluded.embedding;
        SELECT rid FROM memories WHERE id=@id;
        """;
}

internal sealed class PostgreSqlDialect : SqlDialect
{
    public override DatabaseProvider Provider => DatabaseProvider.PostgreSql;

    public override IEnumerable<string> Schema() =>
    [
        "CREATE TABLE IF NOT EXISTS mb_documents(tenant VARCHAR(64) NOT NULL, kind VARCHAR(64) NOT NULL, id VARCHAR(200) NOT NULL, json TEXT NOT NULL, updated_at BIGINT NOT NULL, PRIMARY KEY(tenant, kind, id))",
        "CREATE TABLE IF NOT EXISTS mb_messages(tenant VARCHAR(64) NOT NULL, thread_id VARCHAR(100) NOT NULL, seq BIGINT NOT NULL, json TEXT NOT NULL, PRIMARY KEY(thread_id, seq))",
        "CREATE INDEX IF NOT EXISTS ix_mb_messages_tenant ON mb_messages(tenant, thread_id)",
        "CREATE TABLE IF NOT EXISTS mb_events(id BIGSERIAL PRIMARY KEY, tenant VARCHAR(64) NOT NULL, thread_id VARCHAR(100), task_id VARCHAR(100), ts BIGINT NOT NULL, json TEXT NOT NULL)",
        "CREATE INDEX IF NOT EXISTS ix_mb_events_thread ON mb_events(tenant, thread_id, id)",
        "CREATE INDEX IF NOT EXISTS ix_mb_events_task ON mb_events(tenant, task_id, id)",
        "CREATE INDEX IF NOT EXISTS ix_mb_events_tenant ON mb_events(tenant, id)",
        "CREATE TABLE IF NOT EXISTS mb_memories(rid BIGSERIAL PRIMARY KEY, id VARCHAR(100) NOT NULL UNIQUE, tenant VARCHAR(64) NOT NULL, owner VARCHAR(100) NOT NULL, content TEXT, json TEXT NOT NULL, created_at BIGINT NOT NULL, embedding BYTEA)",
        "CREATE INDEX IF NOT EXISTS ix_mb_memories_owner ON mb_memories(tenant, owner)",
        // Recency scans of the vector search read the index instead of sorting rows with their vectors.
        "CREATE INDEX IF NOT EXISTS ix_mb_memories_recent ON mb_memories(tenant, owner, created_at)",
    ];

    public override string UpsertDocument => """
        INSERT INTO mb_documents(tenant,kind,id,json,updated_at) VALUES(@tenant,@kind,@id,@json,@updated)
        ON CONFLICT(tenant,kind,id) DO UPDATE SET json=EXCLUDED.json, updated_at=EXCLUDED.updated_at
        """;

    public override string InsertEvent => "INSERT INTO mb_events(tenant,thread_id,task_id,ts,json) VALUES(@tenant,@thread,@task,@ts,@json) RETURNING id";

    public override string UpsertMemory => """
        INSERT INTO mb_memories(id,tenant,owner,content,json,created_at,embedding) VALUES(@id,@tenant,@owner,@content,@json,@created,@embedding)
        ON CONFLICT(id) DO UPDATE SET json=EXCLUDED.json, owner=EXCLUDED.owner, content=EXCLUDED.content, embedding=EXCLUDED.embedding
        RETURNING rid
        """;
}

internal sealed class SqlServerDialect : SqlDialect
{
    public override DatabaseProvider Provider => DatabaseProvider.SqlServer;

    public override IEnumerable<string> Schema() =>
    [
        """
        IF OBJECT_ID(N'mb_documents', N'U') IS NULL
        CREATE TABLE mb_documents(tenant NVARCHAR(64) NOT NULL, kind NVARCHAR(64) NOT NULL, id NVARCHAR(200) NOT NULL, json NVARCHAR(MAX) NOT NULL,
            updated_at BIGINT NOT NULL, CONSTRAINT pk_mb_documents PRIMARY KEY(tenant, kind, id))
        """,
        """
        IF OBJECT_ID(N'mb_messages', N'U') IS NULL
        BEGIN
            CREATE TABLE mb_messages(tenant NVARCHAR(64) NOT NULL, thread_id NVARCHAR(100) NOT NULL, seq BIGINT NOT NULL, json NVARCHAR(MAX) NOT NULL,
                CONSTRAINT pk_mb_messages PRIMARY KEY(thread_id, seq));
            CREATE INDEX ix_mb_messages_tenant ON mb_messages(tenant, thread_id);
        END
        """,
        """
        IF OBJECT_ID(N'mb_events', N'U') IS NULL
        BEGIN
            CREATE TABLE mb_events(id BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT pk_mb_events PRIMARY KEY, tenant NVARCHAR(64) NOT NULL,
                thread_id NVARCHAR(100) NULL, task_id NVARCHAR(100) NULL, ts BIGINT NOT NULL, json NVARCHAR(MAX) NOT NULL);
            CREATE INDEX ix_mb_events_thread ON mb_events(tenant, thread_id, id);
            CREATE INDEX ix_mb_events_task ON mb_events(tenant, task_id, id);
            CREATE INDEX ix_mb_events_tenant ON mb_events(tenant, id);
        END
        """,
        """
        IF OBJECT_ID(N'mb_memories', N'U') IS NULL
        BEGIN
            CREATE TABLE mb_memories(rid BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT pk_mb_memories PRIMARY KEY, id NVARCHAR(100) NOT NULL CONSTRAINT uq_mb_memories_id UNIQUE,
                tenant NVARCHAR(64) NOT NULL, owner NVARCHAR(100) NOT NULL, content NVARCHAR(MAX) NULL, json NVARCHAR(MAX) NOT NULL,
                created_at BIGINT NOT NULL, embedding VARBINARY(MAX) NULL);
            CREATE INDEX ix_mb_memories_owner ON mb_memories(tenant, owner);
        END
        """,
        "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_mb_memories_recent') CREATE INDEX ix_mb_memories_recent ON mb_memories(tenant, owner, created_at)",
    ];

    public override string UpsertDocument => """
        MERGE mb_documents WITH (HOLDLOCK) AS t
        USING (SELECT @tenant AS tenant, @kind AS kind, @id AS id) AS s ON t.tenant = s.tenant AND t.kind = s.kind AND t.id = s.id
        WHEN MATCHED THEN UPDATE SET json = @json, updated_at = @updated
        WHEN NOT MATCHED THEN INSERT(tenant, kind, id, json, updated_at) VALUES(@tenant, @kind, @id, @json, @updated);
        """;

    public override string InsertEvent => "INSERT INTO mb_events(tenant,thread_id,task_id,ts,json) OUTPUT INSERTED.id VALUES(@tenant,@thread,@task,@ts,@json)";

    public override string UpsertMemory => """
        MERGE mb_memories WITH (HOLDLOCK) AS t
        USING (SELECT @id AS id) AS s ON t.id = s.id
        WHEN MATCHED THEN UPDATE SET json = @json, owner = @owner, content = @content, embedding = @embedding
        WHEN NOT MATCHED THEN INSERT(id, tenant, owner, content, json, created_at, embedding) VALUES(@id, @tenant, @owner, @content, @json, @created, @embedding);
        SELECT rid FROM mb_memories WHERE id = @id;
        """;

    public override string Limit(string parameter) => $" OFFSET 0 ROWS FETCH NEXT {parameter} ROWS ONLY";
}

internal sealed class MySqlDialect : SqlDialect
{
    public override DatabaseProvider Provider => DatabaseProvider.MySql;

    public override IEnumerable<string> Schema() =>
    [
        """
        CREATE TABLE IF NOT EXISTS mb_documents(tenant VARCHAR(64) NOT NULL, kind VARCHAR(64) NOT NULL, id VARCHAR(191) NOT NULL, json LONGTEXT NOT NULL,
            updated_at BIGINT NOT NULL, PRIMARY KEY(tenant, kind, id)) CHARACTER SET utf8mb4
        """,
        """
        CREATE TABLE IF NOT EXISTS mb_messages(tenant VARCHAR(64) NOT NULL, thread_id VARCHAR(100) NOT NULL, seq BIGINT NOT NULL, json LONGTEXT NOT NULL,
            PRIMARY KEY(thread_id, seq), INDEX ix_mb_messages_tenant(tenant, thread_id)) CHARACTER SET utf8mb4
        """,
        """
        CREATE TABLE IF NOT EXISTS mb_events(id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY, tenant VARCHAR(64) NOT NULL, thread_id VARCHAR(100) NULL,
            task_id VARCHAR(100) NULL, ts BIGINT NOT NULL, json LONGTEXT NOT NULL,
            INDEX ix_mb_events_thread(tenant, thread_id, id), INDEX ix_mb_events_task(tenant, task_id, id), INDEX ix_mb_events_tenant(tenant, id)) CHARACTER SET utf8mb4
        """,
        """
        CREATE TABLE IF NOT EXISTS mb_memories(rid BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY, id VARCHAR(100) NOT NULL UNIQUE, tenant VARCHAR(64) NOT NULL,
            owner VARCHAR(100) NOT NULL, content LONGTEXT NULL, json LONGTEXT NOT NULL, created_at BIGINT NOT NULL, embedding LONGBLOB NULL,
            INDEX ix_mb_memories_owner(tenant, owner)) CHARACTER SET utf8mb4
        """,
        "CREATE INDEX ix_mb_memories_recent ON mb_memories(tenant, owner, created_at)",
    ];

    // ER_DUP_KEYNAME
    public override bool IsAlreadyExists(DbException ex) => ex is MySqlConnector.MySqlException { Number: 1061 };

    public override string UpsertDocument => """
        INSERT INTO mb_documents(tenant,kind,id,json,updated_at) VALUES(@tenant,@kind,@id,@json,@updated) AS n
        ON DUPLICATE KEY UPDATE json=n.json, updated_at=n.updated_at
        """;

    public override string InsertEvent => "INSERT INTO mb_events(tenant,thread_id,task_id,ts,json) VALUES(@tenant,@thread,@task,@ts,@json); SELECT LAST_INSERT_ID();";

    public override string UpsertMemory => """
        INSERT INTO mb_memories(id,tenant,owner,content,json,created_at,embedding) VALUES(@id,@tenant,@owner,@content,@json,@created,@embedding) AS n
        ON DUPLICATE KEY UPDATE json=n.json, owner=n.owner, content=n.content, embedding=n.embedding;
        SELECT rid FROM mb_memories WHERE id=@id;
        """;
}
