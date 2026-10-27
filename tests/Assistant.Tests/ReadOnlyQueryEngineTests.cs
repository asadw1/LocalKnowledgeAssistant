using System.Security.Cryptography;
using Assistant.Core.Indexing;
using Assistant.SqliteMcp;
using Microsoft.Data.Sqlite;

namespace Assistant.Tests;

public sealed class ReadOnlyQueryEngineTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lka-sql-" + Guid.NewGuid().ToString("N"));
    private readonly string _dbPath;
    private readonly ReadOnlyQueryEngine _engine;

    public ReadOnlyQueryEngineTests()
    {
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "meta.db");
        new MetadataDatabase(_dbPath).Migrate();
        Seed(_dbPath);
        _engine = new ReadOnlyQueryEngine(_dbPath);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    internal static void Seed(string dbPath)
    {
        using var c = new MetadataDatabase(dbPath).Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO files (path, name, extension, size_bytes, modified_utc, hash, indexed_utc) VALUES
              ('C:\docs\a.md', 'a.md', '.md', 10, '2026-10-01T00:00:00Z', 'h1', 'now'),
              ('C:\docs\b.md', 'b.md', '.md', 5000, '2026-10-05T00:00:00Z', 'h2', 'now'),
              ('C:\docs\c;d.txt', 'c;d.txt', '.txt', 20, '2026-10-06T00:00:00Z', 'h3', 'now');
            """;
        cmd.ExecuteNonQuery();
        // Leave the WAL behind a checkpoint so a read-only reader sees everything without write access.
        cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
        cmd.ExecuteNonQuery();
    }

    private string FileHash()
    {
        using var stream = new FileStream(_dbPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    [Fact]
    public void Select_returns_columns_and_rows()
    {
        var r = _engine.Execute("SELECT name, size_bytes FROM files WHERE size_bytes > 15 ORDER BY size_bytes");

        Assert.Equal(["name", "size_bytes"], r.Columns);
        Assert.Equal([["c;d.txt", 20L], ["b.md", 5000L]], r.Rows.Select(x => x));
        Assert.False(r.Truncated);
    }

    [Theory]
    [InlineData("SELECT count(*) FROM files;")]
    [InlineData("  select count(*) from files ;; ")]
    [InlineData("WITH big AS (SELECT * FROM files WHERE size_bytes > 100) SELECT count(*) FROM big")]
    [InlineData("SELECT ';' AS semi, 'it''s -- fine' AS txt")]
    [InlineData("SELECT name FROM files WHERE name = 'c;d.txt'")]
    public void Legitimate_queries_are_accepted(string sql) => _engine.Execute(sql);

    [Theory]
    [InlineData("INSERT INTO tags (name) VALUES ('x')", "starts with 'INSERT'")]
    [InlineData("DELETE FROM files", "starts with 'DELETE'")]
    [InlineData("UPDATE files SET size_bytes = 0", "starts with 'UPDATE'")]
    [InlineData("DROP TABLE files", "starts with 'DROP'")]
    [InlineData("CREATE TABLE t (a)", "starts with 'CREATE'")]
    [InlineData("ATTACH DATABASE 'other.db' AS o", "starts with 'ATTACH'")]
    [InlineData("PRAGMA query_only = OFF", "starts with 'PRAGMA'")]
    [InlineData("VACUUM", "starts with 'VACUUM'")]
    [InlineData("SELECT 1; DROP TABLE files", "single statement")]
    [InlineData("SELECT 1 -- hidden", "comments")]
    [InlineData("SELECT /* x */ 1", "comments")]
    [InlineData("", "empty")]
    [InlineData("   ;  ", "empty")]
    [InlineData("SELECT 'oops", "Unterminated")]
    public void Statements_that_are_not_a_single_select_are_rejected(string sql, string messagePart)
    {
        var ex = Assert.Throws<QueryRejectedException>(() => _engine.Execute(sql));
        Assert.Contains(messagePart, ex.Message);
    }

    [Theory]
    [InlineData("WITH x AS (SELECT 1) DELETE FROM files")]
    [InlineData("WITH x AS (SELECT 1) INSERT INTO tags (name) VALUES ('x')")]
    [InlineData("WITH x AS (SELECT 1) UPDATE files SET size_bytes = 0")]
    public void Writes_that_pass_the_filter_are_still_refused_by_sqlite_and_nothing_changes(string sql)
    {
        var before = FileHash();

        Assert.Throws<QueryRejectedException>(() => _engine.Execute(sql));

        Assert.Equal(before, FileHash());
        Assert.Equal(3L, _engine.Execute("SELECT count(*) FROM files").Rows[0][0]);
    }

    [Fact]
    public void Read_only_connection_blocks_writes_even_when_the_filter_is_bypassed()
    {
        // Same connection settings as the engine, driven directly, to prove SQLite is the real guard.
        var cs = new SqliteConnectionStringBuilder { DataSource = _dbPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString();
        using var c = new SqliteConnection(cs);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM files";

        var ex = Assert.Throws<SqliteException>(() => cmd.ExecuteNonQuery());
        Assert.Contains("readonly", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rows_are_capped_and_flagged_as_truncated()
    {
        var r = _engine.Execute("SELECT name FROM files ORDER BY name", maxRows: 2);

        Assert.Equal(2, r.Rows.Count);
        Assert.True(r.Truncated);
    }

    [Fact]
    public void Row_cap_cannot_be_raised_above_the_hard_maximum()
    {
        var engine = new ReadOnlyQueryEngine(_dbPath, new ReadOnlyQueryOptions { HardMaxRows = 1 });

        Assert.Single(engine.Execute("SELECT name FROM files", maxRows: 1000).Rows);
    }

    [Fact]
    public void Runaway_query_is_interrupted_by_the_timeout()
    {
        var engine = new ReadOnlyQueryEngine(_dbPath, new ReadOnlyQueryOptions { Timeout = TimeSpan.FromMilliseconds(500) });

        var started = DateTime.UtcNow;
        var ex = Assert.Throws<QueryRejectedException>(() =>
            engine.Execute("WITH RECURSIVE r(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM r) SELECT count(*) FROM r"));

        Assert.Contains("longer than", ex.Message);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
    }

    private const string Forever = "WITH RECURSIVE r(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM r) SELECT count(*) FROM r";

    [Fact]
    public void Already_cancelled_caller_never_starts_the_query()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => _engine.Execute(Forever, ct: cts.Token));
    }

    [Fact]
    public void Caller_cancellation_interrupts_a_running_query_as_cancellation_not_as_timeout()
    {
        var engine = new ReadOnlyQueryEngine(_dbPath, new ReadOnlyQueryOptions { Timeout = TimeSpan.FromSeconds(30) });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        var started = DateTime.UtcNow;
        Assert.ThrowsAny<OperationCanceledException>(() => engine.Execute(Forever, ct: cts.Token));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void Sql_errors_become_model_readable_messages()
    {
        var ex = Assert.Throws<QueryRejectedException>(() => _engine.Execute("SELECT nope FROM files"));

        Assert.Contains("no such column", ex.Message);
    }

    [Fact]
    public void Missing_database_gives_a_friendly_message_not_a_crash()
    {
        var engine = new ReadOnlyQueryEngine(Path.Combine(_dir, "absent.db"));

        Assert.Contains("does not exist yet", Assert.Throws<QueryRejectedException>(() => engine.Execute("SELECT 1")).Message);
        Assert.Throws<QueryRejectedException>(() => engine.DescribeSchema());
        Assert.False(File.Exists(Path.Combine(_dir, "absent.db")), "a read-only open must not create the file");
    }

    [Fact]
    public void Describe_schema_lists_tables_with_create_sql_and_counts()
    {
        var schema = _engine.DescribeSchema();

        Assert.Equal(["file_tags", "files", "summaries", "tags"], schema.Select(t => t.Name));
        var files = schema.Single(t => t.Name == "files");
        Assert.Equal(3, files.RowCount);
        Assert.Contains("absolute path", files.Sql); // column comments survive and document the data for the model
    }

    [Fact]
    public void Blob_values_are_summarised_not_dumped()
    {
        var r = _engine.Execute("SELECT x'DEADBEEF' AS b, NULL AS n");

        Assert.Equal("<blob 4 bytes>", r.Rows[0][0]);
        Assert.Null(r.Rows[0][1]);
    }

    [Fact]
    public void Database_that_cannot_be_opened_gives_a_model_readable_message()
    {
        if (!OperatingSystem.IsWindows()) return; // relies on Windows mandatory file locking
        SqliteConnection.ClearAllPools(); // seeding leaves pooled handles on the file
        using var hold = new FileStream(_dbPath, FileMode.Open, FileAccess.Read, FileShare.None);

        var ex = Assert.Throws<QueryRejectedException>(() => _engine.Execute("SELECT 1"));

        Assert.Contains("Could not open the metadata database read-only", ex.Message);
    }

    [Fact]
    public void Null_sql_is_treated_as_empty() =>
        Assert.Contains("empty", Assert.Throws<QueryRejectedException>(() => ReadOnlyQueryEngine.Validate(null!, 100)).Message);

    [Fact]
    public void Sql_longer_than_the_limit_is_rejected()
    {
        var ex = Assert.Throws<QueryRejectedException>(() => ReadOnlyQueryEngine.Validate("SELECT 1234567", 10));

        Assert.Contains("longer than 10 characters", ex.Message);
    }

    [Fact]
    public void Bracket_quoted_identifiers_may_contain_semicolons_and_comment_markers()
    {
        Assert.Equal("SELECT 1 AS [a;b -- c]", ReadOnlyQueryEngine.Validate("SELECT 1 AS [a;b -- c];", 100));
        Assert.Equal(3L, _engine.Execute("SELECT count(*) AS [n] FROM files").Rows[0][0]);
    }

    [Fact]
    public void Unterminated_bracket_is_rejected() =>
        Assert.Contains("Unterminated", Assert.Throws<QueryRejectedException>(() => ReadOnlyQueryEngine.Validate("SELECT [name FROM files", 100)).Message);

    [Theory]
    [InlineData("SELECT 5 - 3")]
    [InlineData("SELECT 6 / 3")]
    [InlineData("SELECT 5 -")]
    [InlineData("SELECT 6 /")]
    public void Lone_minus_or_slash_is_not_mistaken_for_a_comment(string sql) =>
        Assert.Equal(sql, ReadOnlyQueryEngine.Validate(sql, 100));
}
