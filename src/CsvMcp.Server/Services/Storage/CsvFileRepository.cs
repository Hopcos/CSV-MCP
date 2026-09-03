using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using CsvMcp.Server.Configuration;
using CsvMcp.Server.Domain;
using CsvMcp.Server.Services.Csv;
using Microsoft.Extensions.Options;

namespace CsvMcp.Server.Services.Storage;

/// <summary>
/// File-backed implementation of <see cref="ICsvFileRepository"/>.
///
/// Thread-safety model:
/// <list type="bullet">
/// <item>Each committed file has its own <see cref="SemaphoreSlim"/> (created on demand in a
/// <see cref="ConcurrentDictionary{TKey,TValue}"/>), so concurrent append/delete calls from parallel tool
/// invocations serialize per file while other files remain fully parallel.</item>
/// <item>Writes are staged in the temp directory and moved into the data directory (atomic rename on the same volume),
/// so a crashed write can never leave a half-written committed file. The temp staging files are swept by
/// <c>TempFileCleanupService</c>.</item>
/// <item>All I/O is asynchronous (<see cref="FileOptions.Asynchronous"/>, async JSON serialization, streaming reads).</item>
/// </list>
/// </summary>
public sealed class CsvFileRepository : ICsvFileRepository
{
    private const int DefaultIoBufferSize = 64 * 1024;

    private static readonly JsonSerializerOptions SidecarJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly ICsvStoragePaths _paths;
    private readonly ICsvFormatter _formatter;
    private readonly CsvStorageOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CsvFileRepository> _logger;

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks = new(StringComparer.Ordinal);

    /// <summary>Initializes a new instance of the <see cref="CsvFileRepository"/> class.</summary>
    public CsvFileRepository(
        ICsvStoragePaths paths,
        ICsvFormatter formatter,
        IOptions<CsvStorageOptions> options,
        TimeProvider timeProvider,
        ILogger<CsvFileRepository> logger)
    {
        _paths = paths;
        _formatter = formatter;
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;

        Directory.CreateDirectory(_paths.DataDirectory);
    }

