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
/// <item>Writing is batched: pre-rendered lines are accumulated into buffers of about 64 KB and flushed with a small
/// number of asynchronous <see cref="StreamWriter.WriteAsync(ReadOnlyMemory{char}, CancellationToken)"/> calls instead of
/// one await per line, which dramatically reduces per-line overhead for large payloads.</item>
/// </list>
/// </summary>
public sealed class CsvFormatter : ICsvFormatter
{
    private const int DefaultBufferSize = 64 * 1024;
    private const int MaxBatchChars = 64 * 1024;

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
    public string[] RenderRows(string[][] rows, string delimiter, CancellationToken cancellationToken)
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

    /// <inheritdoc/>
    public async Task WriteFileAsync(Stream destination, string[] headers, string[][] rows, string delimiter, CancellationToken cancellationToken)
    {
        string headerLine = RenderRow(headers, delimiter);
        string[] renderedRows = RenderRows(rows, delimiter, cancellationToken);

        var lines = new string[renderedRows.Length + 1];
        lines[0] = headerLine;
        Array.Copy(renderedRows, 0, lines, 1, renderedRows.Length);

        await WriteLinesAsync(destination, lines, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task WriteLinesAsync(Stream destination, IReadOnlyList<string> renderedLines, CancellationToken cancellationToken)
    {
        using var writer = new StreamWriter(
            destination,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            DefaultBufferSize,
            leaveOpen: true)
        {
            NewLine = "\r\n", // RFC 4180 requires CRLF line endings.
        };

        var batch = new StringBuilder(MaxBatchChars);

        foreach (string line in renderedLines)
        {
            if (batch.Length > 0 && batch.Length + line.Length + 2 > MaxBatchChars)
            {
                await writer.WriteAsync(batch.ToString().AsMemory(), cancellationToken).ConfigureAwait(false);
                batch.Clear();
            }

            batch.Append(line);
            batch.Append("\r\n");
        }

        if (batch.Length > 0)
        {
            await writer.WriteAsync(batch.ToString().AsMemory(), cancellationToken).ConfigureAwait(false);
        }

        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
