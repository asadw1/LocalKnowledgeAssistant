using System.Runtime.CompilerServices;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Assistant.Tests.Fakes;

/// <summary>A scripted model: each call to the service plays the next turn. After the script ends the last turn repeats.</summary>
public sealed class ScriptedChatCompletionService(params Func<IEnumerable<StreamingChatMessageContent>>[] turns) : IChatCompletionService
{
    private int _next;

    public int Calls => _next;

    /// <summary>Content of the last history message at each model call (the user prompt, then each tool result).</summary>
    public List<string?> HistoryTail { get; } = [];

    public IReadOnlyDictionary<string, object?> Attributes { get; } = new Dictionary<string, object?>();

    public static Func<IEnumerable<StreamingChatMessageContent>> Say(string text) =>
        () => [new StreamingChatMessageContent(AuthorRole.Assistant, text)];

    /// <summary>A turn that requests a tool. <paramref name="argsJson"/> is split across two chunks like a real stream.</summary>
    public static Func<IEnumerable<StreamingChatMessageContent>> Call(string name, string argsJson, string? preamble = null) =>
        () =>
        {
            var half = argsJson.Length / 2;
            var chunks = new List<StreamingChatMessageContent>();
            if (preamble is not null) chunks.Add(new StreamingChatMessageContent(AuthorRole.Assistant, preamble));
            chunks.Add(Update(name, argsJson[..half]));
            chunks.Add(Update(null, argsJson[half..]));
            return chunks;
        };

    private static StreamingChatMessageContent Update(string? name, string args)
    {
        var chunk = new StreamingChatMessageContent(AuthorRole.Assistant, null);
        chunk.Items.Add(new StreamingFunctionCallUpdateContent(callId: "call-1", name: name, arguments: args, functionCallIndex: 0));
        return chunk;
    }

    public Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(
        ChatHistory chatHistory, PromptExecutionSettings? executionSettings = null, Kernel? kernel = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public async IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(
        ChatHistory chatHistory, PromptExecutionSettings? executionSettings = null, Kernel? kernel = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        HistoryTail.Add(chatHistory[^1].Content ?? chatHistory[^1].Items.OfType<FunctionResultContent>().FirstOrDefault()?.Result?.ToString());
        var turn = turns[Math.Min(_next++, turns.Length - 1)];
        foreach (var chunk in turn())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return chunk;
        }
    }
}
