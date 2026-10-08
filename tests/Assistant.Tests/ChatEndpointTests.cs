using System.Net.Http.Json;
using Assistant.Core.Agent;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Assistant.Tests;

public class ChatEndpointTests
{
    private sealed class StubRunner : IAgentRunner
    {
        public async IAsyncEnumerable<AgentEvent> RunAsync(string prompt, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return new TokenEvent("echo:" + prompt);
            yield return new DoneEvent();
            await Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Chat_streams_sse_frames()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureServices(s => s.AddScoped<IAgentRunner, StubRunner>()));
        using var client = factory.CreateClient();

        var res = await client.PostAsJsonAsync("/api/chat", new { prompt = "hi" });
        var body = await res.Content.ReadAsStringAsync();

        Assert.Equal("text/event-stream", res.Content.Headers.ContentType?.MediaType);
        Assert.Contains("event: TokenEvent\ndata: {\"Text\":\"echo:hi\"}\n\n", body);
        Assert.Contains("event: DoneEvent", body);
    }
}
