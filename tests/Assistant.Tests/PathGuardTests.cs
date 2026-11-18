using System.Text.Json;
using Assistant.Core.Tools;

namespace Assistant.Tests;

public sealed class PathGuardTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "lka-guard-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly string _outside;

    public PathGuardTests()
    {
        _root = Path.Combine(_base, "root");
        _outside = Path.Combine(_base, "outside");
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        Directory.CreateDirectory(_outside);
        File.WriteAllText(Path.Combine(_root, "a.txt"), "inside");
        File.WriteAllText(Path.Combine(_outside, "secret.txt"), "secret");
    }

    public void Dispose()
    {
        DirectoryLinks.DeleteTree(_base);
    }

    private static string? Check(PathGuard guard, string name, object? value) =>
        guard.Check(new Dictionary<string, object?> { [name] = value });

    private string? CheckPath(string path, Func<string, string?>? resolver = null) =>
        Check(new PathGuard(_root, resolver), "path", path);

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    // ---- inside the root

    [Fact]
    public void Paths_inside_the_root_are_allowed_whether_or_not_they_exist()
    {
        Assert.Null(CheckPath(_root));
        Assert.Null(CheckPath(Path.Combine(_root, "a.txt")));
        Assert.Null(CheckPath(Path.Combine(_root, "sub")));
        Assert.Null(CheckPath(Path.Combine(_root, "missing", "deeper.txt")));
    }

    [Fact]
    public void Forward_slashes_are_accepted_for_paths_inside_the_root()
    {
        Assert.Null(CheckPath(Path.Combine(_root, "sub").Replace('\\', '/')));
    }

    [Fact]
    public void Matching_follows_the_operating_systems_case_rules()
    {
        if (!OperatingSystem.IsWindows()) return; // case-sensitive file systems treat the upper-cased path as a different folder

        Assert.Null(CheckPath(Path.Combine(_root, "A.TXT").ToUpperInvariant()));
    }

    [Fact]
    public void Values_that_arrive_as_JSON_elements_are_checked_like_strings()
    {
        var guard = new PathGuard(_root);

        Assert.Null(Check(guard, "path", Json(JsonSerializer.Serialize(Path.Combine(_root, "a.txt")))));
        Assert.Contains("outside", Check(guard, "path", Json(JsonSerializer.Serialize(Path.Combine(_outside, "secret.txt"))))!);
    }

    // ---- outside the root

    [Fact]
    public void An_absolute_path_outside_the_root_is_blocked_with_a_recoverable_message()
    {
        var error = CheckPath(Path.Combine(_outside, "secret.txt"));

        Assert.Contains("Argument 'path' was blocked", error);
        Assert.Contains("outside the allowed folder", error);
        Assert.Contains(_root, error); // tells the model where it may look
    }

    [Fact]
    public void A_sibling_folder_that_merely_shares_the_roots_prefix_is_outside()
    {
        var sibling = _root + "-evil";
        Directory.CreateDirectory(sibling);

        Assert.Contains("outside", CheckPath(Path.Combine(sibling, "x.txt"))!);
    }

    [Theory]
    [InlineData(@"..\outside\secret.txt")]
    [InlineData("secret.txt")]
    [InlineData("./a.txt")]
    public void Relative_paths_are_blocked_because_the_server_would_resolve_them_elsewhere(string path)
    {
        Assert.Contains("absolute path", CheckPath(path)!);
    }

    [Fact]
    public void Parent_segments_are_blocked_even_when_the_result_would_stay_inside()
    {
        Assert.Contains("'..'", CheckPath(Path.Combine(_root, "sub", "..", "a.txt"))!);
        Assert.Contains("'..'", CheckPath(_root + "/sub/../../outside/secret.txt")!);
    }

    [Theory]
    [InlineData(@"\\server\share\file.txt")]
    [InlineData("//server/share/file.txt")]
    [InlineData(@"\\?\C:\Windows\win.ini")]
    [InlineData(@"\\.\PhysicalDrive0")]
    public void Network_and_device_paths_are_blocked(string path)
    {
        Assert.Contains("network and device", CheckPath(path)!);
    }

    [Fact]
    public void A_colon_beyond_the_drive_letter_is_blocked_because_it_names_an_alternate_data_stream()
    {
        Assert.Contains("':'", CheckPath(Path.Combine(_root, "a.txt") + ":hidden")!);
    }

    [Fact]
    public void Empty_blank_and_null_character_paths_are_blocked()
    {
        Assert.Contains("empty", CheckPath("")!);
        Assert.Contains("empty", CheckPath("   ")!);
        Assert.Contains("null character", CheckPath(Path.Combine(_root, "a\0.txt"))!);
    }

    // ---- argument shapes

    [Fact]
    public void Arguments_that_are_not_paths_are_ignored()
    {
        var guard = new PathGuard(_root);

        Assert.Null(Check(guard, "text", "../../etc/passwd"));
        Assert.Null(Check(guard, "sql", "SELECT 1"));
        Assert.Null(guard.Check([]));
    }

    [Fact]
    public void Argument_names_match_case_insensitively()
    {
        Assert.Contains("outside", Check(new PathGuard(_root), "PATH", Path.Combine(_outside, "secret.txt"))!);
    }

    [Fact]
    public void A_list_of_paths_is_blocked_when_any_entry_is_outside()
    {
        var guard = new PathGuard(_root);
        var good = Path.Combine(_root, "a.txt");

        Assert.Null(Check(guard, "paths", new[] { good, good }));
        Assert.Contains("outside", Check(guard, "paths", new[] { good, Path.Combine(_outside, "secret.txt") })!);
        Assert.Contains("outside", Check(guard, "paths", Json(JsonSerializer.Serialize(new[] { good, Path.Combine(_outside, "secret.txt") })))!);
    }

    [Fact]
    public void Values_that_are_not_strings_are_blocked()
    {
        var guard = new PathGuard(_root);

        Assert.Contains("must be a string", Check(guard, "path", null)!);
        Assert.Contains("must be a string", Check(guard, "path", 42)!);
        Assert.Contains("must be a string", Check(guard, "path", new { nested = "x" })!);
        Assert.Contains("must be a string", Check(guard, "paths", new object[] { Path.Combine(_root, "a.txt"), 7 })!);
    }

    // ---- glob patterns

    [Theory]
    [InlineData("*.md")]
    [InlineData("**/*.txt")]
    [InlineData("notes/2026-*")]
    public void Relative_patterns_are_allowed(string pattern)
    {
        Assert.Null(Check(new PathGuard(_root), "pattern", pattern));
    }

    [Theory]
    [InlineData("../*")]
    [InlineData(@"sub\..\..\*")]
    [InlineData("/etc/*")]
    [InlineData(@"C:\*")]
    public void Patterns_that_climb_out_or_are_rooted_are_blocked(string pattern)
    {
        Assert.Contains("Argument 'pattern' was blocked", Check(new PathGuard(_root), "pattern", pattern)!);
    }

    [Fact]
    public void Every_exclude_pattern_is_checked()
    {
        Assert.Null(Check(new PathGuard(_root), "excludePatterns", new[] { "*.log", "tmp/**" }));
        Assert.Contains("'..'", Check(new PathGuard(_root), "excludePatterns", new[] { "*.log", "../x" })!);
    }

    // ---- links (resolved through an injected resolver so no OS privilege is needed)

    [Fact]
    public void A_link_inside_the_root_that_points_outside_is_blocked()
    {
        Directory.CreateDirectory(Path.Combine(_root, "link"));
        string? Resolve(string p) => p == Path.Combine(_root, "link") ? _outside : null;

        var error = CheckPath(Path.Combine(_root, "link", "secret.txt"), Resolve);

        Assert.Contains("outside the allowed folder", error);
    }

    [Fact]
    public void A_link_that_stays_inside_the_root_is_allowed()
    {
        Directory.CreateDirectory(Path.Combine(_root, "alias"));
        string? Resolve(string p) => p == Path.Combine(_root, "alias") ? Path.Combine(_root, "sub") : null;

        Assert.Null(CheckPath(Path.Combine(_root, "alias", "file.txt"), Resolve));
    }

    [Fact]
    public void Chains_of_links_are_followed_to_their_end()
    {
        Directory.CreateDirectory(Path.Combine(_root, "first"));
        Directory.CreateDirectory(Path.Combine(_root, "second"));
        string? Resolve(string p) =>
            p == Path.Combine(_root, "first") ? Path.Combine(_root, "second") :
            p == Path.Combine(_root, "second") ? _outside : null;

        Assert.Contains("outside", CheckPath(Path.Combine(_root, "first", "secret.txt"), Resolve)!);
    }

    [Fact]
    public void A_root_that_is_itself_a_link_is_judged_by_its_real_location()
    {
        var real = Path.Combine(_base, "real-root");
        Directory.CreateDirectory(real);
        string? Resolve(string p) => p == _root ? real : null;

        Assert.Null(CheckPath(Path.Combine(_root, "a.txt"), Resolve));
        Assert.Contains("outside", CheckPath(Path.Combine(_outside, "secret.txt"), Resolve)!);
    }

    [Fact]
    public void A_link_loop_is_reported_as_unverifiable_instead_of_hanging_or_throwing()
    {
        Directory.CreateDirectory(Path.Combine(_root, "loop"));
        string? Resolve(string p) => p == Path.Combine(_root, "loop") ? p : null;

        Assert.Contains("could not be verified", CheckPath(Path.Combine(_root, "loop", "x.txt"), Resolve)!);
    }

    [Fact]
    public void A_failure_while_resolving_blocks_the_call()
    {
        string? Resolve(string p) => throw new UnauthorizedAccessException("denied");

        var error = CheckPath(Path.Combine(_root, "a.txt"), Resolve);

        Assert.Contains("could not be verified (UnauthorizedAccessException)", error);
    }

    [Fact]
    public void A_real_directory_link_pointing_outside_is_blocked_and_one_pointing_inside_is_not()
    {
        var escape = Path.Combine(_root, "escape");
        var inward = Path.Combine(_root, "inward");
        if (!DirectoryLinks.TryCreate(escape, _outside) || !DirectoryLinks.TryCreate(inward, Path.Combine(_root, "sub")))
            return; // no permission to create links on this machine; the injected-resolver tests above cover the logic

        var guard = new PathGuard(_root);

        Assert.Contains("outside", Check(guard, "path", Path.Combine(escape, "secret.txt"))!);
        Assert.Null(Check(guard, "path", Path.Combine(inward, "file.txt")));
    }

    // ---- construction

    [Fact]
    public void A_relative_root_is_resolved_to_an_absolute_path()
    {
        var guard = new PathGuard("some-folder");

        Assert.True(Path.IsPathFullyQualified(guard.Root));
        Assert.EndsWith("some-folder", guard.Root);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void A_missing_root_is_rejected(string? root)
    {
        Assert.ThrowsAny<ArgumentException>(() => new PathGuard(root!));
    }

    [Fact]
    public void Checking_null_arguments_throws()
    {
        Assert.Throws<ArgumentNullException>(() => new PathGuard(_root).Check(null!));
    }
}
