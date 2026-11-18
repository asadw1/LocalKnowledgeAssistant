namespace Assistant.Core.Mcp;

/// <summary>A tool as advertised by a server, reduced to what the read-only checks need.</summary>
/// <param name="Name">Tool name.</param>
/// <param name="ReadOnlyHint">The server's own <c>readOnlyHint</c> annotation, or null when it gives none.</param>
/// <param name="DestructiveHint">The server's own <c>destructiveHint</c> annotation, or null when it gives none.</param>
public sealed record OfferedTool(string Name, bool? ReadOnlyHint = null, bool? DestructiveHint = null);

/// <summary>
/// Startup checks that keep a third-party MCP server read-only (docs/SECURITY.md section 3.1). Failing a check throws
/// <see cref="McpServerRejectedException"/>, so the server is dropped entirely rather than run in a degraded state.
/// </summary>
public static class McpServerVerifier
{
    /// <summary>
    /// Checks a server's configuration before it is launched: <see cref="McpServerDefinition.PathRoot"/>, when set,
    /// must be one of the arguments the server is started with, so the guard and the server agree on the root.
    /// </summary>
    /// <param name="server">The configured server.</param>
    /// <param name="expandToken">Expands tokens such as <c>{BaseDirectory}</c> in an argument.</param>
    /// <exception cref="ArgumentNullException"><paramref name="server"/> or <paramref name="expandToken"/> is null.</exception>
    /// <exception cref="McpServerRejectedException">The root is not one of the server's arguments.</exception>
    public static void VerifyConfiguration(McpServerDefinition server, Func<string, string> expandToken)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(expandToken);
        if (server.PathRoot is null) return;

        var root = Path.GetFullPath(expandToken(server.PathRoot));
        var launchedWithRoot = server.Args.Any(a => SamePath(root, a, expandToken));
        if (!launchedWithRoot)
            throw new McpServerRejectedException(server.Name,
                $"PathRoot '{server.PathRoot}' is not one of its launch arguments, so the path guard and the server could disagree about the root.");
    }

    /// <summary>
    /// Checks the tools a server advertises against its allow-list and its own read-only annotations.
    /// Returns the names of tools that will be withheld because they are not allow-listed, for logging: a new name
    /// here after a server upgrade is exactly how <c>append_file</c> and <c>copy_file</c> first showed up.
    /// </summary>
    /// <param name="server">The configured server.</param>
    /// <param name="offered">Every tool the server advertised, before any filtering.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="McpServerRejectedException">
    /// An allow-listed tool is missing (the server no longer matches what was reviewed), or one declares itself
    /// non-read-only or destructive.
    /// </exception>
    public static IReadOnlyList<string> VerifyTools(McpServerDefinition server, IReadOnlyList<OfferedTool> offered)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(offered);
        if (server.IncludeTools is null) return [];

        var byName = offered.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);

        var missing = server.IncludeTools.Where(name => !byName.ContainsKey(name)).ToList();
        if (missing.Count > 0)
            throw new McpServerRejectedException(server.Name,
                $"allow-listed tool(s) not offered: {string.Join(", ", missing)}. The server version may have changed; review it before updating IncludeTools.");

        // The hints are the server's own claim and cannot prove a tool is safe, but a tool that says it writes
        // or destroys must never be on a read-only allow-list.
        var contradicting = server.IncludeTools
            .Select(name => byName[name])
            .Where(t => t.ReadOnlyHint == false || t.DestructiveHint == true)
            .Select(t => t.Name)
            .ToList();
        if (contradicting.Count > 0)
            throw new McpServerRejectedException(server.Name,
                $"allow-listed tool(s) declare themselves as writing or destructive: {string.Join(", ", contradicting)}.");

        var allowed = new HashSet<string>(server.IncludeTools, StringComparer.OrdinalIgnoreCase);
        return offered.Where(t => !allowed.Contains(t.Name)).Select(t => t.Name).Order().ToList();
    }

    private static bool SamePath(string fullRoot, string arg, Func<string, string> expandToken)
    {
        try
        {
            return string.Equals(fullRoot, Path.GetFullPath(expandToken(arg)),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            return false; // an argument containing characters no path can hold is not the root
        }
    }
}
