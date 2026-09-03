namespace CsvMcp.Server.Configuration;

/// <summary>
/// Configuration options for CSV storage and tool limits.
/// Bound from the <c>CsvMcp</c> section of <c>appsettings.json</c> (also overridable via the <c>CsvMcp__*</c> environment variables).
/// </summary>
public sealed class CsvStorageOptions
{
    /// <summary>Name of the configuration section this options class is bound to.</summary>
    public const string SectionName = "CsvMcp";

    /// <summary>
    /// Root directory (relative to the content root) that holds committed CSV files and staging/temp files.
    /// This directory is git-ignored.
    /// </summary>
    public string RootDirectory { get; set; } = "data";

    /// <summary>Subdirectory of <see cref="RootDirectory"/> that stores committed CSV files.</summary>
    public string CsvSubdirectory { get; set; } = "csv";

    /// <summary>Subdirectory of <see cref="RootDirectory"/> that stores staging/temp files (e.g. during atomic writes).</summary>
    public string TempSubdirectory { get; set; } = "tmp";

    /// <summary>
    /// Optional public base URL used to build download links (e.g. <c>https://csv.example.com</c>).
    /// When empty, the scheme and host of the current HTTP request are used instead.
    /// </summary>
    public string? PublicBaseUrl { get; set; }

    /// <summary>Maximum number of data rows (excluding the header) allowed in one CSV file.</summary>
    public int MaxRowsPerFile { get; set; } = 1_000_000;

    /// <summary>Maximum number of columns (including the header) allowed in one CSV file.</summary>
    public int MaxColumns { get; set; } = 10_000;

    /// <summary>Maximum number of characters allowed in a single cell value.</summary>
    public int MaxCellLength { get; set; } = 1_000_000;

    /// <summary>Maximum allowed size of a committed CSV file in bytes.</summary>
    public long MaxFileSizeBytes { get; set; } = 50 * 1024 * 1024;

    /// <summary>Default number of rows returned by <c>get_csv_content</c> when <c>maxRows</c> is not provided.</summary>
    public int DefaultContentRowLimit { get; set; } = 200;

    /// <summary>Hard upper bound for the <c>maxRows</c> argument accepted by <c>get_csv_content</c>.</summary>
    public int MaxContentRowLimit { get; set; } = 100_000;

    /// <summary>
    /// Maximum file size in bytes for which <c>get_csv_download</c> may embed inline base64 content.
    /// Above this limit the tool returns the download URL only.
    /// </summary>
    public long MaxBase64DownloadBytes { get; set; } = 5 * 1024 * 1024;

    /// <summary>Age after which files in the temp directory are considered stale and removed by the cleanup service.</summary>
    public TimeSpan TempFileMaxAge { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Interval at which the temp-file cleanup background service runs.</summary>
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromMinutes(10);
}
