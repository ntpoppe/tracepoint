namespace Tracepoint.API.Contracts;

/// <summary>
/// Represents a submission to the judge, containing the submission ID and the code to be judged.
/// </summary>
public sealed record Submission(
    string SubmissionId,
    string Code
);