    /// <inheritdoc/>
    public async Task<CsvMetadata> CreateAsync(
        string fileName,
        string[] headers,
        string[][] rows,
        string delimiter,
        CancellationToken cancellationToken)
    {
        ValidateDataset(headers, rows);

        string fileId = Guid.NewGuid().ToString("N");
        string committedPath = _paths.GetCommittedFilePath(fileId);
        string stagingPath = _paths.CreateTempStagingPath();

        try
        {
            // Stage the complete file in the temp directory first.
            await using (var stagingStream = new FileStream(
                stagingPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                DefaultIoBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await _formatter.WriteFileAsync(stagingStream, headers, rows, delimiter, cancellationToken).ConfigureAwait(false);
            }

            long sizeBytes = new FileInfo(stagingPath).Length;
            if (sizeBytes > _options.MaxFileSizeBytes)
            {
                throw new CsvValidationException(
                    $"The generated file would be {sizeBytes} bytes, exceeding the maximum allowed size of {_options.MaxFileSizeBytes} bytes.");
            }

            var now = _timeProvider.GetUtcNow();
            var metadata = new CsvMetadata(fileId, fileName, headers.Length, rows.Length, sizeBytes, now, now);

            // Atomic commit: rename moves the fully written staging file into place.
            File.Move(stagingPath, committedPath);
            await WriteSidecarAsync(CsvFileSidecar.FromMetadata(metadata, delimiter), cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "Created CSV file '{FileName}' (fileId '{FileId}', {RowCount} rows, {SizeBytes} bytes).",
                metadata.FileName,
                metadata.FileId,
                metadata.RowCount,
                metadata.SizeBytes);

            return metadata;
        }
        catch
        {
            TryDeleteFile(stagingPath);
            TryDeleteFile(committedPath);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<CsvMetadata?> AppendRowsAsync(string fileId, string[][] rows, CancellationToken cancellationToken)
    {
        string committedPath = _paths.GetCommittedFilePath(fileId);
        SemaphoreSlim gate = AcquireFileGate(fileId);

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(committedPath))
            {
                return null;
            }

            CsvFileSidecar? sidecar = await ReadSidecarOrRebuildAsync(fileId, committedPath, cancellationToken).ConfigureAwait(false);
            if (sidecar is null)
            {
                return null;
            }

            long newRowCount = sidecar.RowCount + rows.Length;
            if (newRowCount > _options.MaxRowsPerFile)
            {
                throw new CsvValidationException(
                    $"Appending {rows.Length} rows would bring the file to {newRowCount} rows, exceeding the maximum of {_options.MaxRowsPerFile} rows per file.");
            }

            ValidateRowShapes(sidecar.ColumnCount, rows);

            string delimiter = sidecar.Delimiter is { Length: 1 } single ? single : ",";

            // Render first so the projected size can be validated before the file is touched.
            string[] renderedLines = new string[rows.Length];
            long projectedBytes = sidecar.SizeBytes;
            for (int i = 0; i < rows.Length; i++)
            {
                renderedLines[i] = _formatter.RenderRow(rows[i], delimiter);
                projectedBytes += Encoding.UTF8.GetByteCount(renderedLines[i]) + 2; // + CRLF
            }

            if (projectedBytes > _options.MaxFileSizeBytes)
            {
                throw new CsvValidationException(
                    $"Appending {rows.Length} rows would grow the file to about {projectedBytes} bytes, exceeding the maximum allowed size of {_options.MaxFileSizeBytes} bytes.");
            }

            await using (var appendStream = new FileStream(
                committedPath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                DefaultIoBufferSize,
                FileOptions.Asynchronous))
            {
                var writer = new StreamWriter(
                    appendStream,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    DefaultIoBufferSize,
                    leaveOpen: true)
                {
                    NewLine = "\r\n", // RFC 4180
                };

                foreach (string line in renderedLines)
                {
                    await writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
                }

                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            long sizeBytes = new FileInfo(committedPath).Length;
            var now = _timeProvider.GetUtcNow();

            var updatedMetadata = new CsvMetadata(
                sidecar.FileId,
                sidecar.FileName,
                sidecar.ColumnCount,
                newRowCount,
                sizeBytes,
                sidecar.CreatedAtUtc,
                now);

            await WriteSidecarAsync(CsvFileSidecar.FromMetadata(updatedMetadata, delimiter), cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "Appended {Appended} rows to CSV file '{FileId}' (now {RowCount} rows).",
                rows.Length,
                fileId,
                updatedMetadata.RowCount);

            return updatedMetadata;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<CsvMetadata>> ListAsync(
        string? namePattern,
        int? offset,
        int? limit,
        CancellationToken cancellationToken)
    {
        var files = new List<CsvMetadata>();

        // 1) Files that have a sidecar (the fast path).
        foreach (string sidecarPath in Directory.EnumerateFiles(_paths.DataDirectory, "*.meta.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();

            string fileId = Path.GetFileName(sidecarPath)[..^".meta.json".Length];
            CsvFileSidecar? sidecar = await ReadSidecarAsync(fileId, cancellationToken).ConfigureAwait(false);

            if (sidecar is not null)
            {
                files.Add(sidecar.ToMetadata());
            }
        }

        // 2) Committed CSV files without a sidecar (e.g. dropped in by an operator) — reconstruct metadata on the fly.
        var sidecarIds = new HashSet<string>(files.Select(f => f.FileId), StringComparer.Ordinal);
        foreach (string csvPath in Directory.EnumerateFiles(_paths.DataDirectory, "*.csv"))
        {
            cancellationToken.ThrowIfCancellationRequested();

            string fileId = Path.GetFileNameWithoutExtension(csvPath);
            if (!sidecarIds.Contains(fileId) && _paths.IsValidFileId(fileId))
            {
                CsvFileSidecar? rebuilt = await RebuildSidecarFromCsvAsync(fileId, csvPath, cancellationToken).ConfigureAwait(false);
                if (rebuilt is not null)
                {
                    files.Add(rebuilt.ToMetadata());
                }
            }
        }

        // 3) Filter by file-name substring (case-insensitive) and paginate, newest first.
        IEnumerable<CsvMetadata> query = files
            .OrderByDescending(f => f.UpdatedAtUtc);

        if (!string.IsNullOrWhiteSpace(namePattern))
        {
            query = query.Where(f => f.FileName.Contains(namePattern, StringComparison.OrdinalIgnoreCase));
        }

        int skip = Math.Max(0, offset ?? 0);
        int take = limit is null
            ? int.MaxValue
            : Math.Clamp(limit.Value, 0, 1000);

        return query.Skip(skip).Take(take).ToList();
    }

    /// <inheritdoc/>
    public async Task<CsvMetadata?> GetMetadataAsync(string fileId, CancellationToken cancellationToken)
    {
        if (!_paths.IsValidFileId(fileId))
        {
            return null;
        }

        CsvFileSidecar? sidecar = await ReadSidecarAsync(fileId, cancellationToken).ConfigureAwait(false);
        if (sidecar is not null)
        {
            return sidecar.ToMetadata();
        }

        string committedPath = _paths.GetCommittedFilePath(fileId);
        return File.Exists(committedPath)
            ? (await RebuildSidecarFromCsvAsync(fileId, committedPath, cancellationToken).ConfigureAwait(false))?.ToMetadata()
            : null;
    }

    /// <inheritdoc/>
    public async Task<CsvReadResult?> ReadContentAsync(
        string fileId,
        int rowOffset,
        int maxRows,
        bool includeHeader,
        CancellationToken cancellationToken)
    {
        if (rowOffset < 0)
        {
            throw new CsvValidationException($"rowOffset must be >= 0 but was {rowOffset}.");
        }

        if (maxRows <= 0)
        {
            throw new CsvValidationException($"maxRows must be >= 1 but was {maxRows}.");
        }

        if (!_paths.IsValidFileId(fileId))
        {
            return null;
        }

        string committedPath = _paths.GetCommittedFilePath(fileId);
        if (!File.Exists(committedPath))
        {
            return null;
        }

        CsvMetadata? metadata = await GetMetadataAsync(fileId, cancellationToken).ConfigureAwait(false);
        if (metadata is null)
        {
            return null;
        }

        var lines = new List<string>(Math.Min(maxRows + 1, 4096));
        int rowsReturned = 0;

        await using (var stream = new FileStream(
            committedPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            DefaultIoBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        using (var reader = new StreamReader(stream, Encoding.UTF8))
        {
            if (includeHeader)
            {
                string? header = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (header is not null)
                {
                    lines.Add(header);
                }
            }

            int dataRowIndex = 0;
            while (rowsReturned < maxRows)
            {
                string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                if (dataRowIndex < rowOffset)
                {
                    dataRowIndex++;
                    continue;
                }

                lines.Add(line);
                dataRowIndex++;
                rowsReturned++;
            }
        }

        return new CsvReadResult(metadata, string.Join("\n", lines), rowsReturned);
    }

    /// <inheritdoc/>
    public Task<string?> GetDownloadPathAsync(string fileId, CancellationToken cancellationToken)
    {
        if (!_paths.IsValidFileId(fileId))
        {
            return Task.FromResult<string?>(null);
        }

        string committedPath = _paths.GetCommittedFilePath(fileId);
        return Task.FromResult<string?>(File.Exists(committedPath) ? committedPath : null);
    }

    /// <inheritdoc/>
    public async Task<byte[]?> ReadAllBytesAsync(string fileId, CancellationToken cancellationToken)
    {
        string? path = await GetDownloadPathAsync(fileId, cancellationToken).ConfigureAwait(false);
        if (path is null)
        {
            return null;
        }

        return await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<CsvMetadata?> DeleteAsync(string fileId, CancellationToken cancellationToken)
    {
        if (!_paths.IsValidFileId(fileId))
        {
            return null;
        }

        string committedPath = _paths.GetCommittedFilePath(fileId);
        SemaphoreSlim gate = AcquireFileGate(fileId);

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            bool csvExists = File.Exists(committedPath);
            bool sidecarExists = File.Exists(_paths.GetSidecarPath(fileId));

            if (!csvExists && !sidecarExists)
            {
                return null;
            }

            CsvFileSidecar? sidecar = await ReadSidecarAsync(fileId, cancellationToken).ConfigureAwait(false);
            if (sidecar is null && csvExists)
            {
                sidecar = await RebuildSidecarFromCsvAsync(fileId, committedPath, cancellationToken).ConfigureAwait(false);
            }

            TryDeleteFile(committedPath);
            TryDeleteFile(_paths.GetSidecarPath(fileId));

            CsvMetadata? deleted = sidecar?.ToMetadata();
            _logger.LogInformation("Deleted CSV file '{FileId}'.", fileId);

            return deleted;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<bool> NameExistsAsync(string fileName, CancellationToken cancellationToken)
    {
        string normalized = Path.GetFileName(fileName);

        foreach (string sidecarPath in Directory.EnumerateFiles(_paths.DataDirectory, "*.meta.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();

            string fileId = Path.GetFileName(sidecarPath)[..^".meta.json".Length];
            CsvFileSidecar? sidecar = await ReadSidecarAsync(fileId, cancellationToken).ConfigureAwait(false);

            if (sidecar is not null &&
                string.Equals(sidecar.FileName, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private SemaphoreSlim AcquireFileGate(string fileId) =>
        _fileLocks.GetOrAdd(fileId, static _ => new SemaphoreSlim(1, 1));

    private async Task WriteSidecarAsync(CsvFileSidecar sidecar, CancellationToken cancellationToken)
    {
        string stagingPath = _paths.CreateTempStagingPath();

        try
        {
            await using (var stagingStream = new FileStream(
                stagingPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                16 * 1024,
                FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stagingStream, sidecar, SidecarJsonOptions, cancellationToken).ConfigureAwait(false);
            }

            File.Move(stagingPath, _paths.GetSidecarPath(sidecar.FileId), overwrite: true);
        }
        catch
        {
            TryDeleteFile(stagingPath);
            throw;
        }
    }

    private async Task<CsvFileSidecar?> ReadSidecarAsync(string fileId, CancellationToken cancellationToken)
    {
        string sidecarPath = _paths.GetSidecarPath(fileId);
        if (!File.Exists(sidecarPath))
        {
            return null;
        }

        try
        {
            await using var stream = new FileStream(
                sidecarPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            return await JsonSerializer.DeserializeAsync<CsvFileSidecar>(stream, SidecarJsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                ex,
                "Sidecar metadata '{SidecarPath}' could not be read; it will be rebuilt from the CSV file.",
                sidecarPath);

            return null;
        }
    }

    private async Task<CsvFileSidecar?> ReadSidecarOrRebuildAsync(string fileId, string committedPath, CancellationToken cancellationToken)
    {
        CsvFileSidecar? sidecar = await ReadSidecarAsync(fileId, cancellationToken).ConfigureAwait(false);
        if (sidecar is not null)
        {
            return sidecar;
        }

        CsvFileSidecar? rebuilt = await RebuildSidecarFromCsvAsync(fileId, committedPath, cancellationToken).ConfigureAwait(false);
        if (rebuilt is not null)
        {
            // Persist the rebuilt sidecar so subsequent operations are fast again.
            await WriteSidecarAsync(rebuilt, cancellationToken).ConfigureAwait(false);
        }

        return rebuilt;
    }

    private async Task<CsvFileSidecar?> RebuildSidecarFromCsvAsync(string fileId, string csvPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(csvPath))
        {
            return null;
        }

        try
        {
            long sizeBytes = new FileInfo(csvPath).Length;
            var createdUtc = new DateTimeOffset(File.GetCreationTimeUtc(csvPath));
            var updatedUtc = new DateTimeOffset(File.GetLastWriteTimeUtc(csvPath));

            long dataLines = 0;
            string? firstLine = null;

            await using (var stream = new FileStream(
                csvPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                DefaultIoBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                firstLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is not null)
                {
                    dataLines++;
                }
            }

            string delimiter = firstLine is null ? "," : DetectDelimiter(firstLine);
            int columnCount = firstLine is null ? 0 : CountColumns(firstLine, delimiter);

            return new CsvFileSidecar
            {
                FileId = fileId,
                FileName = Path.GetFileName(csvPath),
                Delimiter = delimiter,
                ColumnCount = columnCount,
                RowCount = dataLines,
                SizeBytes = sizeBytes,
                CreatedAtUtc = createdUtc,
                UpdatedAtUtc = updatedUtc,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not rebuild metadata from CSV file '{CsvPath}'.", csvPath);
            return null;
        }
    }

    private static string DetectDelimiter(string firstLine)
    {
        foreach (char candidate in new[] { ',', ';', '\t', '|' })
        {
            int count = 0;
            bool inQuotes = false;

            foreach (char c in firstLine)
            {
                if (c == '"')
                {
                    inQuotes = !inQuotes;
                }
                else if (c == candidate && !inQuotes)
                {
                    count++;
                }
            }

            if (count > 0)
            {
                return char.ToString(candidate);
            }
        }

        return ",";
    }

    private static int CountColumns(string line, string delimiter)
    {
        int columns = 1;
        bool inQuotes = false;
        char d = delimiter[0];

        foreach (char c in line)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (c == d && !inQuotes)
            {
                columns++;
            }
        }

        return columns;
    }

    private void ValidateDataset(string[] headers, string[][] rows)
    {
        if (headers.Length == 0)
        {
            throw new CsvValidationException("At least one column header is required.");
        }

        if (headers.Length > _options.MaxColumns)
        {
            throw new CsvValidationException(
                $"The dataset has {headers.Length} columns but at most {_options.MaxColumns} columns are allowed.");
        }

        foreach (string? header in headers)
        {
            if ((header?.Length ?? 0) > _options.MaxCellLength)
            {
                throw new CsvValidationException(
                    $"A header cell exceeds the maximum cell length of {_options.MaxCellLength} characters.");
            }
        }

        if (rows.Length > _options.MaxRowsPerFile)
        {
            throw new CsvValidationException(
                $"The dataset has {rows.Length} rows but at most {_options.MaxRowsPerFile} rows per file are allowed.");
        }

        ValidateRowShapes(headers.Length, rows);

        // The column count is a strong signal that output will be huge; reject early to protect the server.
        if (rows.Length > 0)
        {
            long estimatedCells = (long)rows.Length * headers.Length;
            if (estimatedCells > _options.MaxRowsPerFile * 64L)
            {
                throw new CsvValidationException(
                    $"The dataset is too large: {estimatedCells} cells exceed the allowed budget.");
            }
        }
    }

    private void ValidateRowShapes(int columnCount, string[][] rows)
    {
        for (int rowIndex = 0; rowIndex < rows.Length; rowIndex++)
        {
            string[] row = rows[rowIndex];

            if (row.Length != columnCount)
            {
                throw new CsvValidationException(
                    $"Row {rowIndex + 1} has {row.Length} columns but the file defines {columnCount} columns.");
            }

            for (int columnIndex = 0; columnIndex < row.Length; columnIndex++)
            {
                if ((row[columnIndex]?.Length ?? 0) > _options.MaxCellLength)
                {
                    throw new CsvValidationException(
                        $"Cell at row {rowIndex + 1}, column {columnIndex + 1} exceeds the maximum cell length of {_options.MaxCellLength} characters.");
                }
            }
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup; the temp-file sweeper will retry later.
        }
    }
}
