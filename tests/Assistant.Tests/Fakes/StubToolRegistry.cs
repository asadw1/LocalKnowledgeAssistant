using Assistant.Core.Tools;
using Microsoft.SemanticKernel;

namespace Assistant.Tests.Fakes;

/// <summary>Registers one native plugin "tools" with <c>echo(text)</c>, <c>boom()</c> and <c>big()</c>.</summary>
public sealed class StubToolRegistry : IToolRegistry
{
    public int EchoInvocations { get; private set; }

    public Task RegisterAsync(Kernel kernel, CancellationToken ct = default)
    {
        kernel.Plugins.AddFromFunctions("tools",
        [
            KernelFunctionFactory.CreateFromMethod(
                (string text) => { EchoInvocations++; return $"echo:{text}"; },
                "echo", "Echoes text"),
            KernelFunctionFactory.CreateFromMethod(
                new Func<string>(() => throw new InvalidOperationException("kaboom")), "boom", "Always fails"),
            KernelFunctionFactory.CreateFromMethod(() => new string('x', 500), "big", "Large output"),
            KernelFunctionFactory.CreateFromMethod(
                () => """{"content":[{"type":"text","text":"ENOENT: nope"}],"isError":true}""", "mcp_fail", "MCP-style error envelope"),
        ]);
        return Task.CompletedTask;
    }
}
