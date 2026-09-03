namespace CsvMcp.Server.Domain;

/// <summary>
/// Metadata describing a committed CSV file. This is the public summary object returned by tools.
/// </summary>
/// <param name="FileId">Unique stable identifier of the file (used by every tool and the download endpoint).</param>
/// <param name="FileName">Sanitized, human-friendly file name (may contain non-ASCII characters).</param>
/// <param name="ColumnCount">Total number of columns (including the header column).</param>
/// <param name="RowCount">Number of data rows (excluding the header row).</param>
/// <param name="SizeBytes">Size of the committed CSV file in bytes.</param>
/// <param name="CreatedAtUtc">UTC timestamp of file creation.</param>
/// <param name="UpdatedAtUtc">UTC timestamp of the last modification.</param>
public sealed record CsvMetadata(
    string FileId,
    string FileName,
    int ColumnCount,
    long RowCount,
    long SizeBytes,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);
