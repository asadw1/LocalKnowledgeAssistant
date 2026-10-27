using System.Text.Json;
using Assistant.Core.Agent;
using Assistant.Core.Indexing;
using Assistant.Core.Mcp;
using Assistant.Core.Tools;
using Assistant.SqliteMcp;
using Assistant.Tests.Fakes;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using static Assistant.Tests.Fakes.ScriptedChatCompletionService;

namespace Assistant.Tests;

/// <summary>
/// A real indexed corpus and the real read-only SQLite server over stdio, driven by a scripted model. The filesystem
/// server needs Node and network, so a fake with the same <c>read_text_file(path)</c> shape stands in for it. Shared
/// by the tests in this class: nothing in it is written after setup.
/// </summary>
public sealed class CombinedCorpusFixture : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lka-combined-" + Guid.NewGuid().ToString("N"));
    private McpToolRegistry _sqlite = null!;

    public string Root => Path.Combine(_dir, "corpus");
    public string DatabasePath => Path.Combine(_dir, "meta.db");

    /// <summary>Largest file and recently modified. The name has spaces and parentheses to exercise path round-tripping.</summary>
    public string BigFile => Path.Combine(Root, "projects", "migration notes (final).md");
    public const string BigFileText = "The migration to the new billing system is on track.";

    public string SmallFile => Path.Combine(Root, "small.txt");
    public string OldFile => Path.Combine(Root, "old design.md");
    public const string OldFileText = "The legacy system used a nightly batch job.";

    /// <summary>Sqlite tools from the real server plus the stand-in filesystem reader.</summary>
    public IToolRegistry Tools { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(BigFile)!);
        File.WriteAllText(BigFile, BigFileText + new string('.', 2000));
        File.WriteAllText(SmallFile, "milk, eggs");
        File.WriteAllText(OldFile, OldFileText);
        File.SetLastWriteTimeUtc(OldFile, DateTime.UtcNow.AddDays(-60));

        var database = new MetadataDatabase(DatabasePath);
        database.Migrate();
        new FileIndexer(database, NullLogger<FileIndexer>.Instance).Index(Root);

        var server = new McpServerDefinition("sqlite", "dotnet", [Path.Combine(AppContext.BaseDirectory, "Assistant.SqliteMcp.dll"), DatabasePath]);
        _sqlite = new McpToolRegistry(Options.Create(new McpServerOptions { Servers = [server] }), NullLogger<McpToolRegistry>.Instance);
        await _sqlite.StartAsync(CancellationToken.None);
        Assert.Contains("sqlite", _sqlite.Tools.Keys);

        Tools = new CompositeRegistry(_sqlite, new FilesystemReadStub(Root));
    }

    public async Task DisposeAsync()
    {
        await _sqlite.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    public long RowCount() => Convert.ToInt64(new ReadOnlyQueryEngine(DatabasePath).Execute("SELECT COUNT(*) FROM files").Rows[0][0]);

    private sealed class CompositeRegistry(params IToolRegistry[] parts) : IToolRegistry
    {
        public async Task RegisterAsync(Kernel kernel, CancellationToken ct = default)
        {
            foreach (var part in parts) await part.RegisterAsync(kernel, ct);
        }
    }

    /// <summary>Stands in for the filesystem server's <c>read_text_file</c>, including refusing paths outside its root.</summary>
    private sealed class FilesystemReadStub(string root) : IToolRegistry
    {
        public Task RegisterAsync(Kernel kernel, CancellationToken ct = default)
        {
            kernel.Plugins.AddFromFunctions("filesystem",
            [
                KernelFunctionFactory.CreateFromMethod((string path) =>
                {
                    var full = Path.GetFullPath(path);
                    if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException($"Access denied - path outside allowed directories: {path}");
                    return File.ReadAllText(full);
                }, "read_text_file", "Reads a text file"),
            ]);
            return Task.CompletedTask;
        }
    }
}

public sealed class CombinedScenarioTests(CombinedCorpusFixture corpus) : IClassFixture<CombinedCorpusFixture>
{
    private Task<List<AgentEvent>> Run(ScriptedChatCompletionService model) =>
        RunnerFactory.Create(model, corpus.Tools).RunAsync("q").ToListAsync().AsTask();

