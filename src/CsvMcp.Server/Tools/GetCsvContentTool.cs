using System.ComponentModel;
using CsvMcp.Server.Configuration;
using CsvMcp.Server.Domain;
using CsvMcp.Server.Services.Storage;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace CsvMcp.Server.Tools;

/// <summary>
/// Tool that reads back a slice of a CSV file's content so the agent (or the user) can verify what was stored.
/// </summary>
public sealed class GetCsvContentTool
{
    private readonly ICsvFileRepository _repository;
    private readonly CsvStorageOptions _options;

    /// <summary>Initializes a new instance of the <see cref="GetCsvContentTool"/> class.</summary>
    public GetCsvContentTool(ICsvFileRepository repository, IOptions<CsvStorageOptions> options)
    {
        _repository = repository;
        _options = options.Value;
    }

    /// <summary>
    /// Reads a slice of an existing CSV file's content (a window of data rows, optionally prefixed by the header row)
    /// and returns it as plain text together with file metadata. Use <c>rowOffset</c>/<c>maxRows</c> to page through large files.
    /// </summary>
    [McpServerTool(Name = "get_csv_content", Title = "Get CSV file content")]
    [Description("Reads a slice of a CSV file's content and returns it as text together with the file metadata. Use rowOffset and maxRows to page through large files; set includeHeader to true to prefix the header row.")]
    public async Task<string> GetCsvContentAsync(
        [Description("File ID of the CSV file to read.")]
        string fileId,
        [Description("0-based index of the first data row to return (defaults to 0).")]
        int rowOffset = 0,
        [Description("Maximum number of data rows to return (defaults to 200, hard cap is 100000).")]
        int? maxRows = null,
        [Description("Whether to include the header row at the top of the returned content (defaults to false).")]
        bool includeHeader = false,
        CancellationToken cancellationToken = default)
    {
        const string tool = "get_csv_content";

        int resolvedMaxRows = Math.Clamp(maxRows ?? _options.DefaultContentRowLimit, 1, _options.MaxContentRowLimit);

        try
        {
            CsvReadResult? result = await _repository.ReadContentAsync(fileId, rowOffset, resolvedMaxRows, includeHeader, cancellationToken);

            if (result is null)
            {
                return ToolResults.Error(tool, $"CSV file '{fileId}' was not found.");
            }

            long rowsSkippedBeyondWindow = Math.Max(0, result.Metadata.RowCount - (rowOffset + result.RowsReturned));

            return ToolResults.Ok(tool, new
            {
                fileId = result.Metadata.FileId,
                fileName = result.Metadata.FileName,
                rowOffset,
                rowsReturned = result.RowsReturned,
                hasMore = rowsSkippedBeyondWindow > 0,
                totalDataRows = result.Metadata.RowCount,
                content = result.Content,
            });
        }
        catch (CsvValidationException ex)
        {
            return ToolResults.Error(tool, ex.Message);
        }
    }
}
