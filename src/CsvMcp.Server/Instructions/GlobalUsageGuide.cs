using System.Text;

namespace CsvMcp.Server.Instructions;

/// <summary>
/// Builds the global usage guide that is served to MCP clients as the server <c>instructions</c>.
/// The guide mixes hand-written guidance (workflow, conventions, download endpoint, configuration)
/// with tool sections generated from the <see cref="ToolCatalog"/> so it always reflects the actual tool set.
/// </summary>
public static class GlobalUsageGuide
{
    private static readonly IReadOnlyDictionary<string, string> ToolExamples = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["create_csv"] = """
            {
              "method": "tools/call",
              "params": {
                "name": "create_csv",
                "arguments": {
                  "fileName": "member-spins.csv",
                  "headers": ["domain_id", "year_month", "user_id", "spins_in_month", "spins_remaining"],
                  "rows": [
                    ["1978", "2025-11", "3380506", "0", "8"],
                    ["1978", "2025-11", "3389568", "0", "2"],
                    ["1978", "2025-11", "4192031", "0", "25"],
                    ["1978", "2025-11", "5151275", "0", "62"]
                  ],
                  "delimiter": "comma"
                }
              }
            }
            """,
        ["append_csv_rows"] = """
            {
              "method": "tools/call",
              "params": {
                "name": "append_csv_rows",
                "arguments": {
                  "fileId": "9f2c1a3b4d5e6f708192a3b4c5d6e7f8",
                  "rows": [["1978", "2025-11", "6012345", "1", "4"]]
                }
              }
            }
            """,
        ["list_csv_files"] = """
            {
              "method": "tools/call",
              "params": {
                "name": "list_csv_files",
                "arguments": { "namePattern": "spins", "limit": 20, "offset": 0 }
              }
            }
            """,
        ["get_csv_content"] = """
            {
              "method": "tools/call",
              "params": {
                "name": "get_csv_content",
                "arguments": { "fileId": "9f2c1a3b4d5e6f708192a3b4c5d6e7f8", "rowOffset": 0, "maxRows": 200, "includeHeader": true }
              }
            }
            """,
        ["get_csv_download"] = """
            {
              "method": "tools/call",
              "params": {
                "name": "get_csv_download",
                "arguments": { "fileId": "9f2c1a3b4d5e6f708192a3b4c5d6e7f8", "includeBase64": false }
              }
            }
            """,
        ["delete_csv_file"] = """
            {
              "method": "tools/call",
              "params": {
                "name": "delete_csv_file",
                "arguments": { "fileId": "9f2c1a3b4d5e6f708192a3b4c5d6e7f8" }
              }
            }
            """,
    };

    /// <summary>Builds the complete instructions markdown document.</summary>
    public static string Build(IReadOnlyList<ToolDescriptor> tools)
    {
        var markdown = new StringBuilder(16 * 1024);

        AppendPreamble(markdown);
        AppendTypicalWorkflow(markdown);
        AppendToolSections(markdown, tools);
        AppendDownloadEndpoint(markdown);
        AppendConventions(markdown);

        return markdown.ToString();
    }

    private static void AppendPreamble(StringBuilder markdown)
    {
        markdown.AppendLine("# csv-mcp - Global Usage Guide");
        markdown.AppendLine();
        markdown.AppendLine("csv-mcp is a Model Context Protocol (MCP) server that generates, stores, reads and downloads CSV files over the Streamable HTTP transport. It solves the common pain point where an agent has tabular data (for example query results) but no way to persist it as a CSV file and hand the user a download link.");
        markdown.AppendLine();
        markdown.AppendLine("## Capabilities");
        markdown.AppendLine();
        markdown.AppendLine("| Area | Details |");
        markdown.AppendLine("|---|---|");
        markdown.AppendLine("| Transport | Streamable HTTP (POST JSON-RPC to the MCP endpoint; the transport is stateless per request) |");
        markdown.AppendLine("| Endpoint | `/mcp` |");
        markdown.AppendLine("| Tools | Create, append, list, read, download and delete CSV files |");
        markdown.AppendLine("| Storage | Committed CSV files with JSON sidecar metadata; temp staging files that are swept automatically |");
        markdown.AppendLine();
        markdown.AppendLine("## Intended workflow");
        markdown.AppendLine();
        markdown.AppendLine("1. `create_csv` - turn headers + rows into a committed CSV file; the result contains a `downloadUrl`. ");
        markdown.AppendLine("2. `get_csv_download` (optional) - obtain the signed-less download URL, or inline base64 content for small files when the agent itself needs to attach the file.");
        markdown.AppendLine("3. `get_csv_content` / `list_csv_files` - verify or inspect what is stored.");
        markdown.AppendLine("4. `append_csv_rows` - add more rows later.");
        markdown.AppendLine("5. `delete_csv_file` - clean up files that are no longer needed.");
        markdown.AppendLine();
    }

    private static void AppendTypicalWorkflow(StringBuilder markdown)
    {
        markdown.AppendLine("## Typical workflow example");
        markdown.AppendLine();
        markdown.AppendLine("The agent gathered the following rows and wants to hand them to the user as a downloadable CSV:");
        markdown.AppendLine();
        markdown.AppendLine("```text");
        markdown.AppendLine("domain_id,year_month,user_id,spins_in_month,spins_remaining");
        markdown.AppendLine("1978,2025-11,3380506,0,8");
        markdown.AppendLine("1978,2025-11,3389568,0,2");
        markdown.AppendLine("1978,2025-11,4192031,0,25");
        markdown.AppendLine("1978,2025-11,5151275,0,62");
        markdown.AppendLine("```");
        markdown.AppendLine();
        markdown.AppendLine("Step 1 - call `create_csv` (see its example below for the exact arguments). The result is JSON metadata plus a URL such as `http://localhost:5080/download/9f2c1a3b4d5e6f708192a3b4c5d6e7f8`.");
        markdown.AppendLine();
        markdown.AppendLine("Step 2 - present that URL to the user with wording such as *\"Your CSV is ready, click to download: <url>\"*. The URL triggers a browser/HTTP download with `Content-Disposition: attachment`.");
        markdown.AppendLine();
        markdown.AppendLine("If the hosting environment cannot reach the server from the user's side, call `get_csv_download` with `includeBase64: true` instead and build a data URI: `data:text/csv;base64,<value>`.");
        markdown.AppendLine();
    }

    private static void AppendToolSections(StringBuilder markdown, IReadOnlyList<ToolDescriptor> tools)
    {
        markdown.AppendLine("## Tools");
        markdown.AppendLine();
        markdown.AppendLine("Each tool returns a JSON text envelope: `{\"ok\": true, \"tool\": \"<name>\", \"data\": {...}}` on success, or `{\"ok\": false, \"tool\": \"<name>\", \"error\": \"<human-readable message>\"}` on validation failure.");

        foreach (ToolDescriptor tool in tools)
        {
            markdown.AppendLine();
            markdown.AppendLine($"### `{tool.Name}`");
            markdown.AppendLine();

            if (!string.IsNullOrWhiteSpace(tool.Title))
            {
                markdown.AppendLine($"**{tool.Title}**");
                markdown.AppendLine();
            }

            markdown.AppendLine(tool.Description ?? "No description provided.");
            markdown.AppendLine();
            markdown.AppendLine("**Parameters**");
            markdown.AppendLine();
            markdown.AppendLine("| Name | Type | Required | Description |");
            markdown.AppendLine("|---|---|---|---|");

            foreach (ToolParameterDescriptor parameter in tool.Parameters)
            {
                markdown.AppendLine(
                    $"| `{parameter.Name}` | `{parameter.TypeName}` | {(parameter.IsRequired ? "yes" : "no")} | {parameter.Description ?? "-"} |");
            }

            markdown.AppendLine();

            if (ToolExamples.TryGetValue(tool.Name, out string? example))
            {
                markdown.AppendLine("**Example call**");
                markdown.AppendLine();
                markdown.AppendLine("```json");
                markdown.AppendLine(example);
                markdown.AppendLine("```");
                markdown.AppendLine();
            }
        }
    }

    private static void AppendDownloadEndpoint(StringBuilder markdown)
    {
        markdown.AppendLine("## Download endpoint");
        markdown.AppendLine();
        markdown.AppendLine("Every committed file is served over plain HTTP (outside the MCP protocol) so browsers and scripts can fetch it directly:");
        markdown.AppendLine();
        markdown.AppendLine("| Method | Path | Behavior |");
        markdown.AppendLine("|---|---|---|");
        markdown.AppendLine("| GET | `/download/{fileId}` | Streams the file as `text/csv` with `Content-Disposition: attachment` and `enableRangeProcessing`. |");
        markdown.AppendLine("| GET | `/download/{fileId}?inline=true` | Streams the file inline (`Content-Disposition: inline`) so it renders in the browser. |");
        markdown.AppendLine();
        markdown.AppendLine("- `{fileId}` is a 32-character hexadecimal identifier returned by every tool that produces files. Unknown IDs return HTTP 404.");
        markdown.AppendLine("- The endpoint supports HTTP range requests, which matters for large files.");
        markdown.AppendLine("- File IDs never appear twice and are not guessable in practice, so the endpoint needs no additional authentication. If the server must be internet-facing, put it behind a reverse proxy / firewall.");
        markdown.AppendLine();
        markdown.AppendLine("## Configuration knobs the agent should know");
        markdown.AppendLine();
        markdown.AppendLine("- Files are stored under the data directory (default `data/csv`), git-ignored, one file per `fileId` plus a `.meta.json` sidecar.");
        markdown.AppendLine("- Limits: `MaxRowsPerFile` (default 1,000,000), `MaxColumns` (default 10,000), `MaxCellLength` (default 1,000,000 chars), `MaxFileSizeBytes` (default 50 MB), inline base64 downloads up to `MaxBase64DownloadBytes` (default 5 MB).");
        markdown.AppendLine("- `get_csv_content` caps `maxRows` at `MaxContentRowLimit` (default 100,000) and defaults to `DefaultContentRowLimit` (default 200) rows.");
        markdown.AppendLine("- `create_csv` returns a file with an auto-unique name when a collision occurs; the actual name is always reported in the result.");
        markdown.AppendLine();
        markdown.AppendLine("## CSV conventions");
        markdown.AppendLine();
        markdown.AppendLine("- Encoding: UTF-8 without BOM; line endings CRLF (RFC 4180); cells containing the delimiter, double quotes or CR/LF are double-quoted with interior quotes doubled.");
        markdown.AppendLine("- Delimiter tokens accepted by `create_csv`: `comma` (default), `semicolon`, `tab`, `pipe`.");
        markdown.AppendLine("- `RowCount` in metadata excludes the header row; `ColumnCount` includes it.");
        markdown.AppendLine();
    }

    private static void AppendConventions(StringBuilder markdown)
    {
        markdown.AppendLine("## Error handling");
        markdown.AppendLine();
        markdown.AppendLine("- Validation problems (bad columns, oversized datasets, unknown file IDs, unsupported delimiters) are returned as `{\"ok\": false, \"error\": \"...\"}` JSON text results - the agent should surface `error` to the user and fix the arguments.");
        markdown.AppendLine("- Unexpected server-side failures surface as MCP JSON-RPC error responses (code `-32603` or similar); retry once, then report the message.");
        markdown.AppendLine("- All timestamps are UTC ISO-8601 strings.");
        markdown.AppendLine();
    }
}
