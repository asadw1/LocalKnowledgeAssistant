using Microsoft.Data.Sqlite;

namespace Assistant.Core.Indexing;

/// <summary>Opens the metadata SQLite database and applies embedded migrations (<c>data/migrations/NNN_name.sql</c>).</summary>
public sealed class MetadataDatabase(string path)
{
    private const string MigrationPrefix = "migrations/";

    public string Path { get; } = System.IO.Path.GetFullPath(path);

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path, ForeignKeys = true }.ToString());
        connection.Open();
        return connection;
    }

    /// <summary>Creates the database if needed and applies pending migrations in order. Safe to call repeatedly.</summary>
    public void Migrate()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        using var connection = Open();

        // WAL lets the read-only MCP server query while the indexer writes.
        Execute(connection, "PRAGMA journal_mode = WAL");

        var applied = Scalar(connection, "PRAGMA user_version");
        foreach (var (version, _, sql) in LoadMigrations().Where(m => m.Version > applied))
        {
            using var tx = connection.BeginTransaction();
            Execute(connection, sql, tx);
            Execute(connection, $"PRAGMA user_version = {version}", tx); // PRAGMA takes no parameters; version is a parsed int
            tx.Commit();
        }
    }

    public static IReadOnlyList<(int Version, string Name, string Sql)> LoadMigrations()
    {
        var assembly = typeof(MetadataDatabase).Assembly;
        return Order(assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(MigrationPrefix, StringComparison.Ordinal))
            .Select(n => (Resource: n, Read: (Func<string>)(() => ReadResource(assembly, n)))));
    }

    /// <summary>Parses <c>NNN_description.sql</c> resource names, reads them, and returns them in version order.</summary>
    internal static IReadOnlyList<(int Version, string Name, string Sql)> Order(IEnumerable<(string Resource, Func<string> Read)> resources)
    {
        var migrations = new List<(int Version, string Name, string Sql)>();
        foreach (var (resource, read) in resources)
        {
            var file = resource[MigrationPrefix.Length..];
            var underscore = file.IndexOf('_');
            if (underscore < 1 || !int.TryParse(file[..underscore], out var version))
                throw new InvalidOperationException($"Migration '{file}' must be named NNN_description.sql.");

            migrations.Add((version, file, read()));
        }

        var ordered = migrations.OrderBy(m => m.Version).ToList();
        if (ordered.Select(m => m.Version).Distinct().Count() != ordered.Count)
            throw new InvalidOperationException("Duplicate migration version numbers.");
        return ordered;
    }

    private static string ReadResource(System.Reflection.Assembly assembly, string resource)
    {
        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static void Execute(SqliteConnection connection, string sql, SqliteTransaction? tx = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }
}
