using System.Text.RegularExpressions;

namespace CsvMcp.Server.Services.Csv;

/// <summary>
/// Enforces the file-name policy: sanitization of user-supplied names, default name generation
/// and unique-name resolution so callers never overwrite an existing file.
/// </summary>
public static partial class CsvFileNamePolicy
{
    private const int MaxFileNameLength = 120;

    [GeneratedRegex(@"[<>:""/\\|?*\x00-\x1F]", RegexOptions.Compiled)]
    private static partial Regex InvalidCharactersRegex();

    /// <summary>Generates a default file name such as <c>export-20260601-093000.csv</c>.</summary>
    public static string CreateDefaultName(DateTimeOffset now) => $"export-{now:yyyyMMdd-HHmmss}.csv";

    /// <summary>
    /// Sanitizes a user-supplied file name: strips characters that are invalid in file names on common file systems,
    /// collapses whitespace, enforces a length limit and guarantees a <c>.csv</c> extension.
    /// </summary>
    public static string Sanitize(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return CreateDefaultName(DateTimeOffset.UtcNow);
        }

        string cleaned = InvalidCharactersRegex().Replace(fileName, string.Empty);

        // Collapse runs of whitespace and trim.
        cleaned = Regex.Replace(cleaned.Trim(), @"\s+", " ");

        if (cleaned.Length > MaxFileNameLength)
        {
            cleaned = cleaned[..MaxFileNameLength];
        }

        if (cleaned.Length == 0)
        {
            return CreateDefaultName(DateTimeOffset.UtcNow);
        }

        return cleaned.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)
            ? cleaned
            : cleaned + ".csv";
    }

    /// <summary>
    /// Returns a name that does not collide with any existing name, appending <c>-1</c>, <c>-2</c>, ... as needed.
    /// The base name is expected to end with <c>.csv</c>.
    /// </summary>
    /// <param name="baseName">Candidate file name (already sanitized).</param>
    /// <param name="nameExistsAsync">Async callback that reports whether a name is already taken.</param>
    /// <param name="cancellationToken">Cancellation token passed through to the callback.</param>
    public static async Task<string> ResolveUniqueNameAsync(
        string baseName,
        Func<string, CancellationToken, Task<bool>> nameExistsAsync,
        CancellationToken cancellationToken)
    {
        if (!await nameExistsAsync(baseName, cancellationToken).ConfigureAwait(false))
        {
            return baseName;
        }

        string stem = baseName[..^4]; // strip ".csv"
        int suffix = 1;

        while (await nameExistsAsync($"{stem}-{suffix}.csv", cancellationToken).ConfigureAwait(false))
        {
            suffix++;
        }

        return $"{stem}-{suffix}.csv";
    }
}
