using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Assistant.SqliteMcp;

[McpServerToolType]
public sealed class SqliteTools(ReadOnlyQueryEngine engine)
{
    [McpServerTool(Name = "describe_schema", ReadOnly = true, Destructive = false)]
    [Description("Describes the file metadata database: each table's CREATE statement (column comments explain the data) and its row count. Call this before writing a query.")]
    public string DescribeSchema()
    {
        try
        {
            return JsonSerializer.Serialize(engine.DescribeSchema().Select(t => new { table = t.Name, rows = t.RowCount, create = t.Sql }));
        }
        catch (QueryRejectedException ex)
        {
            throw new McpException(ex.Message);
        }
    }

    [McpServerTool(Name = "query", ReadOnly = true, Destructive = false)]
    [Description("Runs one read-only SQL SELECT (or WITH ... SELECT) against the file metadata database and returns columns and rows as JSON. The database cannot be modified. Results are capped; if truncated is true, narrow the query.")]
    public string Query(
        [Description("A single SQLite SELECT statement. No comments, no semicolon-separated statements.")] string sql,
        [Description("Maximum rows to return (default 100, at most 500).")] int? maxRows = null,
        CancellationToken ct = default)
    {
        try
        {
            var result = engine.Execute(sql, maxRows, ct);
            return JsonSerializer.Serialize(new { columns = result.Columns, rows = result.Rows, rowCount = result.Rows.Count, truncated = result.Truncated });
        }
        catch (QueryRejectedException ex)
        {
            throw new McpException(ex.Message);
        }
    }
}
