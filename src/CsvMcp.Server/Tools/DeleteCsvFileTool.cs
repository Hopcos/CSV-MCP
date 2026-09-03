using System.ComponentModel;
using CsvMcp.Server.Domain;
using CsvMcp.Server.Services.Storage;
using ModelContextProtocol.Server;

namespace CsvMcp.Server.Tools;

/// <summary>
/// Tool that deletes a CSV file (and its metadata sidecar) from the server.
/// </summary>
public sealed class DeleteCsvFileTool
{
    private readonly ICsvFileRepository _repository;

    /// <summary>Initializes a new instance of the <see cref="DeleteCsvFileTool"/> class.</summary>
    public DeleteCsvFileTool(ICsvFileRepository repository)
    {
        _repository = repository;
    }

    /// <summary>
    /// Permanently deletes a CSV file identified by <c>fileId</c>. Returns the metadata of the deleted file,
    /// or an error when no such file exists. Deleting frees storage space on the server.
    /// </summary>
    [McpServerTool(Name = "delete_csv_file", Title = "Delete CSV file")]
    [Description("Permanently deletes a CSV file (and its metadata) from the server by fileId. Returns the metadata of the deleted file; fails with an error message when the file does not exist.")]
    public async Task<string> DeleteCsvFileAsync(
        [Description("File ID of the CSV file to delete.")]
        string fileId,
        CancellationToken cancellationToken = default)
    {
        const string tool = "delete_csv_file";

        CsvMetadata? deleted = await _repository.DeleteAsync(fileId, cancellationToken);

        if (deleted is null)
        {
            return ToolResults.Error(tool, $"CSV file '{fileId}' was not found.");
        }

        return ToolResults.Ok(tool, new
        {
            deletedFileId = deleted.FileId,
            deletedFileName = deleted.FileName,
            deletedRowCount = deleted.RowCount,
            deletedSizeBytes = deleted.SizeBytes,
        });
    }
}
