using Assistant.Core.Indexing;
using Microsoft.Data.Sqlite;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Assistant.Tests;

/// <summary>One real server process over stdio, the way the app launches it, shared by the tests in this class.</summary>
public sealed class SqliteMcpFixture : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lka-mcp-" + Guid.NewGuid().ToString("N"));
    public McpClient Client { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        var db = Path.Combine(_dir, "meta.db");
        new MetadataDatabase(db).Migrate();
        ReadOnlyQueryEngineTests.Seed(db);

        var server = Path.Combine(AppContext.BaseDirectory, "Assistant.SqliteMcp.dll");
        Assert.True(File.Exists(server), $"server not copied next to the tests: {server}");

        Client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "sqlite",
            Command = "dotnet",
            Arguments = [server, db],
        }));
    }

    public async Task DisposeAsync()
    {
        await Client.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

}

public sealed class SqliteMcpServerTests(SqliteMcpFixture fixture) : IClassFixture<SqliteMcpFixture>
{
    private McpClient _client => fixture.Client;

    private static string Text(CallToolResult r) => string.Concat(r.Content.OfType<TextContentBlock>().Select(b => b.Text));

    [Fact]
    public async Task Exposes_only_describe_schema_and_query()
    {
        var tools = await _client.ListToolsAsync();

        Assert.Equal(["describe_schema", "query"], tools.Select(t => t.Name).Order());
    }

    [Fact]
    public async Task Database_path_is_not_a_tool_argument()
    {
        var query = (await _client.ListToolsAsync()).Single(t => t.Name == "query");
        var properties = query.JsonSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToList();

        Assert.Equal(["sql", "maxRows"], properties);
    }

    [Fact]
    public async Task Query_returns_rows_as_json()
    {
        var r = await _client.CallToolAsync("query", new Dictionary<string, object?> { ["sql"] = "SELECT name FROM files ORDER BY name" });

        Assert.NotEqual(true, r.IsError);
        Assert.Contains("\"rowCount\":3", Text(r));
        Assert.Contains("a.md", Text(r));
    }

    [Fact]
    public async Task Write_attempt_comes_back_as_an_error_result_and_the_server_survives()
    {
        var bad = await _client.CallToolAsync("query", new Dictionary<string, object?> { ["sql"] = "DELETE FROM files" });
        Assert.True(bad.IsError);
        Assert.Contains("Only read-only SELECT", Text(bad));

        var good = await _client.CallToolAsync("query", new Dictionary<string, object?> { ["sql"] = "SELECT count(*) AS n FROM files" });
        Assert.NotEqual(true, good.IsError);
        Assert.Contains("[[3]]", Text(good));
    }

    [Fact]
    public async Task Describe_schema_returns_tables()
    {
        var r = await _client.CallToolAsync("describe_schema", new Dictionary<string, object?>());

        Assert.Contains("\"table\":\"files\"", Text(r));
        Assert.Contains("\"rows\":3", Text(r));
    }
}
