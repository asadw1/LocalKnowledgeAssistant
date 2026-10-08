using System.Runtime.CompilerServices;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Assistant.Tests.Fakes;

public sealed class FakeChatCompletionService(params string[] tokens) : IChatCompletionService
{
    public ChatHistory? LastHistory { get; private set; }
    public IReadOnlyDictionary<string, object?> Attributes { get; } = new Dictionary<string, object?>();

    public Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(
        ChatHistory chatHistory, PromptExecutionSettings? executionSettings = null, Kernel? kernel = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public async IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(
        ChatHistory chatHistory, PromptExecutionSettings? executionSettings = null, Kernel? kernel = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        LastHistory = chatHistory;
        foreach (var t in tokens)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return new StreamingChatMessageContent(AuthorRole.Assistant, t);
        }
    }
}
