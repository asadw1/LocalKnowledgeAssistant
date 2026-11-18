using Microsoft.SemanticKernel;

namespace Assistant.Core.Tools;

/// <summary>Registers tools (MCP-backed or native) onto a kernel and vets calls to them.</summary>
public interface IToolRegistry
{
    Task RegisterAsync(Kernel kernel, CancellationToken ct = default);

    /// <summary>
    /// Last check before a validated call runs: whether its arguments are acceptable for the tool's server
    /// (for example, that a path stays inside the server's root).
    /// </summary>
    /// <param name="function">
    /// The function the kernel resolved the call to. Use its <see cref="KernelFunction.PluginName"/>, never the call's
    /// own plugin name: the Ollama connector sends names like <c>filesystem_read_text_file</c> that arrive with an empty
    /// plugin name and still resolve.
    /// </param>
    /// <param name="arguments">The schema-validated arguments the function will be invoked with.</param>
    /// <returns>Null when the call may run; otherwise an error written for the model that says how to recover.</returns>
    string? CheckCall(KernelFunction function, KernelArguments arguments);
}
