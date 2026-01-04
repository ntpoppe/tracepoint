namespace Tracepoint.API.Contracts;

/// <summary>
/// Test run information including timing, outcome, and counters.
/// </summary>
public sealed record TestRunDto(
    string? TestRunId,
    string OverallOutcome,
    string? CreatedAt,
    string? StartedAt,
    string? FinishedAt,
    int DurationMs,
    TestRunCountersDto Counters
);

