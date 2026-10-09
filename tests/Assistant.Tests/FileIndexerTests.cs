using Assistant.Core.Indexing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Assistant.Tests;

public sealed class FileIndexerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lka-index-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly MetadataDatabase _db;
    private readonly FileIndexer _indexer;

    public FileIndexerTests()
    {
        _root = Path.Combine(_dir, "docs");
        Directory.CreateDirectory(_root);
        _db = new MetadataDatabase(Path.Combine(_dir, "meta.db"));
        _db.Migrate();
        _indexer = new FileIndexer(_db, NullLogger<FileIndexer>.Instance);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools(); // release file handles so the temp dir can be removed
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string Write(string relative, string content, DateTime? modifiedUtc = null)
    {
        var path = Path.GetFullPath(Path.Combine(_root, relative));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        if (modifiedUtc is { } m) File.SetLastWriteTimeUtc(path, m);
        return path;
    }

    private List<string[]> Query(string sql, int columns)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        var rows = new List<string[]>();
        while (r.Read()) rows.Add(Enumerable.Range(0, columns).Select(i => Convert.ToString(r.GetValue(i))!).ToArray());
        return rows;
    }

    [Fact]
    public void First_scan_adds_every_file_with_metadata_including_subfolders()
    {
        var a = Write("a.md", "hello");
        var b = Write("sub/B.TXT", "world!");

        var result = _indexer.Index(_root);

        Assert.Equal(new IndexResult(2, 0, 0, 0, 0), result);
        var rows = Query("SELECT path, name, extension, size_bytes FROM files ORDER BY path", 4);
        Assert.Equal([a, "a.md", ".md", "5"], rows[0]);
        Assert.Equal([b, "B.TXT", ".txt", "6"], rows[1]);
    }

    [Fact]
    public void Stores_sha256_of_content()
    {
        Write("a.md", "abc");
        _indexer.Index(_root);

        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", Query("SELECT hash FROM files", 1).Single()[0]);
    }

    [Fact]
    public void Rescan_without_changes_touches_nothing()
    {
        Write("a.md", "hello");
        _indexer.Index(_root);
        var before = Query("SELECT indexed_utc FROM files", 1).Single()[0];

        var result = _indexer.Index(_root);

        Assert.Equal(new IndexResult(0, 0, 1, 0, 0), result);
        Assert.Equal(before, Query("SELECT indexed_utc FROM files", 1).Single()[0]);
    }

    [Fact]
    public void Changed_content_is_updated()
    {
        var path = Write("a.md", "hello", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        _indexer.Index(_root);
        var oldHash = Query("SELECT hash FROM files", 1).Single()[0];

        Write("a.md", "hello, world", new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));
        var result = _indexer.Index(_root);

        Assert.Equal(new IndexResult(0, 1, 0, 0, 0), result);
        var row = Query("SELECT size_bytes, hash, modified_utc FROM files", 3).Single();
        Assert.Equal("12", row[0]);
        Assert.NotEqual(oldHash, row[1]);
        Assert.StartsWith("2026-02-01T00:00:00", row[2]);
        Assert.Equal(path, Query("SELECT path FROM files", 1).Single()[0]);
    }

    [Fact]
    public void Touched_but_identical_file_is_not_counted_as_updated()
    {
        var path = Write("a.md", "same", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        _indexer.Index(_root);

        File.SetLastWriteTimeUtc(path, new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc));
        var result = _indexer.Index(_root);

        Assert.Equal(new IndexResult(0, 0, 1, 0, 0), result);
        Assert.StartsWith("2026-03-01", Query("SELECT modified_utc FROM files", 1).Single()[0]); // new mtime recorded so the next scan skips hashing
    }

    [Fact]
    public void Deleted_file_is_removed_and_its_tags_and_summary_cascade()
    {
        var path = Write("a.md", "hello");
        Write("keep.md", "keep");
        _indexer.Index(_root);

        using (var c = _db.Open())
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                INSERT INTO tags (name) VALUES ('urgent');
                INSERT INTO file_tags (file_id, tag_id) SELECT f.id, t.id FROM files f, tags t WHERE f.name = 'a.md';
                INSERT INTO summaries (file_id, summary, source_hash, generated_utc) SELECT id, 's', hash, 'now' FROM files WHERE name = 'a.md';
                """;
            cmd.ExecuteNonQuery();
        }

        File.Delete(path);
        var result = _indexer.Index(_root);

        Assert.Equal(new IndexResult(0, 0, 1, 1, 0), result);
        Assert.Equal(["keep.md"], Query("SELECT name FROM files", 1).Select(r => r[0]));
        Assert.Empty(Query("SELECT * FROM file_tags", 1));
        Assert.Empty(Query("SELECT * FROM summaries", 1));
    }

    [Fact]
    public void Locked_file_is_skipped_and_existing_row_survives()
    {
        var path = Write("a.md", "hello", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        _indexer.Index(_root);

        File.SetLastWriteTimeUtc(path, new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));
        using var lockStream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var result = _indexer.Index(_root);

        Assert.Equal(1, result.Skipped);
        Assert.Equal(0, result.Removed);
        Assert.Single(Query("SELECT id FROM files", 1));
    }

    [Fact]
    public void Missing_root_throws()
    {
        Assert.Throws<DirectoryNotFoundException>(() => _indexer.Index(Path.Combine(_dir, "nope")));
    }

    [Fact]
    public void Migrate_is_idempotent_and_records_schema_version()
    {
        _db.Migrate();
        _db.Migrate();

        Assert.Equal("1", Query("PRAGMA user_version", 1).Single()[0]);
        Assert.Equal(["file_tags", "files", "summaries", "tags"],
            Query("SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name", 1).Select(r => r[0]));
    }

    [Fact]
    public void Migrations_load_in_order_from_embedded_resources()
    {
        var migrations = MetadataDatabase.LoadMigrations();

        Assert.Equal(1, migrations[0].Version);
        Assert.Equal("001_init.sql", migrations[0].Name);
    }
}
