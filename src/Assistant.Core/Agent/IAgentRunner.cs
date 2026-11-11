namespace Assistant.Core.Agent;

/// <summary>An event emitted while the agent runs a ReAct loop; streamed to the UI.</summary>
public abstract record AgentEvent;
/// <summary>
/// Always the first event of a run. <paramref name="RunId"/> is also in the server log lines the runner writes for that
/// run, so an error shown in the UI can be matched to the log.
/// </summary>
public sealed record RunStartedEvent(string RunId) : AgentEvent;
public sealed record TokenEvent(string Text) : AgentEvent;
public sealed record ToolCallEvent(string Tool, string ArgumentsJson) : AgentEvent;
public sealed record ToolResultEvent(string Tool, string Result, bool IsError) : AgentEvent;
/// <summary>The run stopped abnormally (timeout, iteration cap or an unexpected failure). A <see cref="DoneEvent"/> still follows.</summary>
public sealed record ErrorEvent(string Message) : AgentEvent;
public sealed record DoneEvent : AgentEvent;

/// <summary>LLM abstraction the API layer depends on; Semantic Kernel is an implementation detail.</summary>
public interface IAgentRunner
{
    IAsyncEnumerable<AgentEvent> RunAsync(string prompt, CancellationToken ct = default);
}
