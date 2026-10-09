using Assistant.Core.Agent;
using Assistant.Core.Mcp;
using Assistant.Core.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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

        services.Configure<McpServerOptions>(config.GetSection(McpServerOptions.SectionName));
        services.AddSingleton<McpToolRegistry>();
        services.AddSingleton<IToolRegistry>(sp => sp.GetRequiredService<McpToolRegistry>());
        services.AddHostedService(sp => sp.GetRequiredService<McpToolRegistry>());

        services.AddScoped<IAgentRunner, SemanticKernelAgentRunner>();
        return services;
    }
}
