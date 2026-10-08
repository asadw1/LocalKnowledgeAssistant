using System.Runtime.CompilerServices;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Assistant.Core.Agent;

/// <summary>Streams a chat completion from the configured model. Tools and the ReAct loop arrive in later phases.</summary>
public sealed class SemanticKernelAgentRunner(IChatCompletionService chat, IOptions<OllamaOptions> options) : IAgentRunner
{
    public async IAsyncEnumerable<AgentEvent> RunAsync(string prompt, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var history = new ChatHistory(options.Value.SystemPrompt);
        history.AddUserMessage(prompt);

        await foreach (var chunk in chat.GetStreamingChatMessageContentsAsync(history, cancellationToken: ct))
        {
            if (!string.IsNullOrEmpty(chunk.Content))
                yield return new TokenEvent(chunk.Content);
        }

        yield return new DoneEvent();
    }
}
