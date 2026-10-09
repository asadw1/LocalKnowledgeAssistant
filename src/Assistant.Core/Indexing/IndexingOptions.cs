using System.ComponentModel.DataAnnotations;

namespace Assistant.Core.Indexing;

public sealed class IndexingOptions
{
    public const string SectionName = "Indexing";

    public bool Enabled { get; init; } = true;

    /// <summary>Directory to scan. Keep it equal to the filesystem MCP server's allow-listed root.</summary>
    [Required]
    public string Root { get; init; } = "";

    [Required]
    public string DatabasePath { get; init; } = "";

    /// <summary>Minutes between rescans after the first; 0 scans once at startup.</summary>
    [Range(0, 1440)]
    public int IntervalMinutes { get; init; } = 5;
}
