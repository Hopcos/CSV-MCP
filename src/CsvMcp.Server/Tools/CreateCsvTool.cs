using System.ComponentModel;
using CsvMcp.Server.Domain;
using CsvMcp.Server.Services.Csv;
using CsvMcp.Server.Services.Storage;
using CsvMcp.Server.Web;
using ModelContextProtocol.Server;

namespace CsvMcp.Server.Tools;

/// <summary>
/// Tool that creates a new CSV file from column headers and row data.
/// </summary>
public sealed class CreateCsvTool
{
    private readonly ICsvFileRepository _repository;
    private readonly ICsvFormatter _formatter;
    private readonly DownloadUrlBuilder _urlBuilder;

    /// <summary>Initializes a new instance of the <see cref="CreateCsvTool"/> class.</summary>
    public CreateCsvTool(ICsvFileRepository repository, ICsvFormatter formatter, DownloadUrlBuilder urlBuilder)
    {
        _repository = repository;
        _formatter = formatter;
        _urlBuilder = urlBuilder;
    }

    /// <summary>
    /// Creates a new CSV file from column headers and rows, stores it on the server and returns its metadata plus a download URL.
    /// This is the primary tool for persisting tabular data gathered by the agent (for example query results) as a CSV file
    /// the user can download directly. When <c>fileName</c> collides with an existing file, a unique suffix is added
    /// automatically and the actual name is reported in the result, so existing files are never overwritten.
    /// </summary>
    [McpServerTool(Name = "create_csv", Title = "Create CSV file")]
    [Description("Creates a new CSV file from column headers and rows, stores it on the server and returns its metadata plus a download URL. Rows are validated so every row must have exactly as many cells as headers.")]
    public async Task<string> CreateCsvAsync(
        [Description("Column headers of the CSV file. Example: [\"domain_id\", \"year_month\", \"user_id\"].")]
        string[] headers,
        [Description("Data rows, one array of cell strings per row. Example: [[\"1978\", \"2025-11\", \"3380506\"], [\"1978\", \"2025-11\", \"5151275\"]].")]
        string[][] rows,
        [Description("Optional suggested file name; when omitted a name like export-20260601-093000.csv is generated. Invalid characters are stripped and a .csv extension is guaranteed.")]
        string? fileName = null,
        [Description("Delimiter token: \"comma\" (default), \"semicolon\", \"tab\" or \"pipe\".")]
        string delimiter = "comma",
        CancellationToken cancellationToken = default)
    {
        const string tool = "create_csv";

        try
        {
            string normalizedDelimiter = _formatter.NormalizeDelimiter(delimiter);
            string baseName = CsvFileNamePolicy.Sanitize(fileName);
            string uniqueName = await CsvFileNamePolicy.ResolveUniqueNameAsync(
                baseName, _repository.NameExistsAsync, cancellationToken);

            CsvMetadata metadata = await _repository.CreateAsync(
                uniqueName, headers, rows, normalizedDelimiter, cancellationToken);

            return ToolResults.Ok(tool, new
            {
                fileId = metadata.FileId,
                fileName = metadata.FileName,
                rowCount = metadata.RowCount,
                columnCount = metadata.ColumnCount,
                sizeBytes = metadata.SizeBytes,
                createdAtUtc = metadata.CreatedAtUtc,
                updatedAtUtc = metadata.UpdatedAtUtc,
                downloadUrl = _urlBuilder.BuildUrl(metadata.FileId),
            });
        }
        catch (CsvValidationException ex)
        {
            return ToolResults.Error(tool, ex.Message);
        }
    }
}
