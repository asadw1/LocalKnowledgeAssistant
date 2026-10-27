using Assistant.Core.Agent;
using Assistant.Tests.Fakes;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using static Assistant.Tests.Fakes.ScriptedChatCompletionService;

namespace Assistant.Tests;

public class ReActLoopTests
{
    private static Task<List<AgentEvent>> Run(ScriptedChatCompletionService model, StubToolRegistry? tools = null, AgentOptions? options = null) =>
        RunnerFactory.Create(model, tools ?? new StubToolRegistry(), options).RunAsync("q").ToListAsync().AsTask();

    [Fact]
    public async Task Valid_tool_call_is_executed_and_answer_follows()
    {
        var tools = new StubToolRegistry();
        var model = new ScriptedChatCompletionService(Call("tools-echo", """{"text":"hi"}""", "Looking. "), Say("Done: echo:hi"));

        var events = await Run(model, tools);

        Assert.Equal(
            ["TokenEvent", "ToolCallEvent", "ToolResultEvent", "TokenEvent", "DoneEvent"],
            events.Select(e => e.GetType().Name));
        Assert.Equal(new ToolCallEvent("tools-echo", """{"text":"hi"}"""), events[1]);
        Assert.Equal(new ToolResultEvent("tools-echo", "echo:hi", false), events[2]);
        Assert.Equal(1, tools.EchoInvocations);
        Assert.Equal("echo:hi", model.HistoryTail[1]);
    }

    [Fact]
    public async Task Unknown_tool_returns_error_listing_valid_tools_and_does_not_crash()
    {
        var events = await Run(new ScriptedChatCompletionService(Call("tools-nope", "{}"), Say("sorry")));

        var result = events.OfType<ToolResultEvent>().Single();
        Assert.True(result.IsError);
        Assert.Contains("Unknown tool", result.Result);
        Assert.Contains("tools_echo", result.Result);
        Assert.IsType<DoneEvent>(events[^1]);
    }

    [Fact]
    public async Task Invalid_json_arguments_return_error()
    {
        var model = new ScriptedChatCompletionService(Call("tools-echo", """{"text": "unterminated"""), Say("ok"));
        var result = (await Run(model)).OfType<ToolResultEvent>().Single();

        Assert.True(result.IsError);
        Assert.Contains("not valid JSON", result.Result);
    }

    [Fact]
    public async Task Missing_required_argument_returns_schema_error()
    {
        var tools = new StubToolRegistry();
        var result = (await Run(new ScriptedChatCompletionService(Call("tools-echo", "{}"), Say("ok")), tools)).OfType<ToolResultEvent>().Single();

        Assert.True(result.IsError);
        Assert.Contains("text", result.Result);
        Assert.Equal(0, tools.EchoInvocations);
    }

    [Fact]
    public async Task Unknown_argument_returns_schema_error()
    {
        var tools = new StubToolRegistry();
        var model = new ScriptedChatCompletionService(Call("tools-echo", """{"text":"a","extra":1}"""), Say("ok"));
        var result = (await Run(model, tools)).OfType<ToolResultEvent>().Single();

        Assert.True(result.IsError);
        Assert.Equal(0, tools.EchoInvocations);
    }

    [Fact]
    public async Task Model_can_self_correct_after_an_error()
    {
        var tools = new StubToolRegistry();
        var model = new ScriptedChatCompletionService(Call("tools-echo", "{}"), Call("tools-echo", """{"text":"fixed"}"""), Say("done"));

        var results = (await Run(model, tools)).OfType<ToolResultEvent>().ToList();

        Assert.Equal([true, false], results.Select(r => r.IsError));
        Assert.Equal(1, tools.EchoInvocations);
    }

    [Fact]
    public async Task Tool_exception_becomes_error_result()
    {
        var result = (await Run(new ScriptedChatCompletionService(Call("tools-boom", "{}"), Say("ok")))).OfType<ToolResultEvent>().Single();

        Assert.True(result.IsError);
        Assert.Contains("kaboom", result.Result);
    }

    [Fact]
    public async Task Mcp_error_envelope_is_unwrapped_and_flagged()
    {
        var result = (await Run(new ScriptedChatCompletionService(Call("tools-mcp_fail", "{}"), Say("ok")))).OfType<ToolResultEvent>().Single();

        Assert.True(result.IsError);
        Assert.Equal("ENOENT: nope", result.Result);
    }

