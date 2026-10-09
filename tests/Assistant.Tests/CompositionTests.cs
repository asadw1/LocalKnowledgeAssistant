using Assistant.Core.Agent;
using Assistant.Core.Tools;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Assistant.Tests;

public class CompositionTests
{
    [Fact]
    public async Task Real_container_resolves_the_agent_runner()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseSetting("Indexing:Enabled", "false"));
        using var scope = factory.Services.CreateScope();

        Assert.IsType<SemanticKernelAgentRunner>(scope.ServiceProvider.GetRequiredService<IAgentRunner>());
    }

    [Fact]
    public async Task Configured_sqlite_server_starts_and_exposes_only_its_two_read_tools()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseSetting("Indexing:Enabled", "false"));
        var registry = factory.Services.GetRequiredService<McpToolRegistry>();

        Assert.True(registry.Tools.TryGetValue("sqlite", out var tools), "the sqlite MCP server failed to start with the shipped configuration");
        Assert.Equal(["describe_schema", "query"], tools!.Select(t => t.Name).Order());
    }

    [Fact]
    public void Default_system_prompt_loads_from_embedded_resource()
    {
        Assert.Contains("local knowledge assistant", SystemPrompts.Default);
    }
}
