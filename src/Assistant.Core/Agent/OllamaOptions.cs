using System.ComponentModel.DataAnnotations;

namespace Assistant.Core.Agent;

public sealed class OllamaOptions
{
    public const string SectionName = "Ollama";

    [Required, Url]
    public string BaseUrl { get; init; } = "http://localhost:11434";

    [Required]
    public string Model { get; init; } = "qwen2.5:7b-instruct";

    [Required]
    public string SystemPrompt { get; init; } = "You are a helpful local assistant. Answer concisely.";
}
