using Assistant.Core.Tools;
using Microsoft.SemanticKernel;

namespace Assistant.Tests;

public class ToolCallValidatorTests
{
    private static Kernel KernelWith(params KernelFunction[] functions)
    {
        var kernel = new Kernel();
        if (functions.Length > 0) kernel.Plugins.AddFromFunctions("tools", functions);
        return kernel;
    }

    private static KernelFunction Echo() =>
        KernelFunctionFactory.CreateFromMethod((string text) => text, "echo", "Echoes text");

    [Fact]
    public void Unknown_tool_with_no_tools_registered_says_none()
    {
        var result = ToolCallValidator.Validate(KernelWith(), new FunctionCallContent("echo", "tools"));

        Assert.False(result.IsValid);
        Assert.Contains("Unknown tool 'tools_echo'", result.Error);
        Assert.Contains("(none)", result.Error);
    }

    [Fact]
    public void Unknown_tool_without_a_plugin_prefix_is_named_plainly_and_valid_tools_are_listed()
    {
        var result = ToolCallValidator.Validate(KernelWith(Echo()), new FunctionCallContent("nope"));

        Assert.Contains("Unknown tool 'nope'", result.Error);
        Assert.Contains("tools_echo", result.Error);
    }

    [Fact]
    public void Call_with_no_arguments_at_all_is_checked_against_the_required_parameters()
    {
        var result = ToolCallValidator.Validate(KernelWith(Echo()), new FunctionCallContent("echo", "tools", "id", arguments: null));

        Assert.False(result.IsValid);
        Assert.Contains("Invalid arguments for 'tools_echo'", result.Error);
    }

    [Fact]
    public void Call_with_no_arguments_is_valid_for_a_tool_that_takes_none()
    {
        var noArgs = KernelFunctionFactory.CreateFromMethod(() => "ok", "ping");

        var result = ToolCallValidator.Validate(KernelWith(noArgs), new FunctionCallContent("ping", "tools"));

        Assert.True(result.IsValid);
        Assert.Empty(result.Arguments!);
    }

    [Fact]
    public void Explicit_null_argument_is_checked_rather_than_crashing()
    {
        var call = new FunctionCallContent("echo", "tools", "id", new KernelArguments { ["text"] = null });

        var result = ToolCallValidator.Validate(KernelWith(Echo()), call);

        Assert.False(result.IsValid);
        Assert.Contains("text", result.Error);
    }

    [Fact]
    public void Parameter_without_a_declared_schema_accepts_any_value()
    {
        var loose = KernelFunctionFactory.CreateFromMethod(
            (string x) => x,
            new KernelFunctionFromMethodOptions { FunctionName = "loose", Parameters = [new KernelParameterMetadata("x") { IsRequired = true }] });
        var call = new FunctionCallContent("loose", "tools", "id", new KernelArguments { ["x"] = 42 });

        var result = ToolCallValidator.Validate(KernelWith(loose), call);

        Assert.True(result.IsValid, result.Error);
    }

    [Fact]
    public void Valid_call_returns_the_function_and_its_arguments()
    {
        var call = new FunctionCallContent("echo", "tools", "id", new KernelArguments { ["text"] = "hi" });

        var result = ToolCallValidator.Validate(KernelWith(Echo()), call);

        Assert.True(result.IsValid);
        Assert.Equal("echo", result.Function!.Name);
        Assert.Equal("hi", result.Arguments!["text"]);
    }
}
