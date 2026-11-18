using Assistant.Core.Mcp;
using Assistant.Core.Tools;
using Assistant.Tests.Fakes;
using Microsoft.Extensions.Logging;
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

    private static McpToolRegistry Create(McpServerDefinition server, ILogger<McpToolRegistry>? logger = null, TimeSpan? startupTimeout = null) =>
        new(Options.Create(new McpServerOptions { Servers = [server] }), logger ?? NullLogger<McpToolRegistry>.Instance)
        {
            StartupTimeout = startupTimeout ?? TimeSpan.FromSeconds(30),
        };

    private string SqliteDll => Path.Combine(AppContext.BaseDirectory, "Assistant.SqliteMcp.dll");

    private static KernelArguments Args(params (string Name, object? Value)[] args)
    {
        var arguments = new KernelArguments();
        foreach (var (name, value) in args) arguments[name] = value;
        return arguments;
    }

    [Fact]
    public async Task Server_missing_an_allow_listed_tool_is_rejected_and_none_of_its_tools_are_used()
    {
        var log = new CapturingLogger<McpToolRegistry>();
        await using var registry = Create(SqliteServer(include: ["query", "drop_everything"]), log);

        await registry.StartAsync(CancellationToken.None);

        Assert.Empty(registry.Tools);
        var error = Assert.Single(log.Entries, e => e.Level == LogLevel.Error);
        Assert.Contains("rejected", error.Message);
        Assert.Contains("drop_everything", error.Message);
    }

    [Fact]
    public async Task A_path_root_that_is_not_a_launch_argument_is_rejected_before_anything_is_launched()
    {
        var log = new CapturingLogger<McpToolRegistry>();
        // The command does not exist: reaching the launch would log a different error, so this proves the check came first.
        await using var registry = Create(new("fs", "definitely-not-a-real-command", ["somewhere"], PathRoot: "elsewhere"), log);

        await registry.StartAsync(CancellationToken.None);

        Assert.Empty(registry.Tools);
        Assert.Contains("PathRoot", Assert.Single(log.Entries, e => e.Level == LogLevel.Error).Message);
    }

    [Fact]
    public async Task A_server_that_is_too_slow_to_start_is_skipped_and_the_timeout_is_logged()
    {
        var log = new CapturingLogger<McpToolRegistry>();
        await using var registry = Create(SqliteServer(), log, startupTimeout: TimeSpan.FromMilliseconds(1));

        await registry.StartAsync(CancellationToken.None);

        Assert.Empty(registry.Tools);
        Assert.Contains("did not become ready", Assert.Single(log.Entries, e => e.Level == LogLevel.Error).Message);
    }

    [Fact]
    public async Task Cancelling_startup_stops_it_instead_of_being_logged_as_a_server_failure()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await using var registry = Create(SqliteServer());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => registry.StartAsync(cts.Token));
        Assert.Empty(registry.Tools);
    }

    [Fact]
    public async Task Servers_that_offer_unlisted_tools_log_them_as_withheld()
    {
        var log = new CapturingLogger<McpToolRegistry>();
        await using var registry = Create(SqliteServer(include: ["query"]), log);

        await registry.StartAsync(CancellationToken.None);

        Assert.Contains(log.Entries, e => e.Message.Contains("stay withheld: describe_schema"));
    }

    [Fact]
    public async Task Calls_to_a_server_with_a_path_root_are_checked_by_the_function_they_resolve_to()
    {
        var db = Path.Combine(_dir, "meta.db");
        new Assistant.Core.Indexing.MetadataDatabase(db).Migrate();
        // The root here is a file only because this server takes one argument that names it; the guard logic is the same.
        await using var registry = Create(new("sqlite", "dotnet", [SqliteDll, db], PathRoot: db));
        await registry.StartAsync(CancellationToken.None);
        var kernel = new Kernel();
        await registry.RegisterAsync(kernel);
        var query = kernel.Plugins.GetFunction("sqlite", "query");

        Assert.Contains("outside the allowed folder", registry.CheckCall(query, Args(("path", Path.Combine(_dir, "other.db")))));
        Assert.Null(registry.CheckCall(query, Args(("path", db))));
        Assert.Null(registry.CheckCall(query, Args(("sql", "SELECT 1"))));
    }

    [Fact]
    public async Task A_function_from_a_server_without_a_path_root_is_not_checked()
    {
        await using var registry = Create(SqliteServer());
        await registry.StartAsync(CancellationToken.None);
        var kernel = new Kernel();
        await registry.RegisterAsync(kernel);

        Assert.Null(registry.CheckCall(kernel.Plugins.GetFunction("sqlite", "query"), Args(("path", "/anywhere"))));
    }

    [Fact]
    public async Task A_function_without_a_plugin_is_not_checked_and_null_arguments_are_rejected()
    {
        await using var registry = Create(SqliteServer());
        var loose = KernelFunctionFactory.CreateFromMethod(() => "x", "loose"); // not part of any plugin

        Assert.Null(registry.CheckCall(loose, Args(("path", "/anywhere"))));
        Assert.Throws<ArgumentNullException>(() => registry.CheckCall(null!, Args()));
        Assert.Throws<ArgumentNullException>(() => registry.CheckCall(loose, null!));
    }

    private sealed class ThrowingDisposable : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => throw new InvalidOperationException("pipe already closed");
    }

    [Fact]
    public async Task A_failure_while_discarding_a_rejected_client_is_logged_not_thrown()
    {
        var log = new CapturingLogger<McpToolRegistry>();

        await McpToolRegistry.DisposeQuietlyAsync(new ThrowingDisposable(), "fs", log);

        Assert.Contains("could not be shut down cleanly", Assert.Single(log.Entries).Message);
    }

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
}
