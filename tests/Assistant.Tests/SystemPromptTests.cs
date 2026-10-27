using System.Text.RegularExpressions;
using Assistant.Core.Agent;
using Assistant.Core.Indexing;
using Assistant.Core.Mcp;
using Assistant.SqliteMcp;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Assistant.Tests;

/// <summary>Keeps the system prompt in step with the schema, the tool configuration and SQLite's date handling.</summary>
public sealed class SystemPromptTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lka-prompt-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Every_shipped_version_loads()
    {
        Assert.Contains("local knowledge assistant", SystemPrompts.Load("v1"));
        Assert.Equal(SystemPrompts.Load(SystemPrompts.CurrentVersion), SystemPrompts.Default);
    }

    [Fact]
    public void Prompt_names_every_column_of_the_files_table()
    {
        Directory.CreateDirectory(_dir);
        var db = new MetadataDatabase(Path.Combine(_dir, "meta.db"));
        db.Migrate();
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT name FROM pragma_table_info('files') WHERE name <> 'id'";
        using var reader = cmd.ExecuteReader();

        var columns = new List<string>();
        while (reader.Read()) columns.Add(reader.GetString(0));

        Assert.NotEmpty(columns);
        Assert.All(columns, col => Assert.Matches($@"\b{col}\b", SystemPrompts.Default));
    }

    [Fact]
    public async Task Prompt_does_not_mention_tools_withheld_from_the_model()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseSetting("Indexing:Enabled", "false"));
        var servers = factory.Services.GetRequiredService<IOptions<McpServerOptions>>().Value.Servers;

        var excluded = servers.SelectMany(s => s.ExcludeTools ?? []).ToList();

        Assert.NotEmpty(excluded);
        Assert.All(excluded, tool => Assert.DoesNotMatch($@"\b{Regex.Escape(tool)}\b", SystemPrompts.Default));
    }

    [Fact]
    public async Task Multi_file_read_stays_withheld_because_the_model_passes_its_paths_as_a_string()
    {
        // Live runs against qwen2.5:7b-instruct: read_multiple_files was called with "paths" as JSON text instead of
        // an array in every run, five times in a row, until the iteration cap. One read_text_file per file works.
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseSetting("Indexing:Enabled", "false"));
        var servers = factory.Services.GetRequiredService<IOptions<McpServerOptions>>().Value.Servers;

        var filesystem = Assert.Single(servers, s => s.Name == "filesystem");

        Assert.Contains("read_multiple_files", filesystem.ExcludeTools!);
        Assert.DoesNotContain("read_multiple_files", SystemPrompts.Default);
        Assert.Contains("read_text_file", SystemPrompts.Default);
    }

    [Fact]
    public void Recommended_date_comparison_handles_indexer_timestamps_where_raw_text_comparison_does_not()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "meta.db");
        new MetadataDatabase(path).Migrate();
        var engine = new ReadOnlyQueryEngine(path);

        // Indexer format (7 fractional digits, 'T', 'Z') against SQLite's own format (space, no zone): 01:00 is before 06:00.
        var r = engine.Execute("""
            SELECT datetime('2026-10-05T01:00:00.1234567Z') >= datetime('2026-10-05 06:00:00') AS wrapped,
                   '2026-10-05T01:00:00.1234567Z' >= datetime('2026-10-05 06:00:00') AS raw
            """);

        Assert.Equal([0L, 1L], r.Rows[0]);
    }
}
