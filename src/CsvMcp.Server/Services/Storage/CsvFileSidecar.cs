using CsvMcp.Server.Domain;

namespace CsvMcp.Server.Services.Storage;

/// <summary>
/// On-disk representation of the metadata attached to a committed CSV file (stored next to it as <c>{fileId}.meta.json</c>).
/// The sidecar avoids scanning or counting file content to answer metadata queries and remembers
/// the delimiter used at creation time so append operations escape values consistently.
/// </summary>
public sealed class CsvFileSidecar
{
    /// <summary>Unique identifier of the file.</summary>
    public string FileId { get; set; } = string.Empty;

    /// <summary>Sanitized, human-friendly file name.</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>Delimiter character used when the file was created (e.g. <c>,</c> or <c>;</c>).</summary>
    public string Delimiter { get; set; } = ",";

    /// <summary>Total number of columns (including the header column).</summary>
    public int ColumnCount { get; set; }

    /// <summary>Number of data rows (excluding the header row).</summary>
    public long RowCount { get; set; }

    /// <summary>Size of the committed CSV file in bytes.</summary>
    public long SizeBytes { get; set; }

    /// <summary>UTC timestamp of file creation.</summary>
    public DateTimeOffset CreatedAtUtc { get; set; }

    /// <summary>UTC timestamp of the last modification.</summary>
    public DateTimeOffset UpdatedAtUtc { get; set; }

    /// <summary>Converts this sidecar into the public metadata record.</summary>
    public CsvMetadata ToMetadata() => new(
        FileId,
        FileName,
        ColumnCount,
        RowCount,
        SizeBytes,
        CreatedAtUtc,
        UpdatedAtUtc);

    /// <summary>Creates a sidecar from a <see cref="CsvMetadata"/> record plus the delimiter used.</summary>
    public static CsvFileSidecar FromMetadata(CsvMetadata metadata, string delimiter) => new()
    {
        FileId = metadata.FileId,
        FileName = metadata.FileName,
        Delimiter = delimiter,
        ColumnCount = metadata.ColumnCount,
        RowCount = metadata.RowCount,
        SizeBytes = metadata.SizeBytes,
        CreatedAtUtc = metadata.CreatedAtUtc,
        UpdatedAtUtc = metadata.UpdatedAtUtc,
    };
}
