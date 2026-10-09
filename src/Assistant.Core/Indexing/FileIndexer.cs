using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Assistant.Core.Indexing;

public sealed record IndexResult(int Added, int Updated, int Unchanged, int Removed, int Skipped)
{
    public override string ToString() =>
        $"{Added} added, {Updated} updated, {Unchanged} unchanged, {Removed} removed, {Skipped} skipped";
}

/// <summary>
/// Scans a root directory and brings the <c>files</c> table in line with it. Incremental: a file whose size and
/// modified time are unchanged is not re-read; one whose time changed is re-hashed and only counts as updated if the
/// content hash differs. Rows for files that no longer exist are removed (tags and summaries cascade).
/// </summary>
public sealed class FileIndexer(MetadataDatabase database, ILogger<FileIndexer> logger)
{
    private sealed record Row(long Id, long Size, string ModifiedUtc, string Hash);

    public IndexResult Index(string root, CancellationToken ct = default)
    {
        root = Path.GetFullPath(root);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Index root '{root}' does not exist.");

        using var connection = database.Open();
        using var tx = connection.BeginTransaction();

        var known = LoadKnown(connection, tx);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int added = 0, updated = 0, unchanged = 0, skipped = 0;
        var now = Format(DateTime.UtcNow);

        var enumeration = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        foreach (var path in Directory.EnumerateFiles(root, "*", enumeration))
        {
            ct.ThrowIfCancellationRequested();
            seen.Add(path);
            try
            {
                var info = new FileInfo(path);
                var modified = Format(info.LastWriteTimeUtc);

                if (known.TryGetValue(path, out var row))
                {
                    if (row.Size == info.Length && row.ModifiedUtc == modified)
                    {
                        unchanged++;
                        continue;
                    }

                    var hash = Hash(path);
                    Update(connection, tx, row.Id, info.Length, modified, hash, now);
                    if (hash == row.Hash) unchanged++; else updated++;
                }
                else
                {
                    Insert(connection, tx, path, info, modified, Hash(path), now);
                    added++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Locked or vanished mid-scan: leave any existing row alone and pick it up next time.
                logger.LogWarning(ex, "Skipping {Path}", path);
                skipped++;
            }
        }

        var removed = 0;
        foreach (var (path, row) in known)
        {
            if (seen.Contains(path)) continue;
            Delete(connection, tx, row.Id);
            removed++;
        }

        tx.Commit();
        return new IndexResult(added, updated, unchanged, removed, skipped);
    }

    private static Dictionary<string, Row> LoadKnown(SqliteConnection connection, SqliteTransaction tx)
    {
        var known = new Dictionary<string, Row>(StringComparer.Ordinal);
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT id, path, size_bytes, modified_utc, hash FROM files";
        using var reader = command.ExecuteReader();
        while (reader.Read())
            known[reader.GetString(1)] = new Row(reader.GetInt64(0), reader.GetInt64(2), reader.GetString(3), reader.GetString(4));
        return known;
    }

    private static void Insert(SqliteConnection c, SqliteTransaction tx, string path, FileInfo info, string modified, string hash, string now)
    {
        using var command = c.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            INSERT INTO files (path, name, extension, size_bytes, modified_utc, hash, indexed_utc)
            VALUES ($path, $name, $ext, $size, $modified, $hash, $now)
            """;
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$name", info.Name);
        command.Parameters.AddWithValue("$ext", info.Extension.ToLowerInvariant());
        command.Parameters.AddWithValue("$size", info.Length);
        command.Parameters.AddWithValue("$modified", modified);
        command.Parameters.AddWithValue("$hash", hash);
        command.Parameters.AddWithValue("$now", now);
        command.ExecuteNonQuery();
    }

    private static void Update(SqliteConnection c, SqliteTransaction tx, long id, long size, string modified, string hash, string now)
    {
        using var command = c.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "UPDATE files SET size_bytes = $size, modified_utc = $modified, hash = $hash, indexed_utc = $now WHERE id = $id";
        command.Parameters.AddWithValue("$size", size);
        command.Parameters.AddWithValue("$modified", modified);
        command.Parameters.AddWithValue("$hash", hash);
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    private static void Delete(SqliteConnection c, SqliteTransaction tx, long id)
    {
        using var command = c.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "DELETE FROM files WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    private static string Hash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static string Format(DateTime utc) => utc.ToString("O", CultureInfo.InvariantCulture);
}
