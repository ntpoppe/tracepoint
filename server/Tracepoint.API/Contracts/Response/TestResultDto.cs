namespace Tracepoint.API.Contracts;

/// <summary>
/// Individual test result information.
/// </summary>
public sealed record TestResultDto(
    string Id,
    string Name,
    string? ClassName,
    string? FullyQualifiedName,
    string Outcome,
    int DurationMs,
    string? StartedAt,
    string? FinishedAt,
    string? Message,
    string? StackTrace
);

