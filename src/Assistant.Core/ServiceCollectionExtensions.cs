using Assistant.Core.Agent;
using Assistant.Core.Indexing;
using Assistant.Core.Mcp;
using Assistant.Core.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;

namespace Assistant.Core;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAssistantCore(this IServiceCollection services, IConfiguration config)
    {
        services.AddOptions<OllamaOptions>()
            .Bind(config.GetSection(OllamaOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        var ollama = config.GetSection(OllamaOptions.SectionName).Get<OllamaOptions>() ?? new OllamaOptions();
#pragma warning disable SKEXP0070 // Ollama connector is prerelease
        services.AddOllamaChatCompletion(ollama.Model, new Uri(ollama.BaseUrl));
#pragma warning restore SKEXP0070

        services.AddOptions<AgentOptions>()
            .Bind(config.GetSection(AgentOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddKernel();

        services.Configure<McpServerOptions>(config.GetSection(McpServerOptions.SectionName));
        services.AddSingleton<McpToolRegistry>();
        services.AddSingleton<IToolRegistry>(sp => sp.GetRequiredService<McpToolRegistry>());
        services.AddHostedService(sp => sp.GetRequiredService<McpToolRegistry>());

        services.AddOptions<IndexingOptions>()
            .Bind(config.GetSection(IndexingOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton(sp => new MetadataDatabase(sp.GetRequiredService<IOptions<IndexingOptions>>().Value.DatabasePath));
        services.AddSingleton<FileIndexer>();
        services.AddHostedService<IndexingService>();

        services.AddScoped<IAgentRunner, SemanticKernelAgentRunner>();
        return services;
    }
}
