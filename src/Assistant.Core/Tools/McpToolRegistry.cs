using Assistant.Core.Mcp;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using ModelContextProtocol.Client;

namespace Assistant.Core.Tools;

/// <summary>
/// Launches the configured MCP stdio servers, discovers their tools and exposes them to the kernel.
/// A server that fails to start is logged and skipped; the app stays up without its tools.
/// </summary>
public sealed class McpToolRegistry(IOptions<McpServerOptions> options, ILogger<McpToolRegistry> logger)
    : IToolRegistry, IHostedService, IAsyncDisposable
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(30);

    private readonly List<McpClient> _clients = [];
    private readonly Dictionary<string, IReadOnlyList<McpClientTool>> _tools = new();

    /// <summary>Tools discovered per server name. Servers that failed to start are absent.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<McpClientTool>> Tools => _tools;

    public async Task StartAsync(CancellationToken ct)
    {
        foreach (var server in options.Value.Servers)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(StartupTimeout);

                var transport = new StdioClientTransport(new StdioClientTransportOptions
                {
                    Name = server.Name,
                    Command = server.Command,
                    Arguments = server.Args.Select(ExpandTokens).ToList(),
                });
                var client = await McpClient.CreateAsync(transport, cancellationToken: timeout.Token);
                _clients.Add(client);

                var tools = await client.ListToolsAsync(cancellationToken: timeout.Token);
                _tools[server.Name] = Filter(tools, server.ExcludeTools);
                logger.LogInformation("MCP server {Server} ready with tools: {Tools}",
                    server.Name, string.Join(", ", _tools[server.Name].Select(t => t.Name)));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "MCP server {Server} failed to start; its tools are unavailable", server.Name);
            }
        }
    }

    /// <summary>Replaces <c>{BaseDirectory}</c> with the app's base directory (ends with a separator).</summary>
    public static string ExpandTokens(string arg) => arg.Replace("{BaseDirectory}", AppContext.BaseDirectory, StringComparison.Ordinal);

    /// <summary>Drops tools named in <paramref name="exclude"/> (case-insensitive).</summary>
    public static IReadOnlyList<McpClientTool> Filter(IEnumerable<McpClientTool> tools, string[]? exclude)
    {
        var blocked = new HashSet<string>(exclude ?? [], StringComparer.OrdinalIgnoreCase);
        return tools.Where(t => !blocked.Contains(t.Name)).ToList();
    }

    public Task RegisterAsync(Kernel kernel, CancellationToken ct = default)
    {
        foreach (var (server, tools) in _tools)
        {
            if (kernel.Plugins.Contains(server)) continue;
            kernel.Plugins.AddFromFunctions(server, tools.Select(t => t.AsKernelFunction()));
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients)
            await client.DisposeAsync();
        _clients.Clear();
    }
}
