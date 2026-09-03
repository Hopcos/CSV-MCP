using System.ComponentModel;
using CsvMcp.Server.Domain;
using CsvMcp.Server.Services.Storage;
using CsvMcp.Server.Web;
using ModelContextProtocol.Server;

namespace CsvMcp.Server.Tools;

/// <summary>
/// Tool that lists all committed CSV files with their metadata.
/// </summary>
public sealed class ListCsvFilesTool
{
    private readonly ICsvFileRepository _repository;
    private readonly DownloadUrlBuilder _urlBuilder;

    /// <summary>Initializes a new instance of the <see cref="ListCsvFilesTool"/> class.</summary>
    public ListCsvFilesTool(ICsvFileRepository repository, DownloadUrlBuilder urlBuilder)
    {
        _repository = repository;
        _urlBuilder = urlBuilder;
    }

    /// <summary>
    /// Lists the CSV files stored on the server (newest first), optionally filtered by file-name substring and paginated.
    /// Every item includes its metadata and a ready-to-use download URL.
    /// </summary>
    [McpServerTool(Name = "list_csv_files", Title = "List CSV files")]
    [Description("Lists all CSV files stored on the server (newest first). Supports an optional file-name substring filter and offset/limit pagination; every item includes metadata plus a download URL.")]
    public async Task<string> ListCsvFilesAsync(
        [Description("Optional file-name substring filter, case-insensitive. Example: \"spins\" matches \"member-spins.csv\".")]
        string? namePattern = null,
        [Description("Number of files to skip (0-based pagination offset). Defaults to 0.")]
        int? offset = null,
        [Description("Maximum number of files to return (capped at 1000). Defaults to all matching files.")]
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        const string tool = "list_csv_files";

        IReadOnlyList<CsvMetadata> files = await _repository.ListAsync(namePattern, offset, limit, cancellationToken);

        var items = files.Select(f => new
        {
            fileId = f.FileId,
            fileName = f.FileName,
            rowCount = f.RowCount,
            columnCount = f.ColumnCount,
            sizeBytes = f.SizeBytes,
            createdAtUtc = f.CreatedAtUtc,
            updatedAtUtc = f.UpdatedAtUtc,
            downloadUrl = _urlBuilder.BuildUrl(f.FileId),
        });

        return ToolResults.Ok(tool, new
        {
            returned = files.Count,
            files = items,
        });
    }
}
