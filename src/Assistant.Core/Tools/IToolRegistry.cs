using Microsoft.SemanticKernel;

namespace Assistant.Core.Tools;

/// <summary>Registers tools (MCP-backed or native) onto a kernel. Add schema validation here.</summary>
public interface IToolRegistry
{
    Task RegisterAsync(Kernel kernel, CancellationToken ct = default);
}
