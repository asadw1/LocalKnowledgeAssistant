using System.Reflection;
using System.Text.Json;
using Assistant.Core.Indexing;
using Assistant.SqliteMcp;
using Microsoft.Data.Sqlite;
using ModelContextProtocol;

namespace Assistant.Tests;

/// <summary>The tool class called in-process; <see cref="SqliteMcpServerTests"/> covers the same tools over a real stdio server.</summary>
public sealed class SqliteToolsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lka-tools-" + Guid.NewGuid().ToString("N"));
    private readonly string _dbPath;
    private readonly SqliteTools _tools;

    public SqliteToolsTests()
    {
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "meta.db");
        new MetadataDatabase(_dbPath).Migrate();
        ReadOnlyQueryEngineTests.Seed(_dbPath);
        _tools = new SqliteTools(new ReadOnlyQueryEngine(_dbPath));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Query_returns_columns_rows_count_and_truncation_flag()
    {
        using var doc = JsonDocument.Parse(_tools.Query("SELECT name FROM files ORDER BY name", maxRows: 2));
        var root = doc.RootElement;

        Assert.Equal("name", root.GetProperty("columns")[0].GetString());
        Assert.Equal("a.md", root.GetProperty("rows")[0][0].GetString());
        Assert.Equal(2, root.GetProperty("rowCount").GetInt32());
        Assert.True(root.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public void Rejected_query_surfaces_as_an_mcp_exception_the_client_can_show_the_model()
    {
        var ex = Assert.Throws<McpException>(() => _tools.Query("DELETE FROM files"));

        Assert.Contains("Only read-only SELECT", ex.Message);
    }

    [Fact]
    public void Describe_schema_lists_each_table_with_rows_and_create_statement()
    {
        using var doc = JsonDocument.Parse(_tools.DescribeSchema());
        var files = doc.RootElement.EnumerateArray().Single(t => t.GetProperty("table").GetString() == "files");

        Assert.Equal(3, files.GetProperty("rows").GetInt32());
        Assert.Contains("CREATE TABLE", files.GetProperty("create").GetString());
    }

    [Fact]
    public void Describe_schema_on_a_missing_database_is_an_mcp_exception()
    {
        var tools = new SqliteTools(new ReadOnlyQueryEngine(Path.Combine(_dir, "absent.db")));

        Assert.Contains("does not exist yet", Assert.Throws<McpException>(tools.DescribeSchema).Message);
    }
}

public sealed class SqliteMcpEntryPointTests
{
    private static readonly Type EntryPoint = typeof(SqliteTools).Assembly.GetType("SqliteMcpEntryPoint")!;

    private static Task<int> InvokeMain(params string[] args) =>
        (Task<int>)EntryPoint.GetMethod("Main", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [args])!;

    [Fact]
    public async Task Wrong_number_of_arguments_prints_usage_and_exits_with_2()
    {
        Assert.Equal(2, await InvokeMain());
        Assert.Equal(2, await InvokeMain("a.db", "extra"));
    }

    [Fact]
    public async Task Blank_database_path_exits_with_2() => Assert.Equal(2, await InvokeMain("   "));

    [Fact]
    public async Task Valid_arguments_start_the_host_and_a_cancelled_token_stops_it_cleanly()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var run = (Task<int>)EntryPoint.GetMethod("RunAsync", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [new[] { Path.Combine(Path.GetTempPath(), "never-created.db") }, cts.Token])!;

        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(30)));
    }
}
