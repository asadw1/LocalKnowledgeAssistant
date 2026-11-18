using System.Security.Cryptography;
using System.Text.Json;
using Assistant.Core.Agent;
using Assistant.Core.Indexing;
using Assistant.Core.Mcp;
using Assistant.Core.Tools;
using Assistant.SqliteMcp;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static Assistant.Tests.Fakes.ScriptedChatCompletionService;
using Assistant.Tests.Fakes;

namespace Assistant.Tests;

/// <summary>
/// Adversarial checks of the read-only rule (docs/SECURITY.md section 3): a scripted "model" tries to write and to
/// escape through the real validator, guard, registry and MCP servers, then the files are compared byte for byte.
/// </summary>
public sealed class ReadOnlyEnforcementTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "lka-readonly-" + Guid.NewGuid().ToString("N"));

    public ReadOnlyEnforcementTests() => Directory.CreateDirectory(_base);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        DirectoryLinks.DeleteTree(_base);
    }

    // Tool names use the underscore form the real Ollama connector sends ("filesystem_read_text_file"). Such a call
    // arrives with an empty plugin name, which once let a path guard keyed on the call's plugin name be skipped.
    private static string Args(object value) => JsonSerializer.Serialize(value);

    private static async Task<List<ToolResultEvent>> RunAttempts(IToolRegistry registry, params (string Tool, string Json)[] attempts)
    {
        var turns = attempts.Select(a => Call(a.Tool, a.Json)).Append(Say("done")).ToArray();
        var runner = RunnerFactory.Create(new ScriptedChatCompletionService(turns), registry, new AgentOptions { MaxIterations = attempts.Length + 2 });

        var events = await runner.RunAsync("q").ToListAsync();

        Assert.IsType<DoneEvent>(events[^1]);
        Assert.DoesNotContain(events, e => e is ErrorEvent);
        return events.OfType<ToolResultEvent>().ToList();
    }

    /// <summary>Every file under <paramref name="folder"/> with its SHA-256, plus every directory, so any create, change or delete shows up.</summary>
    private static SortedDictionary<string, string> Snapshot(string folder)
    {
        SqliteConnection.ClearAllPools();
        var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(folder, path);
            if (File.Exists(path))
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                map[relative] = Convert.ToHexString(SHA256.HashData(stream));
            }
            else map[relative] = "<dir>";
        }
        return map;
    }

    // ---- SQLite, through the whole stack (always runs)

    [Fact]
    public async Task Write_and_escape_attempts_against_the_sqlite_server_fail_and_the_database_file_is_unchanged()
    {
        var db = Path.Combine(_base, "meta.db");
        new MetadataDatabase(db).Migrate();
        var before = Snapshot(_base);
        var rowsBefore = new ReadOnlyQueryEngine(db).Execute("SELECT COUNT(*) FROM files").Rows[0][0];

        await using var registry = new McpToolRegistry(
            Options.Create(new McpServerOptions
            {
                Servers = [new("sqlite", "dotnet", [Path.Combine(AppContext.BaseDirectory, "Assistant.SqliteMcp.dll"), db])],
            }),
            NullLogger<McpToolRegistry>.Instance);
        await registry.StartAsync(CancellationToken.None);

        var results = await RunAttempts(registry,
            ("sqlite_query", Args(new { sql = "DROP TABLE files" })),
            ("sqlite_query", Args(new { sql = "DELETE FROM files" })),
            ("sqlite_query", Args(new { sql = "INSERT INTO files(path) VALUES ('x')" })),
            ("sqlite_query", Args(new { sql = "UPDATE files SET name = 'x'" })),
            ("sqlite_query", Args(new { sql = "SELECT 1; DROP TABLE files" })),
            ("sqlite_query", Args(new { sql = "ATTACH DATABASE 'other.db' AS other" })),
            ("sqlite_query", Args(new { sql = "PRAGMA writable_schema = 1" })),
            ("sqlite_query", Args(new { sql = "WITH t AS (SELECT 1) DELETE FROM files" })),
            ("sqlite_query", Args(new { sql = "SELECT 1 -- ; DROP TABLE files" })),
            ("sqlite_execute", Args(new { sql = "DROP TABLE files" })),
            ("sqlite_write", Args(new { sql = "DROP TABLE files" })),
            ("sqlite_query", Args(new { sql = "SELECT 1", database = Path.Combine(_base, "elsewhere.db") })));

        Assert.All(results, r => Assert.True(r.IsError, $"expected an error for {r.Tool}: {r.Result}"));
        // Opening a WAL-mode database creates empty -wal/-shm files even for a read-only reader, so compare the database
        // file itself and require that nothing was written to the log.
        Assert.Equal(before["meta.db"], Snapshot(_base)["meta.db"]);
        Assert.Equal(0, File.Exists(db + "-wal") ? new FileInfo(db + "-wal").Length : 0);
        Assert.Equal(rowsBefore, new ReadOnlyQueryEngine(db).Execute("SELECT COUNT(*) FROM files").Rows[0][0]);
    }

    // ---- real filesystem server, pinned install (opt-in: needs node and `npm ci` in mcp/filesystem)

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LocalKnowledgeAssistant.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not find the repository root from " + AppContext.BaseDirectory);
    }

    /// <summary>The filesystem server exactly as shipped in appsettings.json, pointed at <paramref name="root"/> and the pinned install.</summary>
    private static McpServerDefinition ShippedFilesystemServer(string root)
    {
        var repo = RepoRoot();
        var serverJs = Path.Combine(repo, "mcp", "filesystem", "node_modules", "@modelcontextprotocol", "server-filesystem", "dist", "index.js");
        if (!File.Exists(serverJs)) Assert.Fail("The pinned filesystem server is not installed. Run `npm ci` in mcp/filesystem.");

        var config = new ConfigurationBuilder().AddJsonFile(Path.Combine(repo, "src", "Assistant.Api", "appsettings.json")).Build();
        var options = config.GetSection(McpServerOptions.SectionName).Get<McpServerOptions>()!;
        var shipped = Assert.Single(options.Servers, s => s.Name == "filesystem");
        return shipped with { Command = "node", Args = [serverJs, root], PathRoot = root };
    }

    [Fact]
    public void The_shipped_configuration_launches_the_pinned_install_not_an_unpinned_download()
    {
        var config = new ConfigurationBuilder().AddJsonFile(Path.Combine(RepoRoot(), "src", "Assistant.Api", "appsettings.json")).Build();
        var shipped = Assert.Single(config.GetSection(McpServerOptions.SectionName).Get<McpServerOptions>()!.Servers, s => s.Name == "filesystem");

        Assert.Equal("node", shipped.Command);
        Assert.DoesNotContain("-y", shipped.Args);
        Assert.Contains(shipped.Args, a => a.Contains("mcp/filesystem/node_modules/@modelcontextprotocol/server-filesystem", StringComparison.Ordinal));
        Assert.NotNull(shipped.PathRoot);

        var package = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "mcp", "filesystem", "package.json")));
        var version = package.RootElement.GetProperty("dependencies").GetProperty("@modelcontextprotocol/server-filesystem").GetString();
        Assert.Matches(@"^\d+\.\d+\.\d+$", version); // an exact version: no ^, ~, ranges or tags

        var lockfile = File.ReadAllText(Path.Combine(RepoRoot(), "mcp", "filesystem", "package-lock.json"));
        Assert.Contains("\"integrity\": \"sha512-", lockfile);
    }

    [Fact]
    public async Task Real_filesystem_server_offers_only_read_tools_and_every_escape_and_write_attempt_is_stopped()
    {
        if (Environment.GetEnvironmentVariable("ASSISTANT_MCP_INTEGRATION") != "1") return;

        var root = Path.Combine(_base, "root");
        var outside = Path.Combine(_base, "outside");
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(root, "a.txt"), "hello");
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "secret");
        var linked = DirectoryLinks.TryCreate(Path.Combine(root, "escape"), outside);
        var before = Snapshot(_base);

        await using var registry = new McpToolRegistry(
            Options.Create(new McpServerOptions { Servers = [ShippedFilesystemServer(root)] }), NullLogger<McpToolRegistry>.Instance);
        await registry.StartAsync(CancellationToken.None);

        // Exact set: a write tool added by a later server version must not appear.
        Assert.Equal(["list_allowed_directories", "list_directory", "read_text_file", "search_files"],
            registry.Tools["filesystem"].Select(t => t.Name).Order());

        var secret = Path.Combine(outside, "secret.txt");
        var attempts = new List<(string Tool, string Json, string Expect)>
        {
            // our guard, before the call reaches the server
            ("filesystem_read_text_file", Args(new { path = secret }), "was blocked"),
            ("filesystem_read_text_file", Args(new { path = root + @"\..\outside\secret.txt" }), "was blocked"),
            ("filesystem_read_text_file", Args(new { path = "a.txt" }), "was blocked"),
            ("filesystem_read_text_file", Args(new { path = @"\\localhost\c$\Windows\win.ini" }), "was blocked"),
            ("filesystem_read_text_file", Args(new { path = Path.Combine(root, "a.txt") + ":evil" }), "was blocked"),
            ("filesystem_list_directory", Args(new { path = outside }), "was blocked"),
            ("filesystem_search_files", Args(new { path = root, pattern = "../*" }), "was blocked"),
            ("filesystem_search_files", Args(new { path = outside, pattern = "*" }), "was blocked"),
            // tools that exist on the server but are not allow-listed
            ("filesystem_append_file", Args(new { path = Path.Combine(root, "a.txt"), content = "x" }), "Unknown tool"),
            ("filesystem_copy_file", Args(new { source = Path.Combine(root, "a.txt"), destination = Path.Combine(root, "b.txt") }), "Unknown tool"),
            ("filesystem_write_file", Args(new { path = Path.Combine(root, "c.txt"), content = "x" }), "Unknown tool"),
            ("filesystem_edit_file", Args(new { path = Path.Combine(root, "a.txt"), edits = Array.Empty<string>() }), "Unknown tool"),
            ("filesystem_move_file", Args(new { source = Path.Combine(root, "a.txt"), destination = Path.Combine(root, "d.txt") }), "Unknown tool"),
            ("filesystem_create_directory", Args(new { path = Path.Combine(root, "newdir") }), "Unknown tool"),
        };
        if (linked) attempts.Add(("filesystem_read_text_file", Args(new { path = Path.Combine(root, "escape", "secret.txt") }), "was blocked"));

        var results = await RunAttempts(registry, [.. attempts.Select(a => (a.Tool, a.Json))]);

        Assert.Equal(attempts.Count, results.Count);
        for (var i = 0; i < attempts.Count; i++)
        {
            Assert.True(results[i].IsError, $"attempt {i} ({attempts[i].Tool}) should have failed: {results[i].Result}");
            Assert.Contains(attempts[i].Expect, results[i].Result);
            Assert.DoesNotContain("secret", results[i].Result);
        }
        Assert.Equal(before, Snapshot(_base));

        // The allowed read still works, so the checks are not simply refusing everything.
        var ok = (await RunAttempts(registry, ("filesystem_read_text_file", Args(new { path = Path.Combine(root, "a.txt") })))).Single();
        Assert.False(ok.IsError);
        Assert.Contains("hello", ok.Result);
    }
}
