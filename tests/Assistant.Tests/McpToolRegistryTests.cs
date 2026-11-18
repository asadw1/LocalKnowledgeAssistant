using Assistant.Core.Mcp;
using Assistant.Core.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;

namespace Assistant.Tests;

public sealed class McpToolRegistryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lka-reg-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>The shipped read-only SQLite server, launched the way the app launches it.</summary>
    private McpServerDefinition SqliteServer(string[]? exclude = null, string[]? include = null)
    {
        var db = Path.Combine(_dir, "meta.db");
        new Assistant.Core.Indexing.MetadataDatabase(db).Migrate();
        return new("sqlite", "dotnet", [Path.Combine(AppContext.BaseDirectory, "Assistant.SqliteMcp.dll"), db], exclude, include);
    }

    private static McpToolRegistry Create(McpServerDefinition server) =>
        new(Options.Create(new McpServerOptions { Servers = [server] }), NullLogger<McpToolRegistry>.Instance);

    [Fact]
    public async Task Failing_server_does_not_throw_and_exposes_no_tools()
    {
        await using var registry = Create(new("broken", "definitely-not-a-real-command", []));

        await registry.StartAsync(CancellationToken.None);

        Assert.Empty(registry.Tools);
    }

    [Fact]
    public async Task Register_with_no_tools_leaves_kernel_empty()
    {
        await using var registry = Create(new("broken", "definitely-not-a-real-command", []));
        await registry.StartAsync(CancellationToken.None);
        var kernel = new Kernel();

        await registry.RegisterAsync(kernel);

        Assert.Empty(kernel.Plugins);
    }

    [Fact]
    public void Filter_without_lists_returns_everything() =>
        Assert.Empty(McpToolRegistry.Filter([], null, null));

    [Fact]
    public async Task Working_server_exposes_its_tools_and_registers_them_as_a_plugin()
    {
        await using var registry = Create(SqliteServer());
        await registry.StartAsync(CancellationToken.None);
        var kernel = new Kernel();

        await registry.RegisterAsync(kernel);

        Assert.Equal(["describe_schema", "query"], registry.Tools["sqlite"].Select(t => t.Name).Order());
        Assert.Equal(["describe_schema", "query"], kernel.Plugins["sqlite"].Select(f => f.Name).Order());
    }

    [Fact]
    public async Task Registering_twice_does_not_duplicate_the_plugin()
    {
        await using var registry = Create(SqliteServer());
        await registry.StartAsync(CancellationToken.None);
        var kernel = new Kernel();

        await registry.RegisterAsync(kernel);
        await registry.RegisterAsync(kernel);

        Assert.Single(kernel.Plugins);
    }

    [Fact]
    public async Task Excluded_tools_are_withheld_from_the_model()
    {
        await using var registry = Create(SqliteServer(exclude: ["QUERY"]));
        await registry.StartAsync(CancellationToken.None);

        Assert.Equal(["describe_schema"], registry.Tools["sqlite"].Select(t => t.Name));
    }

    [Fact]
    public async Task Only_allow_listed_tools_are_offered_and_matching_ignores_case()
    {
        await using var registry = Create(SqliteServer(include: ["QUERY"]));
        await registry.StartAsync(CancellationToken.None);
        var kernel = new Kernel();

        await registry.RegisterAsync(kernel);

        // describe_schema exists on the server but is not allow-listed, so the model never sees it.
        Assert.Equal(["query"], registry.Tools["sqlite"].Select(t => t.Name));
        Assert.Equal(["query"], kernel.Plugins["sqlite"].Select(f => f.Name));
    }

    [Fact]
    public async Task Exclusion_still_applies_on_top_of_the_allow_list()
    {
        await using var registry = Create(SqliteServer(exclude: ["query"], include: ["query", "describe_schema"]));
        await registry.StartAsync(CancellationToken.None);

        Assert.Equal(["describe_schema"], registry.Tools["sqlite"].Select(t => t.Name));
    }

    [Fact]
    public async Task An_empty_allow_list_offers_no_tools()
    {
        await using var registry = Create(SqliteServer(include: []));
        await registry.StartAsync(CancellationToken.None);

        Assert.Empty(registry.Tools["sqlite"]);
    }

    [Fact]
    public async Task Stop_completes_and_dispose_after_a_started_server_is_safe_to_repeat()
    {
        var registry = Create(SqliteServer());
        await registry.StartAsync(CancellationToken.None);

        await registry.StopAsync(CancellationToken.None);
        await registry.DisposeAsync();
        await registry.DisposeAsync();
    }

    [Fact]
    public void Filter_drops_named_tools_case_insensitively() => Assert.Empty(McpToolRegistry.Filter([], null, ["x"]));

    // Opt-in: needs node/npx and network on first run. Set ASSISTANT_MCP_INTEGRATION=1.
    [Fact]
    public async Task Real_filesystem_server_enforces_allow_list_and_excludes_write_tools()
    {
        if (Environment.GetEnvironmentVariable("ASSISTANT_MCP_INTEGRATION") != "1") return;

        var root = Directory.CreateTempSubdirectory().FullName;
        await File.WriteAllTextAsync(Path.Combine(root, "a.txt"), "hello");
        var npx = OperatingSystem.IsWindows() ? "npx.cmd" : "npx";
        await using var registry = Create(new("filesystem", npx,
            ["-y", "@modelcontextprotocol/server-filesystem", root],
            ExcludeTools: ["write_file", "edit_file", "create_directory", "move_file"],
            IncludeTools: ["read_text_file", "list_directory", "search_files", "list_allowed_directories"]));
        await registry.StartAsync(CancellationToken.None);

        // Exact set: a new write tool in a later server version (append_file and copy_file arrived this way) must not appear.
        var names = registry.Tools["filesystem"].Select(t => t.Name).Order().ToList();
        Assert.Equal(["list_allowed_directories", "list_directory", "read_text_file", "search_files"], names);

        var read = registry.Tools["filesystem"].First(t => t.Name == "read_text_file");
        var ok = await read.CallAsync(new Microsoft.Extensions.AI.AIFunctionArguments { ["path"] = Path.Combine(root, "a.txt") });
        Assert.Contains("hello", System.Text.Json.JsonSerializer.Serialize(ok));

        var outside = Path.Combine(Path.GetTempPath(), "outside-" + Guid.NewGuid() + ".txt");
        await File.WriteAllTextAsync(outside, "secret");
        try
        {
            var denied = await Record.ExceptionAsync(async () =>
            {
                var r = await read.CallAsync(new Microsoft.Extensions.AI.AIFunctionArguments { ["path"] = outside });
                if (System.Text.Json.JsonSerializer.Serialize(r).Contains("secret")) throw new InvalidOperationException("allow-list bypassed");
            });
            Assert.True(denied is not InvalidOperationException);
        }
        finally { File.Delete(outside); }
    }
}
