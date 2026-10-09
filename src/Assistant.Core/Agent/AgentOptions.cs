using System.ComponentModel.DataAnnotations;

namespace Assistant.Core.Agent;

/// <summary>Limits that keep the ReAct loop bounded.</summary>
public sealed class AgentOptions
{
    public const string SectionName = "Agent";

    /// <summary>Maximum model calls (loop iterations) per run.</summary>
    [Range(1, 50)]
    public int MaxIterations { get; init; } = 8;

    [Range(1, 3600)]
    public int RunTimeoutSeconds { get; init; } = 120;

    /// <summary>Tool output longer than this is truncated before it reaches the model.</summary>
    [Range(100, 1_000_000)]
    public int MaxToolResultChars { get; init; } = 8000;

    /// <summary>How many times the same tool call with the same arguments may execute in one run.</summary>
    [Range(1, 10)]
    public int MaxIdenticalCalls { get; init; } = 2;
}
