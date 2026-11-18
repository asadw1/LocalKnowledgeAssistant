using Assistant.Core.Mcp;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Assistant.Tests;

/// <summary>Guards the shipped MCP configuration, which is what keeps the agent read-only.</summary>
public sealed class McpConfigTests
{
    private static readonly string[] ReadOnlyFilesystemTools =
        ["list_allowed_directories", "list_directory", "read_text_file", "search_files"];

    [Fact]
    public async Task Shipped_filesystem_server_offers_exactly_the_read_only_tools()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseSetting("Indexing:Enabled", "false"));
        var servers = factory.Services.GetRequiredService<IOptions<McpServerOptions>>().Value.Servers;

        var filesystem = Assert.Single(servers, s => s.Name == "filesystem");

        // An allow-list, not just a deny-list: tools a newer server version adds (append_file, copy_file did) stay hidden.
        Assert.NotNull(filesystem.IncludeTools);
        Assert.Equal(ReadOnlyFilesystemTools, filesystem.IncludeTools.Order());
    }
}
