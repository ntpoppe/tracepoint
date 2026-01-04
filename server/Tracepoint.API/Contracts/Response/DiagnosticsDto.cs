namespace Tracepoint.API.Contracts;

/// <summary>
/// Diagnostic information including output streams and notes.
/// </summary>
public sealed record DiagnosticsDto(
    string? Stdout,
    string? Stderr,
    string? TrxPath,
    string? Note
);

