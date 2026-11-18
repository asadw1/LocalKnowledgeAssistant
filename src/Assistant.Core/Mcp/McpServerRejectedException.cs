namespace Assistant.Core.Mcp;

/// <summary>
/// Thrown when a configured MCP server must not be used: its tool set or configuration breaks the read-only contract.
/// Startup treats it as "fail closed": the server is disposed and none of its tools reach the model.
/// </summary>
/// <param name="server">Name of the rejected server (from configuration).</param>
/// <param name="reason">What is wrong, written for the operator reading the log.</param>
public sealed class McpServerRejectedException(string server, string reason)
    : Exception($"MCP server '{server}' was rejected: {reason}")
{
    /// <summary>Name of the rejected server.</summary>
    public string Server { get; } = server;
}
