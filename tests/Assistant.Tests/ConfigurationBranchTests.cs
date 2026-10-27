using Assistant.Core;
using Assistant.Core.Agent;
using Assistant.Core.Indexing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Assistant.Tests;

public class ConfigurationBranchTests
{
    [Fact]
    public void Core_registers_with_no_configuration_at_all_by_falling_back_to_option_defaults()
    {
        var services = new ServiceCollection().AddAssistantCore(new ConfigurationBuilder().Build());

        Assert.Contains(services, d => d.ServiceType == typeof(IAgentRunner));
        Assert.Contains(services, d => d.ServiceType == typeof(MetadataDatabase));
    }

    [Fact]
    public void Unknown_system_prompt_version_is_reported_with_its_resource_name()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => SystemPrompts.Load("v999"));

        Assert.Contains("'v999' not found", ex.Message);
    }

    [Fact]
    public void Index_result_reads_as_a_one_line_summary()
    {
        Assert.Equal("1 added, 2 updated, 3 unchanged, 4 removed, 5 skipped", new IndexResult(1, 2, 3, 4, 5).ToString());
    }
}
