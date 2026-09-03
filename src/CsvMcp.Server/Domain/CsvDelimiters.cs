namespace CsvMcp.Server.Domain;

/// <summary>
/// Normalizes user-supplied delimiter tokens into the actual delimiter character.
/// Keeps the tool input schema stable while supporting the most common CSV variants.
/// </summary>
public static class CsvDelimiters
{
    /// <summary>Token for the comma delimiter (RFC 4180 default).</summary>
    public const string Comma = "comma";

    /// <summary>Token for the semicolon delimiter (common in European locales).</summary>
    public const string Semicolon = "semicolon";

    /// <summary>Token for the tab delimiter (TSV).</summary>
    public const string Tab = "tab";

    /// <summary>Token for the pipe delimiter.</summary>
    public const string Pipe = "pipe";

    /// <summary>
    /// Converts a delimiter token into the actual delimiter character.
    /// </summary>
    /// <param name="token">One of <see cref="Comma"/>, <see cref="Semicolon"/>, <see cref="Tab"/> or <see cref="Pipe"/>; case-insensitive.</param>
    /// <returns>The delimiter character.</returns>
    /// <exception cref="CsvValidationException">When the token is not recognized.</exception>
    public static string Normalize(string? token)
    {
        return (token ?? Comma).Trim().ToLowerInvariant() switch
        {
            "" or Comma => ",",
            Semicolon => ";",
            Tab => "\t",
            Pipe => "|",
            _ => throw new CsvValidationException(
                $"Unsupported delimiter token '{token}'. Supported tokens: {Comma}, {Semicolon}, {Tab}, {Pipe}.")
        };
    }
}
