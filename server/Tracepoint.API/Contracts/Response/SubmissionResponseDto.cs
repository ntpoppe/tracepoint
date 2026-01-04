namespace Tracepoint.API.Contracts;

/// <summary>
/// Response DTO for judge execution results, containing test run information,
/// individual test results, and diagnostics.
/// </summary>
public sealed record SubmissionResponseDto(
    string SubmissionId,
    string Status,
    TestRunDto? Run,
    IReadOnlyList<TestResultDto> Tests,
    DiagnosticsDto Diagnostics
);
