using System.Reflection;

namespace Assistant.Core.Agent;

/// <summary>Versioned system prompts shipped as embedded resources under <c>Prompts/</c>.</summary>
public static class SystemPrompts
{
    public const string CurrentVersion = "v1";

    public static string Default { get; } = Load(CurrentVersion);

    public static string Load(string version)
    {
        var assembly = typeof(SystemPrompts).Assembly;
        var name = $"{assembly.GetName().Name}.Prompts.system.{version}.md";
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"System prompt '{version}' not found ({name}).");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Trim();
    }
}
