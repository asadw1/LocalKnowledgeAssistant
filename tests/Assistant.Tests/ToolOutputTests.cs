using Assistant.Core.Tools;

namespace Assistant.Tests;

public class ToolOutputTests
{
    [Theory]
    [InlineData("plain text, not json")]
    [InlineData("[1,2,3]")]
    [InlineData("""{"other":1}""")]
    [InlineData("""{"content":"not an array"}""")]
    [InlineData("""{"content":[]}""")]
    [InlineData("""{"content":[{"type":"image","data":"abc"}]}""")]
    [InlineData("""{"content":[{"type":"text","text":5}]}""")]
    public void Output_that_is_not_an_mcp_text_envelope_is_returned_as_is(string raw)
    {
        Assert.Equal((raw, false), ToolOutput.Parse(raw));
    }

    [Fact]
    public void Text_items_are_joined_with_newlines()
    {
        var (text, isError) = ToolOutput.Parse("""{"content":[{"type":"text","text":"a"},{"type":"image"},{"type":"text","text":"b"}]}""");

        Assert.Equal("a\nb", text);
        Assert.False(isError);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("\"true\"", false)]
    public void Is_error_flag_is_honoured_only_when_it_is_a_json_true(string flag, bool expected)
    {
        var (text, isError) = ToolOutput.Parse($$"""{"content":[{"type":"text","text":"boom"}],"isError":{{flag}}}""");

        Assert.Equal("boom", text);
        Assert.Equal(expected, isError);
    }

    [Fact]
    public void Non_object_content_items_are_ignored_rather_than_crashing()
    {
        var raw = """{"content":[1,"two",null]}""";

        Assert.Equal((raw, false), ToolOutput.Parse(raw));
    }
}
