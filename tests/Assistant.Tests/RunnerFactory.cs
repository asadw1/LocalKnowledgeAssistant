using Assistant.Core.Agent;
using Assistant.Core.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Assistant.Tests;

internal static class RunnerFactory
{
    public static SemanticKernelAgentRunner Create(IChatCompletionService chat, IToolRegistry tools, AgentOptions? options = null) =>
        new(chat, new Kernel(), tools,
            Options.Create(new OllamaOptions { SystemPrompt = "sys" }),
            Options.Create(options ?? new AgentOptions()),
            NullLogger<SemanticKernelAgentRunner>.Instance);
}
