using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Assistant.Core.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.Ollama;

namespace Assistant.Core.Agent;

/// <summary>
/// ReAct loop over a Semantic Kernel chat service. Function calling is driven manually (auto-invoke off) so every
/// call is validated, streamed to the UI and bounded by an iteration cap, a run timeout and repeat detection.
/// </summary>
public sealed class SemanticKernelAgentRunner(
    IChatCompletionService chat,
    Kernel kernel,
    IToolRegistry tools,
    IOptions<OllamaOptions> ollama,
    IOptions<AgentOptions> agent,
    ILogger<SemanticKernelAgentRunner> logger) : IAgentRunner
{
    public async IAsyncEnumerable<AgentEvent> RunAsync(string prompt, [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(agent.Value.RunTimeoutSeconds));

        await using var loop = Loop(prompt, timeout.Token).GetAsyncEnumerator(timeout.Token);
        var timedOut = false;
        string? failure = null;
        while (true)
        {
            AgentEvent next;
            try
            {
                if (!await loop.MoveNextAsync()) break;
                next = loop.Current;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeout.IsCancellationRequested)
            {
                timedOut = true;
                break;
            }
            // Boundary: the response has already started, so an exception cannot change the HTTP status. Turn it into
            // an ErrorEvent + DoneEvent the UI can show. A caller cancellation (client disconnect or Stop) is not
            // matched here and propagates, so nothing further is sent to a client that has gone. This still catches a
            // TaskCanceledException from an HttpClient timeout, which is a failure rather than a cancellation.
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogError(ex, "Agent run failed unexpectedly ({ExceptionType})", ex.GetType().Name);
                failure = FailureMessage(ex);
                break;
            }
            yield return next;
        }

        if (timedOut)
        {
            logger.LogWarning("Agent run exceeded {Seconds}s timeout", agent.Value.RunTimeoutSeconds);
            yield return new ErrorEvent($"The run timed out after {agent.Value.RunTimeoutSeconds} seconds.");
        }
        else if (failure is not null)
        {
            yield return new ErrorEvent(failure);
        }

        yield return new DoneEvent();
    }

    /// <summary>
    /// Text for the UI when a run throws. Exception messages can quote URLs, paths or arguments, so only the
    /// well-understood "model server unreachable" case gets specifics; everything else points at the server log.
    /// </summary>
    private static string FailureMessage(Exception ex) => ex is HttpRequestException or TaskCanceledException
        ? "Could not reach the model server. Check that Ollama is running and the configured model is pulled."
        : "The run failed unexpectedly. See the server log for details.";

    private async IAsyncEnumerable<AgentEvent> Loop(string prompt, [EnumeratorCancellation] CancellationToken ct)
    {
        await tools.RegisterAsync(kernel, ct);
        logger.LogDebug("Kernel functions: {Functions}", string.Join(", ", kernel.Plugins.GetFunctionsMetadata().Select(f => $"[{f.PluginName}|{f.Name}]")));

        var history = new ChatHistory(ollama.Value.SystemPrompt);
        history.AddUserMessage(prompt);

#pragma warning disable SKEXP0070 // Ollama connector is prerelease
        var settings = new OllamaPromptExecutionSettings
        {
            FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(autoInvoke: false),
        };
#pragma warning restore SKEXP0070

        var executed = new Dictionary<string, int>();

        for (var iteration = 1; iteration <= agent.Value.MaxIterations; iteration++)
        {
            var text = new StringBuilder();
            var calls = new FunctionCallContentBuilder();

            await foreach (var chunk in chat.GetStreamingChatMessageContentsAsync(history, settings, kernel, ct))
            {
                calls.Append(chunk);
                if (!string.IsNullOrEmpty(chunk.Content))
                {
                    text.Append(chunk.Content);
                    yield return new TokenEvent(chunk.Content);
                }
            }

            var requested = calls.Build();
            if (requested.Count == 0) yield break; // final answer

            var assistant = new ChatMessageContent(AuthorRole.Assistant, text.ToString());
            foreach (var call in requested) assistant.Items.Add(call);
            history.Add(assistant);

            foreach (var call in requested)
            {
                var name = string.IsNullOrEmpty(call.PluginName) ? call.FunctionName : $"{call.PluginName}-{call.FunctionName}";
                var argsJson = JsonSerializer.Serialize(call.Arguments ?? []);
                yield return new ToolCallEvent(name, argsJson);

                var (result, isError) = await ExecuteAsync(call, argsJson, executed, ct);
                logger.LogInformation("Iteration {Iteration}: tool {Tool} {Outcome}", iteration, name, isError ? "failed" : "ok");

                yield return new ToolResultEvent(name, result, isError);
                history.Add(new ChatMessageContent(AuthorRole.Tool, [new FunctionResultContent(call, result)]));
            }
        }

        logger.LogWarning("Agent hit the {Max}-iteration cap", agent.Value.MaxIterations);
        yield return new ErrorEvent($"Stopped after {agent.Value.MaxIterations} iterations without a final answer.");
    }

    private async Task<(string Result, bool IsError)> ExecuteAsync(
        FunctionCallContent call, string argsJson, Dictionary<string, int> executed, CancellationToken ct)
    {
        var validated = ToolCallValidator.Validate(kernel, call);
        if (!validated.IsValid)
        {
            logger.LogWarning("Rejected tool call: {Error}", validated.Error);
            return (validated.Error!, true);
        }

        var signature = $"{call.PluginName}-{call.FunctionName}:{argsJson}";
        executed[signature] = executed.GetValueOrDefault(signature) + 1;
        if (executed[signature] > agent.Value.MaxIdenticalCalls)
            return ("You already made this exact call and received its result. Use that result, or try different arguments, or answer now.", true);

        try
        {
            var result = await validated.Function!.InvokeAsync(kernel, validated.Arguments, ct);
            var (text, isError) = ToolOutput.Parse(result.ToString());
            return (Truncate(text), isError);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Tool {Tool} threw", call.FunctionName);
            return ($"Tool '{call.FunctionName}' failed: {ex.Message}", true);
        }
    }

    private string Truncate(string result)
    {
        var max = agent.Value.MaxToolResultChars;
        return result.Length <= max
            ? result
            : $"{result[..max]}\n[truncated: output was {result.Length} characters, showing the first {max}]";
    }
}
