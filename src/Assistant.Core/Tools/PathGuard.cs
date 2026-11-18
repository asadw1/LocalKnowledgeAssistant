using System.Text.Json;
using System.Text.Json.Nodes;

namespace Assistant.Core.Tools;

/// <summary>
/// Our own check, in front of a third-party filesystem MCP server, that every model-supplied path stays inside one
/// root (docs/SECURITY.md section 3.1). The server enforces its own allow-list too; this is a second, independent
/// layer so a bug or an upgrade in either does not open the filesystem on its own.
/// Fails closed: anything it cannot positively show to be inside the root is rejected.
/// </summary>
/// <remarks>
/// Paths must be absolute because a relative path would be resolved by the server against its own working directory,
/// which is not necessarily the root, so this class could approve a path the server then reads elsewhere.
/// Links (symlinks, junctions) are followed component by component, so a link inside the root that points outside it
/// is rejected. A path that does not exist yet is checked below its deepest existing ancestor.
/// </remarks>
public sealed class PathGuard
{
    /// <summary>Argument names that carry a file or directory path.</summary>
    private static readonly HashSet<string> PathArguments =
        new(["path", "paths", "source", "destination", "directory", "file"], StringComparer.OrdinalIgnoreCase);

    /// <summary>Argument names that carry glob patterns, which are relative to a path argument and must not climb out of it.</summary>
    private static readonly HashSet<string> PatternArguments = new(["pattern", "excludePatterns"], StringComparer.OrdinalIgnoreCase);

    private const int MaxLinkDepth = 32;

    private readonly string _root;
    private readonly Func<string, string?> _resolveLink;

    /// <summary>Creates a guard for <paramref name="root"/>.</summary>
    /// <param name="root">The permitted folder. A relative value resolves against the current directory, as the launched server's does.</param>
    /// <param name="resolveLink">
    /// Returns the final target of an existing link, or null when the path is not a link. Defaults to the file system;
    /// tests substitute their own so link handling does not depend on OS privileges.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="root"/> is null, empty or whitespace.</exception>
    public PathGuard(string root, Func<string, string?>? resolveLink = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.GetFullPath(root);
        _resolveLink = resolveLink ?? ResolveLinkOnDisk;
    }

    /// <summary>The permitted folder, as an absolute path.</summary>
    public string Root => _root;

    /// <summary>Checks every path-like argument of one tool call.</summary>
    /// <param name="arguments">The call's arguments, already validated against the tool's schema.</param>
    /// <returns>Null when the call may run; otherwise an error written for the model that says how to recover.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="arguments"/> is null.</exception>
    public string? Check(IEnumerable<KeyValuePair<string, object?>> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        foreach (var (name, value) in arguments)
        {
            var isPath = PathArguments.Contains(name);
            if (!isPath && !PatternArguments.Contains(name)) continue;

            var texts = AsStrings(value);
            if (texts is null) return $"Argument '{name}' must be a string or a list of strings.";

            foreach (var text in texts)
            {
                var problem = isPath ? CheckPath(text) : CheckPattern(text);
                if (problem is not null) return $"Argument '{name}' was blocked: {problem}";
            }
        }
        return null;
    }

    /// <summary>Reads a string or an array of strings out of an argument value; null for anything else.</summary>
    private static string[]? AsStrings(object? value)
    {
        var node = value is null ? null : JsonSerializer.SerializeToNode(value);
        if (node is JsonValue single && single.TryGetValue<string>(out var text)) return [text];
        if (node is JsonArray array)
        {
            var items = new List<string>();
            foreach (var item in array)
            {
                if (item is JsonValue v && v.TryGetValue<string>(out var s)) items.Add(s);
                else return null;
            }
            return [.. items];
        }
        return null;
    }

    private string? CheckPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "the path is empty.";
        if (path.Contains('\0')) return "the path contains a null character.";
        // UNC and device paths (\\server\share, \\?\C:\) can reach places a drive-letter path cannot.
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
            return "network and device paths are not allowed.";
        // A colon after the drive letter is an alternate data stream on Windows.
        if (path.IndexOf(':', path.Length > 1 && path[1] == ':' ? 2 : 0) >= 0) return "the path contains ':' outside a drive letter.";
        if (!Path.IsPathFullyQualified(path)) return $"use an absolute path inside '{_root}'.";
        if (HasParentSegment(path)) return "'..' segments are not allowed.";

        try
        {
            var full = Path.GetFullPath(path);
            var realRoot = Canonicalize(_root, 0);
            // Lexical first (the path as written must be under the root as written), then real: with every link
            // resolved, the path must be under the root's real location. The root itself may be a link.
            if (!IsWithin(_root, full) || !IsWithin(realRoot, Canonicalize(full, 0)))
                return $"the path is outside the allowed folder '{_root}'. Use an absolute path inside it.";
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Includes a link loop or too-deep link chain. A path that cannot be resolved cannot be shown to be safe.
            return $"the path could not be verified ({ex.GetType().Name}). Try a different path inside '{_root}'.";
        }
    }

    private static string? CheckPattern(string pattern)
    {
        if (Path.IsPathRooted(pattern) || pattern.Contains(':')) return "glob patterns must be relative to the search path.";
        return HasParentSegment(pattern) ? "'..' segments are not allowed." : null;
    }

    private static bool HasParentSegment(string path) => path.Split('/', '\\').Any(segment => segment == "..");

    /// <summary>Is <paramref name="candidate"/> the root or below it? Case rules follow the OS.</summary>
    private static bool IsWithin(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        if (relative == ".") return true;
        var climbs = relative == ".." ||
                     relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                     relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
        return !climbs && !Path.IsPathRooted(relative);
    }

    /// <summary>
    /// Rewrites <paramref name="fullPath"/> with every existing link replaced by its target, one component at a time.
    /// Recursive because a link's target may itself pass through links.
    /// </summary>
    private string Canonicalize(string fullPath, int depth)
    {
        if (depth > MaxLinkDepth) throw new IOException("too many nested links");

        var current = Path.GetPathRoot(fullPath)!; // fullPath is absolute, so it has a root
        var separators = new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
        foreach (var segment in fullPath[current.Length..].Split(separators, StringSplitOptions.RemoveEmptyEntries))
        {
            var next = Path.Combine(current, segment);
            var target = Path.Exists(next) ? _resolveLink(next) : null;
            current = target is null ? next : Canonicalize(Path.GetFullPath(target), depth + 1);
        }
        return current;
    }

    private static string? ResolveLinkOnDisk(string path)
    {
        FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        return info.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
    }
}
