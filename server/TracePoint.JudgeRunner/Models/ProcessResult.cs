namespace Tracepoint.JudgeRunner.Models;

/// <summary>
/// Represents the result of a process execution, including exit code, output, and truncation flags.
/// </summary>
public sealed record ProcessResult(
    int ExitCode,
    string Stdout,
    string Stderr,
    bool TimedOut,
    bool StdoutTruncated,
    bool StderrTruncated
);