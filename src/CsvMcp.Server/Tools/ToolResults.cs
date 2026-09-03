using System.Text.Json;

namespace CsvMcp.Server.Tools;

/// <summary>
/// Formats the JSON text envelope returned by every tool:
/// success is <c>{"ok": true, "tool": "...", "data": {...}}</c>, failures are
/// <c>{"ok": false, "tool": "...", "error": "..."}</c>.
/// Returning text content keeps results readable for every MCP client.
/// </summary>
internal static class ToolResults
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    public static string Ok(string tool, object? data) =>
        JsonSerializer.Serialize(new { ok = true, tool, data }, SerializerOptions);

    public static string Error(string tool, string message) =>
        JsonSerializer.Serialize(new { ok = false, tool, error = message }, SerializerOptions);
}
