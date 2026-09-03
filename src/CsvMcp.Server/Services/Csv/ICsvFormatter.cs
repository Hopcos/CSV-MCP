namespace CsvMcp.Server.Services.Csv;

/// <summary>
/// Renders CSV rows according to RFC 4180 escaping rules and provides the file-writing pipeline.
/// </summary>
public interface ICsvFormatter
{
    /// <summary>Normalizes the delimiter token and returns the actual delimiter character.</summary>
    string NormalizeDelimiter(string? token);

    /// <summary>Escapes a single cell value when required by RFC 4180 (quotes, delimiters, CR/LF).</summary>
    string EscapeCell(string? value, string delimiter);

    /// <summary>Renders one row (including escaping) into a single CSV line.</summary>
    string RenderRow(IReadOnlyList<string> cells, string delimiter);

    /// <summary>
    /// Writes the header row followed by every data row into <paramref name="destination"/> using UTF-8 (no BOM) and CRLF line endings.
    /// Data rows are rendered in parallel across worker threads and written sequentially to preserve row order.
    /// </summary>
    Task WriteFileAsync(Stream destination, string[] headers, string[][] rows, string delimiter, CancellationToken cancellationToken);
}
