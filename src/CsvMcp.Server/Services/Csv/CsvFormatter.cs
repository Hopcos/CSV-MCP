using System.Text;
using CsvMcp.Server.Domain;

namespace CsvMcp.Server.Services.Csv;

/// <summary>
/// Default <see cref="ICsvFormatter"/> implementation.
///
/// Performance notes:
/// <list type="bullet">
/// <item>Row rendering is CPU-bound and embarrassingly parallel, so rows are grouped into chunks that are rendered
/// concurrently with <see cref="Parallel.For(int, int, ParallelOptions, Action{int})"/> using bounded parallelism
/// (<c>Environment.ProcessorCount</c>). Rendered lines are kept in a pre-sized array so the original row order is preserved.</item>
/// <item>Writing is fully asynchronous: buffered <see cref="StreamWriter"/> with <see cref="StreamWriter.WriteLineAsync(ReadOnlyMemory{char}, CancellationToken)"/>.</item>
/// </list>
/// </summary>
public sealed class CsvFormatter : ICsvFormatter
{
    private const int DefaultBufferSize = 64 * 1024;

    /// <inheritdoc/>
    public string NormalizeDelimiter(string? token) => CsvDelimiters.Normalize(token);

    /// <inheritdoc/>
    public string EscapeCell(string? value, string delimiter)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (value.IndexOfAny([delimiter[0], '"', '\r', '\n']) < 0)
        {
            return value;
        }

        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    /// <inheritdoc/>
    public string RenderRow(IReadOnlyList<string> cells, string delimiter)
    {
        var builder = new StringBuilder();
        for (int i = 0; i < cells.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(delimiter);
            }

            builder.Append(EscapeCell(cells[i], delimiter));
        }

        return builder.ToString();
    }

    /// <inheritdoc/>
    public Task WriteFileAsync(Stream destination, string[] headers, string[][] rows, string delimiter, CancellationToken cancellationToken)
    {
        string headerLine = RenderRow(headers, delimiter);
        string[] renderedRows = RenderRowsInParallel(rows, delimiter, cancellationToken);

        return WriteRenderedLinesAsync(destination, headerLine, renderedRows, cancellationToken);
    }

    /// <summary>
    /// Renders all data rows into a pre-sized string array. Each row is independent, so chunks are processed
    /// concurrently on the thread pool; array positions are written by exactly one worker, preserving row order.
    /// </summary>
    private string[] RenderRowsInParallel(string[][] rows, string delimiter, CancellationToken cancellationToken)
    {
        if (rows.Length == 0)
        {
            return [];
        }

        var rendered = new string[rows.Length];

        int chunkCount = Math.Max(1, Math.Min(rows.Length, Environment.ProcessorCount * 4));
        int chunkSize = (int)Math.Ceiling(rows.Length / (double)chunkCount);

        Parallel.For(
            0,
            chunkCount,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Environment.ProcessorCount,
                CancellationToken = cancellationToken,
            },
            chunkIndex =>
            {
                int start = chunkIndex * chunkSize;
                int end = Math.Min(start + chunkSize, rows.Length);

                for (int i = start; i < end; i++)
                {
                    rendered[i] = RenderRow(rows[i], delimiter);
                }
            });

        return rendered;
    }

    private static async Task WriteRenderedLinesAsync(
        Stream destination,
        string headerLine,
        string[] renderedRows,
        CancellationToken cancellationToken)
    {
        var writer = new StreamWriter(
            destination,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            DefaultBufferSize,
            leaveOpen: true);

        writer.NewLine = "\r\n"; // RFC 4180 requires CRLF line endings.

        await writer.WriteLineAsync(headerLine.AsMemory(), cancellationToken).ConfigureAwait(false);

        foreach (string line in renderedRows)
        {
            await writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
        }

        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
