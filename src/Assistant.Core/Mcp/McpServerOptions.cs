namespace Assistant.Core.Mcp;

/// <summary>A local MCP stdio server to launch and expose as Semantic Kernel plugins.</summary>
public sealed class McpServerOptions
{
    public const string SectionName = "Mcp";

    public List<McpServerDefinition> Servers { get; init; } = [];
}

public sealed record McpServerDefinition(string Name, string Command, string[] Args);
