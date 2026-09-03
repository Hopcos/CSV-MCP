using CsvMcp.Server.Configuration;
using CsvMcp.Server.Services.Storage;
using Microsoft.Extensions.Options;

namespace CsvMcp.Server.Services.Maintenance;

/// <summary>
/// Background service that sweeps stale files out of the temp directory.
/// Runs once at startup and then on a configurable interval (see <see cref="CsvStorageOptions.CleanupInterval"/>);
/// files older than <see cref="CsvStorageOptions.TempFileMaxAge"/> are removed.
/// </summary>
public sealed class TempFileCleanupService : BackgroundService
{
    private readonly ICsvStoragePaths _paths;
    private readonly CsvStorageOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<TempFileCleanupService> _logger;

    /// <summary>Initializes a new instance of the <see cref="TempFileCleanupService"/> class.</summary>
    public TempFileCleanupService(
        ICsvStoragePaths paths,
        IOptions<CsvStorageOptions> options,
        TimeProvider timeProvider,
        ILogger<TempFileCleanupService> logger)
    {
        _paths = paths;
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.CleanupInterval);

        do
        {
            CleanOnce(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    private void CleanOnce(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_paths.TempDirectory))
        {
            return;
        }

        DateTimeOffset cutoff = _timeProvider.GetUtcNow() - _options.TempFileMaxAge;
        int removed = 0;

        foreach (string file in Directory.EnumerateFiles(_paths.TempDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                DateTimeOffset lastWrite = File.GetLastWriteTimeUtc(file);
                if (lastWrite <= cutoff)
                {
                    File.Delete(file);
                    removed++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "Could not remove stale temp file '{Path}'.", file);
            }
        }

        if (removed > 0)
        {
            _logger.LogInformation("Temp-file cleanup removed {Count} stale file(s) from '{TempDirectory}'.", removed, _paths.TempDirectory);
        }
    }
}