    private static string Args(object value) => JsonSerializer.Serialize(value);

    /// <summary>The <c>path</c> in the first row of a query result, as the model would read it from its history.</summary>
    private static string FirstPath(string? queryResult)
    {
        using var doc = JsonDocument.Parse(queryResult!);
        return doc.RootElement.GetProperty("rows")[0][0].GetString()!;
    }

    [Theory]
    [InlineData("SELECT path FROM files WHERE datetime(modified_utc) >= datetime('now', '-7 days') ORDER BY size_bytes DESC LIMIT 1", "migration notes (final).md", CombinedCorpusFixture.BigFileText)]
    [InlineData("SELECT path FROM files WHERE datetime(modified_utc) < datetime('now', '-30 days')", "old design.md", CombinedCorpusFixture.OldFileText)]
    public async Task Query_picks_the_file_and_its_path_is_read_exactly_as_returned(string sql, string expectedName, string expectedText)
    {
        ScriptedChatCompletionService model = null!;
        model = new ScriptedChatCompletionService(
            Call("sqlite-query", Args(new { sql })),
            // History tail 1 is the query result; the read uses the path from it, never a path the test knows.
            () => Call("filesystem-read_text_file", Args(new { path = FirstPath(model.HistoryTail[1]) }))(),
            Say("done"));

        var events = await Run(model);

        var results = events.OfType<ToolResultEvent>().ToList();
        Assert.Equal(["sqlite-query", "filesystem-read_text_file"], results.Select(r => r.Tool));
        Assert.All(results, r => Assert.False(r.IsError, r.Result));
        Assert.EndsWith(expectedName, FirstPath(results[0].Result));
        Assert.StartsWith(expectedText, results[1].Result);
        Assert.IsType<DoneEvent>(events[^1]);
    }

    [Fact]
    public async Task Rejected_query_is_corrected_on_the_next_iteration_and_the_read_still_follows()
    {
        ScriptedChatCompletionService model = null!;
        model = new ScriptedChatCompletionService(
            Call("sqlite-query", Args(new { sql = "SELECT path FROM files -- everything" })),
            Call("sqlite-query", Args(new { sql = "SELECT path FROM files WHERE extension = '.txt'" })),
            () => Call("filesystem-read_text_file", Args(new { path = FirstPath(model.HistoryTail[2]) }))(),
            Say("done"));

        var results = (await Run(model)).OfType<ToolResultEvent>().ToList();

        Assert.True(results[0].IsError);
        Assert.Contains("comments", results[0].Result);
        Assert.False(results[1].IsError);
        Assert.Equal("milk, eggs", results[2].Result);
    }

    [Theory]
    [InlineData("DELETE FROM files")]
    [InlineData("WITH x AS (SELECT 1) DELETE FROM files")]
    [InlineData("SELECT 1; DROP TABLE files")]
    [InlineData("ATTACH DATABASE 'other.db' AS other")]
    [InlineData("PRAGMA query_only = OFF")]
    public async Task A_write_attempt_by_the_model_fails_and_the_index_is_unchanged(string sql)
    {
        var before = corpus.RowCount();
        var model = new ScriptedChatCompletionService(Call("sqlite-query", Args(new { sql })), Say("sorry"));

        var events = await Run(model);

        var result = events.OfType<ToolResultEvent>().Single();
        Assert.True(result.IsError);
        Assert.Equal(before, corpus.RowCount());
        Assert.IsType<DoneEvent>(events[^1]);
    }

    [Fact]
    public async Task A_read_outside_the_root_comes_back_as_an_error_the_run_survives()
    {
        var outside = Path.Combine(Path.GetTempPath(), "not-in-the-corpus.txt");
        var model = new ScriptedChatCompletionService(Call("filesystem-read_text_file", Args(new { path = outside })), Say("cannot read that"));

        var events = await Run(model);

        var result = events.OfType<ToolResultEvent>().Single();
        Assert.True(result.IsError);
        Assert.Contains("outside allowed directories", result.Result);
        Assert.IsType<DoneEvent>(events[^1]);
    }
}
