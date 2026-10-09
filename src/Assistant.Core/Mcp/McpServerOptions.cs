namespace Assistant.Core.Mcp;

/// <summary>A local MCP stdio server to launch and expose as Semantic Kernel plugins.</summary>
public sealed class McpServerOptions
{
    public const string SectionName = "Mcp";

    public List<McpServerDefinition> Servers { get; init; } = [];
}

/// <param name="ExcludeTools">Tool names withheld from the model (e.g. write tools to keep the agent read-only).</param>
public sealed record McpServerDefinition(string Name, string Command, string[] Args, string[]? ExcludeTools = null);
