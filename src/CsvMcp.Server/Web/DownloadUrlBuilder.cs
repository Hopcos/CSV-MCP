using CsvMcp.Server.Configuration;
using Microsoft.Extensions.Options;

namespace CsvMcp.Server.Web;

/// <summary>
/// Builds absolute download URLs for committed CSV files.
/// The base URL is taken from the current HTTP request when available (so links are correct behind proxies),
/// falls back to the configured <see cref="CsvStorageOptions.PublicBaseUrl"/>, and finally to a relative URL.
/// </summary>
public sealed class DownloadUrlBuilder
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly CsvStorageOptions _options;

    /// <summary>Initializes a new instance of the <see cref="DownloadUrlBuilder"/> class.</summary>
    public DownloadUrlBuilder(IHttpContextAccessor httpContextAccessor, IOptions<CsvStorageOptions> options)
    {
        _httpContextAccessor = httpContextAccessor;
        _options = options.Value;
    }

    /// <summary>
    /// Returns the URL that serves the given file, e.g. <c>http://localhost:5080/download/9f2c...f8</c>.
    /// </summary>
    /// <param name="fileId">The committed file's ID.</param>
    /// <param name="inline">When <c>true</c>, appends <c>?inline=true</c> so the file streams inline instead of as an attachment.</param>
    public string BuildUrl(string fileId, bool inline = false)
    {
        string query = inline ? "?inline=true" : string.Empty;

        if (!string.IsNullOrWhiteSpace(_options.PublicBaseUrl))
        {
            return $"{_options.PublicBaseUrl.TrimEnd('/')}/download/{fileId}{query}";
        }

        HttpContext? context = _httpContextAccessor.HttpContext;
        if (context is not null)
        {
            return $"{context.Request.Scheme}://{context.Request.Host}/download/{fileId}{query}";
        }

        return $"/download/{fileId}{query}";
    }
}
