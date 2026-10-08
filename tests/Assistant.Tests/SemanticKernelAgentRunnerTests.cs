using Assistant.Core.Agent;
using Assistant.Tests.Fakes;
using Microsoft.Extensions.Options;

namespace Assistant.Tests;

public class SemanticKernelAgentRunnerTests
{
    private static SemanticKernelAgentRunner Create(FakeChatCompletionService chat) =>
        new(chat, Options.Create(new OllamaOptions { SystemPrompt = "sys" }));

    [Fact]
    public async Task Streams_tokens_in_order_then_done()
    {
        var chat = new FakeChatCompletionService("Hel", "lo", "");
        var events = await Create(chat).RunAsync("hi").ToListAsync();

        Assert.Equal(["Hel", "lo"], events.OfType<TokenEvent>().Select(e => e.Text));
        Assert.IsType<DoneEvent>(events[^1]);
    }

    [Fact]
    public async Task Sends_system_prompt_and_user_message()
    {
        var chat = new FakeChatCompletionService("x");
        await Create(chat).RunAsync("question").ToListAsync();

        Assert.Equal(["sys", "question"], chat.LastHistory!.Select(m => m.Content));
    }

    [Fact]
    public async Task Cancellation_stops_the_stream()
    {
        using var cts = new CancellationTokenSource();
        var chat = new FakeChatCompletionService("a", "b", "c");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in Create(chat).RunAsync("hi", cts.Token))
                cts.Cancel();
        });
    }
}
