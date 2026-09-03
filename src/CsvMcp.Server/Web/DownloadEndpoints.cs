using CsvMcp.Server.Domain;
using CsvMcp.Server.Services.Storage;
using Microsoft.Net.Http.Headers;

namespace CsvMcp.Server.Web;

/// <summary>
/// Minimal-API handlers for serving committed CSV files over plain HTTP.
/// These endpoints are intentionally outside the MCP protocol so browsers, scripts
/// and download managers can fetch files with standard HTTP semantics (including range requests).
/// </summary>
public static class DownloadEndpoints
{
    /// <summary>
    /// Streams the CSV file identified by <c>fileId</c> with an appropriate <c>Content-Disposition</c> header.
    /// </summary>
    /// <param name="context">The current HTTP context (used to set headers before the file result executes).</param>
    /// <param name="fileId">Committed file ID.</param>
    /// <param name="inline">When <c>true</c>, serves the file inline instead of as a download attachment (optional, defaults to false).</param>
    /// <param name="repository">The CSV file repository.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    public static async Task<IResult> HandleAsync(
        HttpContext context,
        string fileId,
        bool inline = false,
        ICsvFileRepository repository = null!,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        CsvMetadata? metadata = await repository.GetMetadataAsync(fileId, cancellationToken);
        if (metadata is null)
        {
            return Results.NotFound(new { error = $"CSV file '{fileId}' was not found." });
        }

        string? path = await repository.GetDownloadPathAsync(fileId, cancellationToken);
        if (path is null)
        {
            return Results.NotFound(new { error = $"CSV file '{fileId}' was not found." });
        }

        // Set Content-Disposition manually: inline and attachment both carry the real file name,
        // encoded per RFC 5987 for non-ASCII names.
        var contentDisposition = new ContentDispositionHeaderValue(inline ? "inline" : "attachment");
        contentDisposition.SetHttpFileName(metadata.FileName);
        context.Response.Headers.ContentDisposition = contentDisposition.ToString();

        return Results.File(path, contentType: "text/csv", enableRangeProcessing: true);
    }
}
