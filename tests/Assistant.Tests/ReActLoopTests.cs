using Assistant.Core.Agent;
using Assistant.Tests.Fakes;
using Microsoft.Extensions.Logging;
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
            ["RunStartedEvent", "TokenEvent", "ToolCallEvent", "ToolResultEvent", "TokenEvent", "DoneEvent"],
            events.Select(e => e.GetType().Name));
        Assert.Equal(new ToolCallEvent("tools-echo", """{"text":"hi"}"""), events[2]);
        Assert.Equal(new ToolResultEvent("tools-echo", "echo:hi", false), events[3]);
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
        var logger = new CapturingLogger<SemanticKernelAgentRunner>();
        var events = await RunnerFactory.Create(new ScriptedChatCompletionService(SlowChunks), new StubToolRegistry(), new AgentOptions { RunTimeoutSeconds = 1 }, logger)
            .RunAsync("q").ToListAsync();

        Assert.Contains("timed out", events.OfType<ErrorEvent>().Single().Message);
        Assert.IsType<DoneEvent>(events[^1]);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains(events.OfType<RunStartedEvent>().Single().RunId));

        static IEnumerable<StreamingChatMessageContent> SlowChunks()
        {
            while (true)
            {
                Thread.Sleep(300);
                yield return new StreamingChatMessageContent(AuthorRole.Assistant, ".");
            }
        }
    }

    private static IEnumerable<StreamingChatMessageContent> Throw(Exception ex, string? tokenFirst = null)
    {
        if (tokenFirst is not null) yield return new StreamingChatMessageContent(AuthorRole.Assistant, tokenFirst);
        throw ex;
    }

    [Fact]
    public async Task Unreachable_model_server_emits_error_then_done_instead_of_throwing()
    {
        var model = new ScriptedChatCompletionService(() => Throw(new HttpRequestException("Connection refused (127.0.0.1:11434)")));

        var events = await Run(model);

        Assert.Equal(["RunStartedEvent", "ErrorEvent", "DoneEvent"], events.Select(e => e.GetType().Name));
        Assert.Contains("Ollama", events.OfType<ErrorEvent>().Single().Message);
    }

    [Fact]
    public async Task Http_client_timeout_is_reported_as_a_failure_not_a_cancellation()
    {
        var model = new ScriptedChatCompletionService(() => Throw(new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout")));

        var events = await Run(model);

        Assert.Equal(["RunStartedEvent", "ErrorEvent", "DoneEvent"], events.Select(e => e.GetType().Name));
    }

    [Fact]
    public async Task Failure_after_some_tokens_keeps_them_and_then_reports_error_and_done()
    {
        var model = new ScriptedChatCompletionService(() => Throw(new InvalidOperationException("boom-secret-detail"), tokenFirst: "Part"));

        var events = await Run(model);

        Assert.Equal(["RunStartedEvent", "TokenEvent", "ErrorEvent", "DoneEvent"], events.Select(e => e.GetType().Name));
        Assert.DoesNotContain("boom-secret-detail", events.OfType<ErrorEvent>().Single().Message);
        Assert.Contains("server log", events.OfType<ErrorEvent>().Single().Message);
    }

    [Fact]
    public async Task Unexpected_failure_is_logged_with_its_exception_type_and_run_id()
    {
        var logger = new CapturingLogger<SemanticKernelAgentRunner>();
        var model = new ScriptedChatCompletionService(() => Throw(new InvalidOperationException("boom-secret-detail")));

        var events = await RunnerFactory.Create(model, new StubToolRegistry(), logger: logger).RunAsync("q").ToListAsync();

        var runId = events.OfType<RunStartedEvent>().Single().RunId;
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("InvalidOperationException") && e.Message.Contains(runId));
    }

    [Fact]
    public async Task First_event_is_a_short_hex_run_id_that_differs_between_runs()
    {
        var runner = RunnerFactory.Create(new ScriptedChatCompletionService(Say("a")), new StubToolRegistry());

        var first = (await runner.RunAsync("q").ToListAsync())[0];
        var second = (await runner.RunAsync("q").ToListAsync())[0];

        var id1 = Assert.IsType<RunStartedEvent>(first).RunId;
        var id2 = Assert.IsType<RunStartedEvent>(second).RunId;
        Assert.Matches("^[0-9a-f]{12}$", id1);
        Assert.NotEqual(id1, id2);
    }

    [Fact]
    public async Task Every_info_or_higher_runner_log_line_carries_the_run_id()
    {
        var logger = new CapturingLogger<SemanticKernelAgentRunner>();
        var model = new ScriptedChatCompletionService(
            Call("tools-echo", """{"text":"hi"}"""), // ok: iteration log
            Call("tools-nope", "{}"),                // rejected: validation warning
            Call("tools-boom", "{}"),                // tool throws: warning with exception
            Say("done"));

        var events = await RunnerFactory.Create(model, new StubToolRegistry(), logger: logger).RunAsync("q").ToListAsync();

        var runId = events.OfType<RunStartedEvent>().Single().RunId;
        var lines = logger.Entries.Where(e => e.Level >= LogLevel.Information).ToList();
        Assert.True(lines.Count >= 4);
        Assert.All(lines, e => Assert.Contains(runId, e.Message));
    }

    [Fact]
    public async Task Iteration_cap_log_carries_the_run_id()
    {
        var logger = new CapturingLogger<SemanticKernelAgentRunner>();
        var model = new ScriptedChatCompletionService(Call("tools-echo", """{"text":"x"}"""));

        var events = await RunnerFactory.Create(model, new StubToolRegistry(), new AgentOptions { MaxIterations = 2, MaxIdenticalCalls = 99 }, logger)
            .RunAsync("q").ToListAsync();

        var runId = events.OfType<RunStartedEvent>().Single().RunId;
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("iteration cap") && e.Message.Contains(runId));
    }

    [Fact]
    public async Task Tool_registration_failure_emits_error_then_done()
    {
        var events = await RunnerFactory.Create(new ScriptedChatCompletionService(Say("x")), new ThrowingToolRegistry())
            .RunAsync("q").ToListAsync();

        Assert.Equal(["RunStartedEvent", "ErrorEvent", "DoneEvent"], events.Select(e => e.GetType().Name));
    }

    private sealed class ThrowingToolRegistry : Assistant.Core.Tools.IToolRegistry
    {
        public Task RegisterAsync(Kernel kernel, CancellationToken ct = default) => throw new InvalidOperationException("registry down");
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
