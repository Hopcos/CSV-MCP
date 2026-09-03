namespace CsvMcp.Server.Domain;

/// <summary>
/// Thrown when tool input does not satisfy CSV validation rules.
/// The message is deliberately user/agent friendly and safe to surface in a tool result.
/// </summary>
public sealed class CsvValidationException(string message) : Exception(message);
