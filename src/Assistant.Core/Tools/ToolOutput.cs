using System.Text;
using System.Text.Json;

namespace Assistant.Core.Tools;

/// <summary>Normalises raw tool output. MCP tools return <c>{"content":[{"type":"text","text":...}],"isError":bool}</c>.</summary>
public static class ToolOutput
{
    public static (string Text, bool IsError) Parse(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("content", out var content) ||
                content.ValueKind != JsonValueKind.Array)
                return (raw, false);

            var text = new StringBuilder();
            foreach (var item in content.EnumerateArray())
            {
                if (item.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                {
                    if (text.Length > 0) text.Append('\n');
                    text.Append(t.GetString());
                }
            }

            var isError = doc.RootElement.TryGetProperty("isError", out var e) && e.ValueKind == JsonValueKind.True;
            return (text.Length > 0 ? text.ToString() : raw, isError);
        }
        catch (JsonException)
        {
            return (raw, false);
        }
    }
}
