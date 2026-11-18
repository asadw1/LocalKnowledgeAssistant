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
    /// <summary>How long one server gets to start and list its tools. Settable so a test can force the timeout path.</summary>
    internal TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(30);

    private readonly List<McpClient> _clients = [];
    private readonly Dictionary<string, IReadOnlyList<McpClientTool>> _tools = new();
    private readonly Dictionary<string, PathGuard> _guards = new();

    /// <summary>Tools discovered per server name. Servers that failed to start are absent.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<McpClientTool>> Tools => _tools;

    public async Task StartAsync(CancellationToken ct)
    {
        foreach (var server in options.Value.Servers)
        {
            McpClient? client = null;
            try
            {
                McpServerVerifier.VerifyConfiguration(server, ExpandTokens);

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(StartupTimeout);

                var transport = new StdioClientTransport(new StdioClientTransportOptions
                {
                    Name = server.Name,
                    Command = server.Command,
                    Arguments = server.Args.Select(ExpandTokens).ToList(),
                });
                client = await McpClient.CreateAsync(transport, cancellationToken: timeout.Token);

                var tools = await client.ListToolsAsync(cancellationToken: timeout.Token);
                // Fail closed: a server whose tool set no longer matches the reviewed allow-list is not used at all.
                var withheld = McpServerVerifier.VerifyTools(server, tools.Select(AsOffered).ToList());

                _tools[server.Name] = Filter(tools, server.IncludeTools, server.ExcludeTools);
                if (server.PathRoot is not null) _guards[server.Name] = new PathGuard(ExpandTokens(server.PathRoot));
                _clients.Add(client);
                client = null; // ownership moved to _clients; the finally block must not dispose it

                logger.LogInformation("MCP server {Server} ready with tools: {Tools}",
                    server.Name, string.Join(", ", _tools[server.Name].Select(t => t.Name)));
                if (withheld.Count > 0)
                    logger.LogInformation("MCP server {Server} offers tools that are not allow-listed and stay withheld: {Withheld}",
                        server.Name, string.Join(", ", withheld));
            }
            catch (McpServerRejectedException ex)
            {
                logger.LogError("{Reason} Its tools are unavailable.", ex.Message);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // the host is shutting down; do not keep starting servers
            }
            catch (OperationCanceledException)
            {
                logger.LogError("MCP server {Server} did not become ready within {Seconds}s; its tools are unavailable",
                    server.Name, StartupTimeout.TotalSeconds);
            }
            // Boundary: any other failure (missing executable, protocol error, bad arguments) must not stop the app.
            // The server is skipped and its tools stay unavailable, which is the safe state.
            catch (Exception ex)
            {
                logger.LogError(ex, "MCP server {Server} failed to start; its tools are unavailable", server.Name);
            }
            finally
            {
                if (client is not null) await DisposeQuietlyAsync(client, server.Name, logger);
            }
        }
    }

    private static OfferedTool AsOffered(McpClientTool tool) =>
        new(tool.Name, tool.ProtocolTool.Annotations?.ReadOnlyHint, tool.ProtocolTool.Annotations?.DestructiveHint);

    /// <summary>Disposes a client that is not being kept. A failure to dispose must not mask the original problem.</summary>
    internal static async Task DisposeQuietlyAsync(IAsyncDisposable client, string server, ILogger logger)
    {
        try
        {
            await client.DisposeAsync();
        }
        // Boundary: this runs while handling another failure, so it can only log.
        catch (Exception ex)
        {
            logger.LogWarning(ex, "MCP server {Server} could not be shut down cleanly after being rejected", server);
        }
    }

    /// <inheritdoc />
    public string? CheckCall(KernelFunction function, KernelArguments arguments)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(arguments);
        return function.PluginName is not null && _guards.TryGetValue(function.PluginName, out var guard)
            ? guard.Check(arguments)
            : null;
    }

    /// <summary>Replaces <c>{BaseDirectory}</c> with the app's base directory (ends with a separator).</summary>
    public static string ExpandTokens(string arg) => arg.Replace("{BaseDirectory}", AppContext.BaseDirectory, StringComparison.Ordinal);

    /// <summary>
    /// Keeps only tools named in <paramref name="include"/> (when not null), then drops those named in
    /// <paramref name="exclude"/>. Names match case-insensitively.
    /// </summary>
    public static IReadOnlyList<McpClientTool> Filter(IEnumerable<McpClientTool> tools, string[]? include, string[]? exclude)
    {
        var allowed = include is null ? null : new HashSet<string>(include, StringComparer.OrdinalIgnoreCase);
        var blocked = new HashSet<string>(exclude ?? [], StringComparer.OrdinalIgnoreCase);
        return tools.Where(t => (allowed is null || allowed.Contains(t.Name)) && !blocked.Contains(t.Name)).ToList();
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
