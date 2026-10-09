using Assistant.Core.Agent;
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
    public void Default_system_prompt_loads_from_embedded_resource()
    {
        Assert.Contains("local knowledge assistant", SystemPrompts.Default);
    }
}
