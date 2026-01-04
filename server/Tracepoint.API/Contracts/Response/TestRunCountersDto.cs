namespace Tracepoint.API.Contracts;

/// <summary>
/// Test execution counters.
/// </summary>
public sealed record TestRunCountersDto(
    int Total,
    int Executed,
    int Passed,
    int Failed,
    int Skipped,
    int Error,
    int Timeout,
    int Aborted,
    int Inconclusive
);

