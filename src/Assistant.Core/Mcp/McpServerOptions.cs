namespace Assistant.Core.Mcp;

/// <summary>A local MCP stdio server to launch and expose as Semantic Kernel plugins.</summary>
public sealed class McpServerOptions
{
    public const string SectionName = "Mcp";

    public List<McpServerDefinition> Servers { get; init; } = [];
}

/// <param name="ExcludeTools">Tool names withheld from the model (e.g. tools that overlap, or that a 7B model misuses).</param>
/// <param name="IncludeTools">
/// Allow-list: when set, only these tools are offered, so a tool a server adds in a later version stays hidden until it is
/// listed here. This is what keeps a third-party server read-only; <see cref="ExcludeTools"/> is then applied on top.
/// <c>null</c> offers every tool; an empty array offers none.
/// </param>
public sealed record McpServerDefinition(
    string Name, string Command, string[] Args, string[]? ExcludeTools = null, string[]? IncludeTools = null);
