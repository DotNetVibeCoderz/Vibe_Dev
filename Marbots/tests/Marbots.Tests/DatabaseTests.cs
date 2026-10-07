using Marbots.Abstractions;
using Marbots.Runtime;
using Marbots.Storage;
using Microsoft.Data.Sqlite;

namespace Marbots.Tests;

/// <summary>
/// Storage conformance on every configured database. SQLite always runs; PostgreSQL, SQL Server and MySQL run when
/// MARBOTS_TEST_POSTGRES / MARBOTS_TEST_SQLSERVER / MARBOTS_TEST_MYSQL hold a connection string (CI sets all three).
/// Each test uses fresh tenants, so a shared server database stays clean between runs.
/// </summary>
public sealed class DatabaseTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mb-db-" + Guid.NewGuid().ToString("N")[..8]);

    public static TheoryData<string> Providers()
    {
        var data = new TheoryData<string> { "sqlite" };
        foreach (var (name, env) in new[] { ("postgresql", "MARBOTS_TEST_POSTGRES"), ("sqlserver", "MARBOTS_TEST_SQLSERVER"), ("mysql", "MARBOTS_TEST_MYSQL") })
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(env))) data.Add(name);
        return data;
    }

    private MarbotsDatabase Open(string provider, string tenant) => provider switch
    {
        "sqlite" => new SqliteDatabase(Path.Combine(_dir, "t.db"), tenant),
        "postgresql" => new MarbotsDatabase(DatabaseProvider.PostgreSql, Environment.GetEnvironmentVariable("MARBOTS_TEST_POSTGRES")!, tenant),
        "sqlserver" => new MarbotsDatabase(DatabaseProvider.SqlServer, Environment.GetEnvironmentVariable("MARBOTS_TEST_SQLSERVER")!, tenant),
        _ => new MarbotsDatabase(DatabaseProvider.MySql, Environment.GetEnvironmentVariable("MARBOTS_TEST_MYSQL")!, tenant),
    };

    private static string NewTenant() => "t" + Guid.NewGuid().ToString("N")[..10];

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Documents_are_isolated_per_tenant(string provider)
    {
        var (ta, tb) = (NewTenant(), NewTenant());
        var a = new DocumentStore<BotDefinition>(Open(provider, ta), "bot", MarbotsJsonContext.Default.BotDefinition, b => b.Id);
        var b = new DocumentStore<BotDefinition>(Open(provider, tb), "bot", MarbotsJsonContext.Default.BotDefinition, x => x.Id);
        await a.UpsertAsync(new BotDefinition { Id = "atlas", Name = "Atlas A" });
        await a.UpsertAsync(new BotDefinition { Id = "atlas", Name = "Atlas A2" });
        await b.UpsertAsync(new BotDefinition { Id = "atlas", Name = "Atlas B" });
        Assert.Equal("Atlas A2", (await a.GetAsync("atlas"))!.Name);
        Assert.Equal("Atlas B", (await b.GetAsync("atlas"))!.Name);
        Assert.Single(await a.ListAsync());
        Assert.True(await a.DeleteAsync("atlas"));
        Assert.Null(await a.GetAsync("atlas"));
        Assert.NotNull(await b.GetAsync("atlas"));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Messages_get_sequential_numbers_per_thread(string provider)
    {
        var db = Open(provider, NewTenant());
        var store = new MessageStore(db);
        var thread = "thr-" + Guid.NewGuid().ToString("N")[..8];
        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => store.AppendAsync(new ChatMessage { ThreadId = thread, Content = i.ToString() })));
        var list = await store.ListAsync(thread);
        Assert.Equal(Enumerable.Range(1, 20).Select(i => (long)i), list.Select(m => m.Seq));
        Assert.Equal(20, await store.CountAsync(thread));
        Assert.Equal(5, (await store.ListAsync(thread, 15)).Count);
        Assert.Equal(3, (await store.ListAsync(thread, 0, 3)).Count);
        await store.DeleteThreadAsync(thread);
        Assert.Equal(0, await store.CountAsync(thread));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Events_replay_after_id_and_stay_in_their_tenant(string provider)
    {
        var tenant = NewTenant();
        var store = new EventStore(Open(provider, tenant));
        var other = new EventStore(Open(provider, NewTenant()));
        var first = await store.AppendAsync(new AgentEvent { Type = "A", ThreadId = "t", TaskId = "k" });
        await store.AppendAsync(new AgentEvent { Type = "B", ThreadId = "t" });
        await store.AppendAsync(new AgentEvent { Type = "C", ThreadId = "other" });
        await other.AppendAsync(new AgentEvent { Type = "X", ThreadId = "t" });
        var after = await store.ListAsync("t", null, first, 10);
        Assert.Equal(["B"], after.Select(e => e.Type));
        Assert.True(after[0].Id > first);
        Assert.Equal(["A"], (await store.ListAsync(null, "k", 0, 10)).Select(e => e.Type));
        Assert.Equal(["B", "C"], (await store.RecentAsync(2)).Select(e => e.Type));
        Assert.DoesNotContain(await store.RecentAsync(50), e => e.Type == "X");
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Memory_search_ranks_filters_by_owner_and_is_injection_safe(string provider)
    {
        var store = new MemoryStore(Open(provider, NewTenant()), new HashingEmbeddings());
        await store.WriteAsync(new MemoryRecord { Owner = "atlas", Content = "The client prefers reports in Bahasa Indonesia" });
        await store.WriteAsync(new MemoryRecord { Owner = "atlas", Content = "Quarterly revenue target is 2 billion rupiah" });
        await store.WriteAsync(new MemoryRecord { Owner = "alice", Content = "Reports must use Bahasa Indonesia headings" });
        var hits = await store.SearchAsync(new MemoryQuery("what language for reports?", ["atlas"]));
        Assert.NotEmpty(hits);
        Assert.All(hits, h => Assert.Equal("atlas", h.Record.Owner));
        Assert.Contains("Bahasa", hits[0].Record.Content);
        Assert.DoesNotContain(await store.SearchAsync(new MemoryQuery("\" OR 1=1 -- NEAR( %' ;DROP", ["x"])), _ => true);
        Assert.Equal(2, (await store.ListAsync("atlas")).Count);
        Assert.Equal(2, await store.DeleteOwnerAsync("atlas"));
        Assert.Empty(await store.ListAsync("atlas"));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Hybrid_search_finds_word_forms_that_keyword_search_misses(string provider)
    {
        var db = Open(provider, NewTenant());
        var lexical = new MemoryStore(db);
        var hybrid = new MemoryStore(db, new HashingEmbeddings());
        await hybrid.WriteAsync(new MemoryRecord { Owner = "mira", Content = "Monthly invoicing runs on the 25th via the billing pipeline" });
        await hybrid.WriteAsync(new MemoryRecord { Owner = "mira", Content = "The office plant needs water on Fridays" });
        // "invoices" shares no whole word with "invoicing"; character trigrams in the vector still connect them.
        Assert.Empty(await lexical.SearchAsync(new MemoryQuery("invoices", ["mira"])));
        var hits = await hybrid.SearchAsync(new MemoryQuery("invoices", ["mira"]));
        Assert.Contains("invoicing", hits[0].Record.Content);
    }

    [Fact]
    public async Task Sqlite_databases_from_before_multi_tenancy_are_migrated()
    {
        Directory.CreateDirectory(_dir);
        var file = Path.Combine(_dir, "old.db");
        await using (var c = new SqliteConnection($"Data Source={file}"))
        {
            await c.OpenAsync();
            await using var cmd = c.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE documents(kind TEXT NOT NULL, id TEXT NOT NULL, json TEXT NOT NULL, updated_at INTEGER NOT NULL, PRIMARY KEY(kind, id)) WITHOUT ROWID;
                CREATE TABLE messages(thread_id TEXT NOT NULL, seq INTEGER NOT NULL, json TEXT NOT NULL, PRIMARY KEY(thread_id, seq)) WITHOUT ROWID;
                CREATE TABLE events(id INTEGER PRIMARY KEY AUTOINCREMENT, thread_id TEXT, task_id TEXT, ts INTEGER NOT NULL, json TEXT NOT NULL);
                CREATE TABLE memories(rid INTEGER PRIMARY KEY AUTOINCREMENT, id TEXT NOT NULL UNIQUE, owner TEXT NOT NULL, json TEXT NOT NULL, created_at INTEGER NOT NULL);
                INSERT INTO documents VALUES('bot','old-bot','{"id":"old-bot","name":"Old Bot"}',1);
                INSERT INTO memories(id,owner,json,created_at) VALUES('m1','atlas','{"id":"m1","owner":"atlas","content":"legacy fact"}',1);
                """;
            await cmd.ExecuteNonQueryAsync();
        }
        SqliteConnection.ClearAllPools();
        var db = new SqliteDatabase(file);
        var bots = new DocumentStore<BotDefinition>(db, "bot", MarbotsJsonContext.Default.BotDefinition, b => b.Id);
        Assert.Equal("Old Bot", (await bots.GetAsync("old-bot"))!.Name);
        Assert.Equal("legacy fact", Assert.Single(await new MemoryStore(db).ListAsync("atlas")).Content);
        await new EventStore(db).AppendAsync(new AgentEvent { Type = "after-migration" });
    }

    [Theory]
    [InlineData("sqlite", DatabaseProvider.Sqlite)]
    [InlineData("PostgreSQL", DatabaseProvider.PostgreSql)]
    [InlineData("mssql", DatabaseProvider.SqlServer)]
    [InlineData("MariaDB", DatabaseProvider.MySql)]
    public void Provider_names_are_parsed(string name, DatabaseProvider expected) => Assert.Equal(expected, DatabaseOptions.Parse(name));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }
}
