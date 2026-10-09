using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Microsoft.SemanticKernel;

namespace Assistant.Core.Tools;

/// <summary>Outcome of validating one model-requested tool call. Exactly one of <see cref="Function"/> or <see cref="Error"/> is set.</summary>
public sealed record ValidatedCall(KernelFunction? Function, KernelArguments? Arguments, string? Error)
{
    public bool IsValid => Error is null;
}

/// <summary>
/// Checks a tool call before it runs (TAD section 6): tool name, argument JSON, then arguments against the tool's schema.
/// Never throws for bad model output; failures come back as an error message the model can act on.
/// </summary>
public static class ToolCallValidator
{
    public static ValidatedCall Validate(Kernel kernel, FunctionCallContent call)
    {
        // 1. Name check
        if (!kernel.Plugins.TryGetFunction(call.PluginName, call.FunctionName, out var function))
        {
            var valid = string.Join(", ", ToolNames(kernel));
            return Fail($"Unknown tool '{Qualified(call)}'. Valid tools: {(valid.Length == 0 ? "(none)" : valid)}.");
        }

        // 2. Argument parse (the call builder records JSON parse failures on the content)
        if (call.Exception is not null)
            return Fail($"Arguments for '{Qualified(call)}' are not valid JSON: {call.Exception.Message}");

        // 3. Schema check
        var args = call.Arguments ?? [];
        var errors = CheckSchema(function.Metadata.Parameters, args);
        return errors.Count == 0
            ? new ValidatedCall(function, args, null)
            : Fail($"Invalid arguments for '{Qualified(call)}': {string.Join("; ", errors)}.");
    }

    public static IEnumerable<string> ToolNames(Kernel kernel) =>
        kernel.Plugins.SelectMany(p => p.Select(f => $"{p.Name}{FunctionNameSeparator}{f.Name}"));

    private const string FunctionNameSeparator = "_"; // how the Ollama connector names tools

    private static string Qualified(FunctionCallContent call) =>
        string.IsNullOrEmpty(call.PluginName) ? call.FunctionName : $"{call.PluginName}{FunctionNameSeparator}{call.FunctionName}";

    private static ValidatedCall Fail(string error) => new(null, null, error);

    private static List<string> CheckSchema(IReadOnlyList<KernelParameterMetadata> parameters, KernelArguments args)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var p in parameters)
        {
            properties[p.Name] = p.Schema is null ? new JsonObject() : JsonNode.Parse(p.Schema.RootElement.GetRawText());
            if (p.IsRequired) required.Add(p.Name);
        }

        var schemaNode = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = required,
            ["additionalProperties"] = false,
        };

        var instance = new JsonObject();
        foreach (var (name, value) in args)
            instance[name] = value is null ? null : JsonSerializer.SerializeToNode(value);

        var schema = JsonSchema.FromText(schemaNode.ToJsonString());
        var result = schema.Evaluate(instance, new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (result.IsValid) return [];

        return result.Details
            .Where(d => d.Errors is { Count: > 0 })
            .SelectMany(d => d.Errors!.Select(e => $"{(d.InstanceLocation.ToString() is { Length: > 0 } loc ? loc : "arguments")}: {e.Value}"))
            .Distinct()
            .ToList();
    }
}
