using Assistant.SqliteMcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Named entry point (not top-level statements) so this executable does not collide with the API's `Program` type
// when both are referenced from the test project.
internal static class SqliteMcpEntryPoint
{
    /// <summary>
    /// Read-only SQLite MCP server over stdio. The database path is fixed here, at launch, and is never a tool argument.
    /// Usage: Assistant.SqliteMcp &lt;path-to-database&gt;
    /// </summary>
    private static Task<int> Main(string[] args) => RunAsync(args);

    /// <summary>The server's real body; <paramref name="ct"/> lets tests stop the host that <c>Main</c> would run until killed.</summary>
    internal static async Task<int> RunAsync(string[] args, CancellationToken ct = default)
    {
        if (args.Length != 1 || string.IsNullOrWhiteSpace(args[0]))
        {
            Console.Error.WriteLine("Usage: Assistant.SqliteMcp <path-to-database>");
            return 2;
        }

        var builder = Host.CreateApplicationBuilder();
        // stdout carries the MCP protocol; all logging must go to stderr.
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

        builder.Services.AddSingleton(new ReadOnlyQueryEngine(args[0]));
        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithTools<SqliteTools>();

        await builder.Build().RunAsync(ct);
        return 0;
    }
}
