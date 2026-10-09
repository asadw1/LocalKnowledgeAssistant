using Assistant.Core.Tools;

namespace Assistant.Tests;

public class McpArgumentTokenTests
{
    [Fact]
    public void BaseDirectory_token_expands_to_the_app_directory()
    {
        Assert.Equal(AppContext.BaseDirectory + "Assistant.SqliteMcp.dll", McpToolRegistry.ExpandTokens("{BaseDirectory}Assistant.SqliteMcp.dll"));
    }

    [Theory]
    [InlineData("../../sample-docs")]
    [InlineData("-y")]
    [InlineData("")]
    public void Arguments_without_tokens_are_unchanged(string arg) => Assert.Equal(arg, McpToolRegistry.ExpandTokens(arg));
}
