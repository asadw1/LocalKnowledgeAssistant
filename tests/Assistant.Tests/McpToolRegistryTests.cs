using Assistant.Core.Mcp;
using Assistant.Core.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;

namespace Assistant.Tests;

public class McpToolRegistryTests
{
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
    public void Filter_without_exclusions_returns_everything() =>
        Assert.Empty(McpToolRegistry.Filter([], null));

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
            ["write_file", "edit_file", "create_directory", "move_file"]));
        await registry.StartAsync(CancellationToken.None);

        var names = registry.Tools["filesystem"].Select(t => t.Name).ToList();
        Assert.Contains("read_text_file", names);
        Assert.DoesNotContain("write_file", names);

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
