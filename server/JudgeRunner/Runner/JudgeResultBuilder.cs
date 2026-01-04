using System.Text.Json;
using JudgeRunner.Models;
using JudgeRunner.Process;

namespace JudgeRunner;

/// <summary>
/// Builds results for judge executions, generating JSON-formatted output for different
/// run outcomes such as success, timeout, resource limit exceeded, and errors.
/// </summary>
public sealed class JudgeResultBuilder
{
    private const int TimeoutExitCode = 124;
    private const int ResourceLimitExitCode = 137;

    public JudgeRunResult CreateTimeoutResult(string submissionId)
    {
        string json = JsonSerializer.Serialize(new
        {
            submissionId,
            status = "timed_out"
        });
        return new JudgeRunResult(json, TimeoutExitCode);
    }

    public JudgeRunResult CreateResourceLimitResult(
        string submissionId,
        ProcessResult processResult)
    {
        string json = JsonSerializer.Serialize(new
        {
            submissionId,
            status = "resource_limit",
            diagnostics = new
            {
                note = "Resource limit exceeded; container or test host was terminated.",
                exitCode = processResult.ExitCode,
                stdout = processResult.Stdout,
                stdoutTruncated = processResult.StdoutTruncated,
                stderr = processResult.Stderr,
                stderrTruncated = processResult.StderrTruncated
            }
        });

        int exitCode = processResult.ExitCode == 0 ? ResourceLimitExitCode : processResult.ExitCode;
        return new JudgeRunResult(json, exitCode);
    }

    public JudgeRunResult CreateTrxFileSizeLimitResult(
        string submissionId,
        long trxFileSizeBytes,
        long maxTrxFileSizeBytes,
        ProcessResult testResult)
    {
        string json = JsonSerializer.Serialize(new
        {
            submissionId,
            status = "resource_limit",
            diagnostics = new
            {
                note = "TRX exceeded maximum size (likely output flooding).",
                trxBytes = trxFileSizeBytes,
                maxTrxBytes = maxTrxFileSizeBytes,
                exitCode = testResult.ExitCode
            }
        });

        int exitCode = testResult.ExitCode == 0 ? ResourceLimitExitCode : testResult.ExitCode;
        return new JudgeRunResult(json, exitCode);
    }

    public JudgeRunResult CreateRunnerErrorResult(
        string submissionId,
        string phase,
        ProcessResult processResult,
        int? exitCode = null)
    {
        string json = JsonSerializer.Serialize(new
        {
            submissionId,
            status = "runner_error",
            diagnostics = new
            {
                phase,
                exitCode = processResult.ExitCode,
                stdout = processResult.Stdout,
                stdoutTruncated = processResult.StdoutTruncated,
                stderr = processResult.Stderr,
                stderrTruncated = processResult.StderrTruncated
            }
        });

        int finalExitCode = exitCode ?? processResult.ExitCode;
        return new JudgeRunResult(json, finalExitCode);
    }

    public ProcessResult CreateConverterFailureResult(ProcessResult originalResult, Exception exception)
    {
        string errorMessage = $"\n[TRX PARSE ERROR] {exception.GetType().Name}: {exception.Message}";
        string enhancedStderr = (originalResult.Stderr ?? "") + errorMessage;

        return new ProcessResult(
            ExitCode: originalResult.ExitCode,
            Stdout: originalResult.Stdout,
            Stderr: enhancedStderr,
            TimedOut: false,
            StdoutTruncated: originalResult.StdoutTruncated,
            StderrTruncated: originalResult.StderrTruncated);
    }

    public ProcessResult CreateWorkspaceInitializationErrorResult(DirectoryNotFoundException exception)
    {
        return new ProcessResult(
            ExitCode: 1,
            Stdout: "",
            Stderr: exception.Message,
            TimedOut: false,
            StdoutTruncated: false,
            StderrTruncated: false);
    }
}

