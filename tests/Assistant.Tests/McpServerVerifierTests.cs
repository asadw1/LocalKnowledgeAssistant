using Assistant.Core.Mcp;

namespace Assistant.Tests;

public sealed class McpServerVerifierTests
{
    private static McpServerDefinition Server(string[]? include = null, string[]? args = null, string? pathRoot = null) =>
        new("fs", "node", args ?? [], IncludeTools: include, PathRoot: pathRoot);

    private static OfferedTool Tool(string name, bool? readOnly = null, bool? destructive = null) => new(name, readOnly, destructive);

    // ---- tools

    [Fact]
    public void Without_an_allow_list_nothing_is_checked_or_withheld()
    {
        Assert.Empty(McpServerVerifier.VerifyTools(Server(), [Tool("anything", readOnly: false)]));
    }

    [Fact]
    public void Tools_that_are_not_allow_listed_are_reported_sorted_so_drift_is_visible_in_the_log()
    {
        var withheld = McpServerVerifier.VerifyTools(
            Server(include: ["read_text_file"]),
            [Tool("read_text_file"), Tool("copy_file"), Tool("append_file")]);

        Assert.Equal(["append_file", "copy_file"], withheld);
    }

    [Fact]
    public void Allow_list_matching_ignores_case()
    {
        Assert.Empty(McpServerVerifier.VerifyTools(Server(include: ["READ_TEXT_FILE"]), [Tool("read_text_file")]));
    }

    [Fact]
    public void A_missing_allow_listed_tool_rejects_the_server()
    {
        var ex = Assert.Throws<McpServerRejectedException>(() =>
            McpServerVerifier.VerifyTools(Server(include: ["read_text_file", "list_directory"]), [Tool("read_text_file")]));

        Assert.Equal("fs", ex.Server);
        Assert.Contains("list_directory", ex.Message);
        Assert.Contains("rejected", ex.Message);
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(null, true)]
    [InlineData(false, true)]
    public void An_allow_listed_tool_that_declares_itself_writing_or_destructive_rejects_the_server(bool? readOnly, bool? destructive)
    {
        var ex = Assert.Throws<McpServerRejectedException>(() =>
            McpServerVerifier.VerifyTools(Server(include: ["read_text_file"]), [Tool("read_text_file", readOnly, destructive)]));

        Assert.Contains("read_text_file", ex.Message);
        Assert.Contains("writing or destructive", ex.Message);
    }

    [Fact]
    public void Tools_that_declare_themselves_read_only_or_say_nothing_pass()
    {
        Assert.Empty(McpServerVerifier.VerifyTools(
            Server(include: ["a", "b"]), [Tool("a", readOnly: true, destructive: false), Tool("b")]));
    }

    [Fact]
    public void A_write_tool_that_is_not_allow_listed_does_not_reject_the_server_it_is_just_withheld()
    {
        var withheld = McpServerVerifier.VerifyTools(
            Server(include: ["read_text_file"]), [Tool("read_text_file", readOnly: true), Tool("write_file", readOnly: false)]);

        Assert.Equal(["write_file"], withheld);
    }

    [Fact]
    public void Null_arguments_are_rejected_by_tool_verification()
    {
        Assert.Throws<ArgumentNullException>(() => McpServerVerifier.VerifyTools(null!, []));
        Assert.Throws<ArgumentNullException>(() => McpServerVerifier.VerifyTools(Server(), null!));
    }

    // ---- configuration

    [Fact]
    public void Configuration_without_a_path_root_needs_nothing()
    {
        McpServerVerifier.VerifyConfiguration(Server(), a => a);
    }

    [Fact]
    public void The_path_root_must_be_one_of_the_launch_arguments()
    {
        var root = Path.Combine(Path.GetTempPath(), "lka-root");

        McpServerVerifier.VerifyConfiguration(Server(args: ["script.js", root], pathRoot: root), a => a);

        var ex = Assert.Throws<McpServerRejectedException>(() =>
            McpServerVerifier.VerifyConfiguration(Server(args: ["script.js", root + "-other"], pathRoot: root), a => a));
        Assert.Contains("PathRoot", ex.Message);
    }

    [Fact]
    public void Root_and_argument_are_compared_after_token_expansion_and_normalisation()
    {
        var root = Path.Combine(Path.GetTempPath(), "lka-root");
        var server = Server(args: ["{Base}lka-root"], pathRoot: root);

        McpServerVerifier.VerifyConfiguration(server, a => a.Replace("{Base}", Path.GetTempPath()));
    }

    [Fact]
    public void An_argument_that_cannot_be_a_path_is_simply_not_the_root()
    {
        var root = Path.Combine(Path.GetTempPath(), "lka-root");

        McpServerVerifier.VerifyConfiguration(Server(args: ["bad\0arg", root], pathRoot: root), a => a);
        Assert.Throws<McpServerRejectedException>(() =>
            McpServerVerifier.VerifyConfiguration(Server(args: ["bad\0arg"], pathRoot: root), a => a));
    }

    [Fact]
    public void Null_arguments_are_rejected_by_configuration_verification()
    {
        Assert.Throws<ArgumentNullException>(() => McpServerVerifier.VerifyConfiguration(null!, a => a));
        Assert.Throws<ArgumentNullException>(() => McpServerVerifier.VerifyConfiguration(Server(), null!));
    }
}
