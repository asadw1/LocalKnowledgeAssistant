using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace Assistant.SqliteMcp;

/// <summary>A query was refused or failed in a way the model can act on. The message is safe to show it.</summary>
public sealed class QueryRejectedException(string message) : Exception(message);

public sealed record QueryResult(IReadOnlyList<string> Columns, IReadOnlyList<object?[]> Rows, bool Truncated);

public sealed record TableInfo(string Name, string Sql, long RowCount);

/// <summary>
/// Runs model-written SQL against one fixed SQLite file with no way to change it. Protection is layered, and the
/// innermost layer does not depend on parsing: the file is opened <c>Mode=ReadOnly</c> with <c>PRAGMA query_only</c>,
/// so SQLite itself refuses every write. The statement filter in front of it only exists to give the model clear
/// errors and to keep ATTACH, PRAGMA and multi-statement tricks out.
/// </summary>
public sealed class ReadOnlyQueryEngine(string databasePath, ReadOnlyQueryOptions? options = null)
{
    private readonly ReadOnlyQueryOptions _options = options ?? new ReadOnlyQueryOptions();

    public string DatabasePath { get; } = Path.GetFullPath(databasePath);

    public QueryResult Execute(string sql, int? maxRows = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var statement = Validate(sql, _options.MaxSqlLength);
        var limit = Math.Clamp(maxRows ?? _options.DefaultMaxRows, 1, _options.HardMaxRows);

        using var connection = OpenReadOnly();
        using var command = connection.CreateCommand();
        command.CommandText = statement;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.Timeout);
        // SQLite cannot be cancelled through ADO.NET; interrupting the handle makes the running statement throw.
        using var interrupt = timeout.Token.Register(() =>
        {
            if (connection.Handle is { } handle) raw.sqlite3_interrupt(handle);
        });

        try
        {
            using var reader = command.ExecuteReader();
            var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
            var rows = new List<object?[]>();
            var truncated = false;
            while (reader.Read())
            {
                if (rows.Count == limit)
                {
                    truncated = true;
                    break;
                }

                var row = new object?[reader.FieldCount];
                for (var i = 0; i < row.Length; i++) row[i] = ToCell(reader, i);
                rows.Add(row);
            }
            return new QueryResult(columns, rows, truncated);
        }
        catch (SqliteException ex) when (timeout.IsCancellationRequested && ex.SqliteErrorCode == raw.SQLITE_INTERRUPT)
        {
            ct.ThrowIfCancellationRequested();
            throw new QueryRejectedException($"The query ran longer than {_options.Timeout.TotalSeconds:0.#} seconds and was stopped. Make it more selective.");
        }
        catch (SqliteException ex)
        {
            throw new QueryRejectedException($"SQL error: {ex.Message}");
        }
    }

    public IReadOnlyList<TableInfo> DescribeSchema()
    {
        using var connection = OpenReadOnly();
        var tables = new List<(string Name, string Sql)>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name, sql FROM sqlite_master WHERE type IN ('table', 'view') AND name NOT LIKE 'sqlite_%' ORDER BY name";
            using var reader = command.ExecuteReader();
            while (reader.Read()) tables.Add((reader.GetString(0), reader.GetString(1)));
        }

        var result = new List<TableInfo>();
        foreach (var (name, sql) in tables)
        {
            using var count = connection.CreateCommand();
            count.CommandText = $"SELECT COUNT(*) FROM \"{name.Replace("\"", "\"\"")}\"";
            result.Add(new TableInfo(name, sql, Convert.ToInt64(count.ExecuteScalar())));
        }
        return result;
    }

    private SqliteConnection OpenReadOnly()
    {
        if (!File.Exists(DatabasePath))
            throw new QueryRejectedException("The metadata database does not exist yet; the indexer has not run. Try again shortly.");

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString());

        try
        {
            connection.Open();
            using var pragma = connection.CreateCommand();
            pragma.CommandText = "PRAGMA query_only = ON";
            pragma.ExecuteNonQuery();
            return connection;
        }
        catch (SqliteException ex)
        {
            connection.Dispose();
            throw new QueryRejectedException($"Could not open the metadata database read-only: {ex.Message}");
        }
    }

    /// <summary>Returns the statement without trailing semicolons, or throws <see cref="QueryRejectedException"/>.</summary>
    public static string Validate(string sql, int maxLength)
    {
        var statement = (sql ?? "").Trim().TrimEnd(';', ' ', '\t', '\r', '\n');
        if (statement.Length == 0)
            throw new QueryRejectedException("The query is empty.");
        if (statement.Length > maxLength)
            throw new QueryRejectedException($"The query is longer than {maxLength} characters.");

        // Scan outside of quoted text: no further statements, no comments (they can hide intent).
        for (var i = 0; i < statement.Length; i++)
        {
            var c = statement[i];
            switch (c)
            {
                case '\'' or '"' or '`':
                    i = SkipQuoted(statement, i, c);
                    break;
                case '[':
                    i = SkipQuoted(statement, i, ']');
                    break;
                case ';':
                    throw new QueryRejectedException("Only a single statement is allowed.");
                case '-' when i + 1 < statement.Length && statement[i + 1] == '-':
                case '/' when i + 1 < statement.Length && statement[i + 1] == '*':
                    throw new QueryRejectedException("SQL comments are not allowed.");
            }
        }

        var keyword = FirstWord(statement);
        if (!keyword.Equals("SELECT", StringComparison.OrdinalIgnoreCase) && !keyword.Equals("WITH", StringComparison.OrdinalIgnoreCase))
            throw new QueryRejectedException($"Only read-only SELECT queries are allowed (statement starts with '{keyword}').");

        return statement;
    }

    private static int SkipQuoted(string s, int start, char close)
    {
        for (var i = start + 1; i < s.Length; i++)
            if (s[i] == close) return i;
        throw new QueryRejectedException("Unterminated quoted text in the query.");
    }

    private static string FirstWord(string s)
    {
        var end = 0;
        while (end < s.Length && (char.IsLetter(s[end]) || s[end] == '_')) end++;
        return s[..end];
    }

    private static object? ToCell(SqliteDataReader reader, int i)
    {
        if (reader.IsDBNull(i)) return null;
        return reader.GetFieldType(i) == typeof(byte[])
            ? $"<blob {((byte[])reader.GetValue(i)).Length} bytes>"
            : reader.GetValue(i);
    }
}

public sealed class ReadOnlyQueryOptions
{
    public int MaxSqlLength { get; init; } = 4000;
    public int DefaultMaxRows { get; init; } = 100;
    public int HardMaxRows { get; init; } = 500;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(5);
}
