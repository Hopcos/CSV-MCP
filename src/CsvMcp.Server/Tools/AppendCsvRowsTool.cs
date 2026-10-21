using System.ComponentModel;
using CsvMcp.Server.Domain;
using CsvMcp.Server.Services.Storage;
using CsvMcp.Server.Web;
using ModelContextProtocol.Server;

namespace CsvMcp.Server.Tools;

/// <summary>
/// Tool that appends rows to an existing CSV file (the file's delimiter is remembered in its sidecar metadata,
/// so new rows are always escaped consistently).
/// </summary>
public sealed class AppendCsvRowsTool
{
    private readonly ICsvFileRepository _repository;
    private readonly DownloadUrlBuilder _urlBuilder;

    /// <summary>Initializes a new instance of the <see cref="AppendCsvRowsTool"/> class.</summary>
    public AppendCsvRowsTool(ICsvFileRepository repository, DownloadUrlBuilder urlBuilder)
    {
        _repository = repository;
        _urlBuilder = urlBuilder;
    }

    /// <summary>
    /// Appends rows to an existing CSV file. Every row must have exactly as many cells as the file's columns.
    /// Returns the updated file metadata, including the new row count and size.
    /// </summary>
    [McpServerTool(Name = "append_csv_rows", Title = "Append rows to CSV file")]
    [Description("Appends data rows to an existing CSV file (identified by fileId) and returns the updated metadata. Each appended row must have exactly as many cells as the file has columns. One call may add at most MaxAppendBytes of rendered content (default 1,000,000 bytes); for larger datasets split the rows across several append calls.")]
    public async Task<string> AppendCsvRowsAsync(
        [Description("File ID of the target CSV file (returned by create_csv or list_csv_files).")]
        string fileId,
        [Description("Data rows to append, one array of cell strings per row. Example: [[\"1978\", \"2025-11\", \"5151275\", \"0\", \"62\"]]. Cell values are given as strings.")]
        string[][] rows,
        CancellationToken cancellationToken = default)
    {
        const string tool = "append_csv_rows";

        try
        {
            CsvMetadata? updated = await _repository.AppendRowsAsync(fileId, rows, cancellationToken);

            if (updated is null)
            {
                return ToolResults.Error(tool, $"CSV file '{fileId}' was not found.");
            }

            return ToolResults.Ok(tool, new
            {
                fileId = updated.FileId,
                fileName = updated.FileName,
                rowCount = updated.RowCount,
                columnCount = updated.ColumnCount,
                sizeBytes = updated.SizeBytes,
                createdAtUtc = updated.CreatedAtUtc,
                updatedAtUtc = updated.UpdatedAtUtc,
                downloadUrl = _urlBuilder.BuildUrl(updated.FileId),
            });
        }
        catch (CsvValidationException ex)
        {
            return ToolResults.Error(tool, ex.Message);
        }
    }
}
