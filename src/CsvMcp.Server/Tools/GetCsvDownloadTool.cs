using System.ComponentModel;
using CsvMcp.Server.Configuration;
using CsvMcp.Server.Domain;
using CsvMcp.Server.Services.Storage;
using CsvMcp.Server.Web;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace CsvMcp.Server.Tools;

/// <summary>
/// Tool that produces download information for a CSV file: a direct download URL served by the server,
/// plus optional inline base64 content (and a ready-made <c>data:</c> URI) for small files.
/// This is the tool that lets the agent hand the user an actual downloadable <c>.csv</c>.
/// </summary>
public sealed class GetCsvDownloadTool
{
    private readonly ICsvFileRepository _repository;
    private readonly DownloadUrlBuilder _urlBuilder;
    private readonly CsvStorageOptions _options;

    /// <summary>Initializes a new instance of the <see cref="GetCsvDownloadTool"/> class.</summary>
    public GetCsvDownloadTool(
        ICsvFileRepository repository,
        DownloadUrlBuilder urlBuilder,
        IOptions<CsvStorageOptions> options)
    {
        _repository = repository;
        _urlBuilder = urlBuilder;
        _options = options.Value;
    }

    /// <summary>
    /// Returns the ready-to-use download URL of a CSV file. When <c>includeBase64</c> is enabled and the file is
    /// small enough, the result additionally embeds the file as base64 plus a <c>data:text/csv;base64,...</c> URI,
    /// which works even when the user has no direct network access to this server.
    /// </summary>
    [McpServerTool(Name = "get_csv_download", Title = "Get CSV download")]
    [Description("Returns the download URL for a CSV file so the user can download it directly. Optionally embeds the file content as base64 and as a data: URI when includeBase64=true (files up to 5 MB).")]
    public async Task<string> GetCsvDownloadAsync(
        [Description("File ID of the CSV file to download.")]
        string fileId,
        [Description("When true and the file is at most 5 MB, the result also embeds the raw file as base64 plus a ready-made data:text/csv;base64,... URI.")]
        bool includeBase64 = false,
        CancellationToken cancellationToken = default)
    {
        const string tool = "get_csv_download";

        CsvMetadata? metadata = await _repository.GetMetadataAsync(fileId, cancellationToken);
        if (metadata is null)
        {
            return ToolResults.Error(tool, $"CSV file '{fileId}' was not found.");
        }

        var data = new Dictionary<string, object?>
        {
            ["fileId"] = metadata.FileId,
            ["fileName"] = metadata.FileName,
            ["sizeBytes"] = metadata.SizeBytes,
            ["rowCount"] = metadata.RowCount,
            ["columnCount"] = metadata.ColumnCount,
            ["downloadUrl"] = _urlBuilder.BuildUrl(metadata.FileId),
            ["inlineUrl"] = _urlBuilder.BuildUrl(metadata.FileId, inline: true),
        };

        if (includeBase64)
        {
            if (metadata.SizeBytes > _options.MaxBase64DownloadBytes)
            {
                return ToolResults.Error(
                    tool,
                    $"CSV file '{metadata.FileName}' is {metadata.SizeBytes} bytes; inline base64 is limited to {_options.MaxBase64DownloadBytes} bytes. Use the downloadUrl instead.");
            }

            byte[]? bytes = await _repository.ReadAllBytesAsync(fileId, cancellationToken);
            if (bytes is null)
            {
                return ToolResults.Error(tool, $"CSV file '{fileId}' could not be read.");
            }

            string base64 = Convert.ToBase64String(bytes);
            data["base64"] = base64;
            data["dataUri"] = "data:text/csv;base64," + base64;
        }

        return ToolResults.Ok(tool, data);
    }
}
