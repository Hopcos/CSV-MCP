namespace CsvMcp.Server.Services.Storage;

/// <summary>
/// Resolves the on-disk layout of the storage (committed CSV files, sidecar metadata and temp staging files)
/// and centralizes path validation so untrusted file IDs can never escape the data directory (path traversal guard).
/// </summary>
public interface ICsvStoragePaths
{
    /// <summary>Absolute path of the directory that stores committed CSV files and sidecar metadata.</summary>
    string DataDirectory { get; }

    /// <summary>Absolute path of the directory that stores staging/temp files.</summary>
    string TempDirectory { get; }

    /// <summary>
    /// Returns the absolute path of the committed CSV file for a file ID.
    /// </summary>
    /// <param name="fileId">A valid file ID (see <see cref="IsValidFileId"/>).</param>
    /// <returns>The absolute path of <c>{fileId}.csv</c> inside the data directory.</returns>
    /// <exception cref="Domain.CsvValidationException">When the file ID is invalid.</exception>
    string GetCommittedFilePath(string fileId);

    /// <summary>Returns the absolute path of the sidecar metadata file for a file ID.</summary>
    string GetSidecarPath(string fileId);

    /// <summary>Creates and returns the path of a fresh, unique staging file inside the temp directory.</summary>
    string CreateTempStagingPath();

    /// <summary>
    /// Validates that a file ID only contains the characters allowed in committed file names.
    /// </summary>
    bool IsValidFileId(string fileId);
}