    [Fact]
    public async Task Oversize_output_is_truncated_with_marker()
    {
        var options = new AgentOptions { MaxToolResultChars = 100 };
        var result = (await Run(new ScriptedChatCompletionService(Call("tools-big", "{}"), Say("ok")), options: options)).OfType<ToolResultEvent>().Single();

        Assert.False(result.IsError);
        Assert.Contains("[truncated: output was 500 characters", result.Result);
        Assert.StartsWith(new string('x', 100), result.Result);
    }

    [Fact]
    public async Task Repeated_identical_calls_stop_executing()
    {
        var tools = new StubToolRegistry();
        var same = Call("tools-echo", """{"text":"a"}""");
        var model = new ScriptedChatCompletionService(same, same, same, Say("fine"));

        var results = (await Run(model, tools, new AgentOptions { MaxIdenticalCalls = 2 })).OfType<ToolResultEvent>().ToList();

        Assert.Equal([false, false, true], results.Select(r => r.IsError));
        Assert.Equal(2, tools.EchoInvocations);
    }

    [Fact]
    public async Task Never_ending_model_stops_at_iteration_cap()
    {
        var model = new ScriptedChatCompletionService(Call("tools-big", "{}"));
        var events = await Run(model, options: new AgentOptions { MaxIterations = 3, MaxIdenticalCalls = 10 });

        Assert.Equal(3, model.Calls);
        Assert.Contains("3 iterations", events.OfType<ErrorEvent>().Single().Message);
        Assert.IsType<DoneEvent>(events[^1]);
    }

    [Fact]
    public async Task Run_timeout_emits_error_then_done()
    {
        var events = await Run(new ScriptedChatCompletionService(SlowChunks), options: new AgentOptions { RunTimeoutSeconds = 1 });

        Assert.Contains("timed out", events.OfType<ErrorEvent>().Single().Message);
        Assert.IsType<DoneEvent>(events[^1]);

        static IEnumerable<StreamingChatMessageContent> SlowChunks()
        {
            while (true)
            {
                Thread.Sleep(300);
                yield return new StreamingChatMessageContent(AuthorRole.Assistant, ".");
            }
        }
    }

    [Fact]
    public async Task Client_cancellation_propagates_instead_of_reporting_timeout()
    {
        using var cts = new CancellationTokenSource();
        var model = new ScriptedChatCompletionService(() => new[] { "a", "b", "c" }.Select(t => new StreamingChatMessageContent(AuthorRole.Assistant, t)));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in RunnerFactory.Create(model, new StubToolRegistry()).RunAsync("q", cts.Token))
                cts.Cancel();
        });
    }

    [Fact]
    public async Task Tool_name_without_a_plugin_prefix_is_reported_under_its_plain_name()
    {
        var events = await Run(new ScriptedChatCompletionService(Call("nope", "{}"), Say("ok")));

        Assert.Equal("nope", events.OfType<ToolCallEvent>().Single().Tool);
        Assert.Contains("Unknown tool 'nope'", events.OfType<ToolResultEvent>().Single().Result);
    }

    private sealed class CancellingToolRegistry(CancellationTokenSource cts) : Assistant.Core.Tools.IToolRegistry
    {
        public Task RegisterAsync(Kernel kernel, CancellationToken ct = default)
        {
            kernel.Plugins.AddFromFunctions("tools",
            [
                KernelFunctionFactory.CreateFromMethod(new Func<CancellationToken, string>(token =>
                {
                    cts.Cancel(); // the client goes away while the tool is running
                    token.ThrowIfCancellationRequested();
                    return "unreachable";
                }), "slow", "Runs until the client disconnects"),
            ]);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Client_disconnecting_during_a_tool_call_cancels_the_run_instead_of_becoming_a_tool_error()
    {
        using var cts = new CancellationTokenSource();
        var runner = RunnerFactory.Create(new ScriptedChatCompletionService(Call("tools-slow", "{}"), Say("never")), new CancellingToolRegistry(cts));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in runner.RunAsync("q", cts.Token)) { }
        });
    }
}
