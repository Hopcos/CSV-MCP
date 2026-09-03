namespace CsvMcp.Server.Domain;

/// <summary>
/// Result of reading a slice of a CSV file's content.
/// </summary>
/// <param name="Metadata">Metadata of the file that was read.</param>
/// <param name="Content">The requested slice of the file content (one line per row, LF-separated, excluding the header unless requested).</param>
/// <param name="RowsReturned">Number of data rows included in <see cref="Content"/>.</param>
public sealed record CsvReadResult(CsvMetadata Metadata, string Content, int RowsReturned);
