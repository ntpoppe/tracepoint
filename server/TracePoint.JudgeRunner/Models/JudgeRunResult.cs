namespace Tracepoint.JudgeRunner.Models;

/// <summary>
/// Represents the result of a judge run, containing output in JSON format and the exit code.
/// </summary>
public sealed record JudgeRunResult(string Json, int ExitCode);
