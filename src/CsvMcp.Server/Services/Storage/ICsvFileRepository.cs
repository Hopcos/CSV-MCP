using CsvMcp.Server.Domain;

namespace CsvMcp.Server.Services.Storage;

/// <summary>
/// The CSV file store. All operations are asynchronous and thread-safe:
/// each committed file is guarded by its own <see cref="System.Threading.SemaphoreSlim"/> so concurrent
/// appends/deletes from parallel tool invocations serialize per file without blocking the whole store.
/// Writes are staged through the temp directory and moved into place atomically (all-or-nothing commits).
/// </summary>
public interface ICsvFileRepository
{
    /// <summary>Creates a new CSV file from headers and rows and returns its metadata.</summary>
    Task<CsvMetadata> CreateAsync(string fileName, string[] headers, string[][] rows, string delimiter, CancellationToken cancellationToken);

    /// <summary>Appends rows to an existing CSV file and returns the updated metadata; <c>null</c> when the file does not exist.</summary>
    Task<CsvMetadata?> AppendRowsAsync(string fileId, string[][] rows, CancellationToken cancellationToken);

    /// <summary>Lists committed files, optionally filtered by name substring and paginated.</summary>
    Task<IReadOnlyList<CsvMetadata>> ListAsync(string? namePattern, int? offset, int? limit, CancellationToken cancellationToken);

    /// <summary>Returns the metadata of one file; <c>null</c> when it does not exist.</summary>
    Task<CsvMetadata?> GetMetadataAsync(string fileId, CancellationToken cancellationToken);

    /// <summary>Reads a slice of a file's content; <c>null</c> when the file does not exist.</summary>
    Task<CsvReadResult?> ReadContentAsync(string fileId, int rowOffset, int maxRows, bool includeHeader, CancellationToken cancellationToken);

    /// <summary>Returns the committed path of a file ready for streaming; <c>null</c> when it does not exist.</summary>
    Task<string?> GetDownloadPathAsync(string fileId, CancellationToken cancellationToken);

    /// <summary>Reads the complete file content as bytes (used for inline base64 downloads); <c>null</c> when the file does not exist.</summary>
    Task<byte[]?> ReadAllBytesAsync(string fileId, CancellationToken cancellationToken);

    /// <summary>Deletes a file and its sidecar; returns the metadata of the deleted file or <c>null</c> when it did not exist.</summary>
    Task<CsvMetadata?> DeleteAsync(string fileId, CancellationToken cancellationToken);

    /// <summary>
    /// Reports whether a file name (a display name, e.g. <c>member-spins.csv</c>) is already taken by a committed file.
    /// Matches the display names stored in sidecar metadata, case-insensitively.
    /// </summary>
    Task<bool> NameExistsAsync(string fileName, CancellationToken cancellationToken);
}
