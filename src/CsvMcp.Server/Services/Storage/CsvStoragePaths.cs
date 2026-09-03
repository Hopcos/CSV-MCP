using CsvMcp.Server.Configuration;
using CsvMcp.Server.Domain;
using Microsoft.Extensions.Options;

namespace CsvMcp.Server.Services.Storage;

/// <summary>
/// Default <see cref="ICsvStoragePaths"/> implementation backed by the configured root directory.
/// Directories are created lazily on first use to keep startup side-effect free.
/// </summary>
public sealed class CsvStoragePaths : ICsvStoragePaths
{
    private static readonly char[] AllowedFileIdCharacters = "0123456789abcdefABCDEF-".ToCharArray();

    private readonly string _dataDirectory;
    private readonly string _tempDirectory;

    /// <summary>Initializes a new instance of the <see cref="CsvStoragePaths"/> class.</summary>
    public CsvStoragePaths(IOptions<CsvStorageOptions> options, IHostEnvironment environment)
    {
        CsvStorageOptions storageOptions = options.Value;

        string root = Path.GetFullPath(
            Path.Combine(environment.ContentRootPath, storageOptions.RootDirectory));

        _dataDirectory = Path.GetFullPath(Path.Combine(root, storageOptions.CsvSubdirectory));
        _tempDirectory = Path.GetFullPath(Path.Combine(root, storageOptions.TempSubdirectory));
    }

    /// <inheritdoc/>
    public string DataDirectory => _dataDirectory;

    /// <inheritdoc/>
    public string TempDirectory => _tempDirectory;

    /// <inheritdoc/>
    public string GetCommittedFilePath(string fileId)
    {
        if (!IsValidFileId(fileId))
        {
            throw new CsvValidationException(
                $"Invalid file ID '{fileId}'. File IDs only contain hexadecimal digits and dashes.");
        }

        return Path.Combine(_dataDirectory, fileId + ".csv");
    }

    /// <inheritdoc/>
    public string GetSidecarPath(string fileId) => Path.Combine(_dataDirectory, fileId + ".meta.json");

    /// <inheritdoc/>
    public string CreateTempStagingPath()
    {
        Directory.CreateDirectory(_tempDirectory);
        return Path.Combine(_tempDirectory, $"{Guid.NewGuid():N}.tmp");
    }

    /// <inheritdoc/>
    public bool IsValidFileId(string fileId)
    {
        if (string.IsNullOrEmpty(fileId) || fileId.Length is < 1 or > 64)
        {
            return false;
        }

        return fileId.All(c => AllowedFileIdCharacters.Contains(c));
    }
}
