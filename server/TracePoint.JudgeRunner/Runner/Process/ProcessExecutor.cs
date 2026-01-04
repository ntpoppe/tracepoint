using System.Diagnostics;
using System.Text;
using Tracepoint.JudgeRunner.Models;

namespace Tracepoint.JudgeRunner.Process;

/// <summary>
/// Executes processes with limited output buffering, truncating excessively long
/// output to prevent memory bloat and ensure timely termination.
/// </summary>
public sealed class ProcessExecutor
{
    private const int MaxStdoutChars = 64_000;
    private const int MaxStderrChars = 64_000;

    public ProcessResult Run(
        string fileName,
        string arguments,
        string workingDirectory,
        TimeSpan timeout)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var proc = new System.Diagnostics.Process { StartInfo = psi };
        proc.Start();

        var stdoutPump = PumpLimitedAsync(proc.StandardOutput, MaxStdoutChars);
        var stderrPump = PumpLimitedAsync(proc.StandardError, MaxStderrChars);

        bool exited = proc.WaitForExit((int)timeout.TotalMilliseconds);
        bool timedOut = !exited;

        if (timedOut)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
        }
        else
        {
            proc.WaitForExit();
        }

        Task.WaitAll(new Task[] { stdoutPump, stderrPump }, TimeSpan.FromSeconds(5));

        var (stdout, stdoutTruncated) = stdoutPump.IsCompleted ? stdoutPump.Result : ("", true);
        var (stderr, stderrTruncated) = stderrPump.IsCompleted ? stderrPump.Result : ("", true);

        return new ProcessResult(
            ExitCode: timedOut ? -1 : proc.ExitCode,
            Stdout: stdout,
            Stderr: stderr,
            TimedOut: timedOut,
            StdoutTruncated: stdoutTruncated,
            StderrTruncated: stderrTruncated
        );
    }

    /// <summary>
    /// Reads output from a <see cref="StreamReader"/>, keeping up to <paramref name="maxChars"/> chars.
    /// If output is truncated, adds a marker and sets <c>Truncated</c> to true.
    /// </summary>
    private static async Task<(string Text, bool Truncated)> PumpLimitedAsync(StreamReader reader, int maxChars)
    {
        var sb = new StringBuilder(capacity: Math.Min(maxChars, 8_192));
        var buffer = new char[8_192];

        int kept = 0;
        bool truncated = false;
        bool markerAdded = false;

        while (true)
        {
            int read = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
            if (read <= 0) break;

            if (kept < maxChars)
            {
                int toKeep = Math.Min(read, maxChars - kept);
                if (toKeep > 0)
                {
                    sb.Append(buffer, 0, toKeep);
                    kept += toKeep;
                }

                if (toKeep < read)
                {
                    truncated = true;
                    if (!markerAdded)
                    {
                        sb.AppendLine();
                        sb.AppendLine("...[TRUNCATED: output exceeded limit]...");
                        markerAdded = true;
                    }
                }
            }
            else
            {
                truncated = true;
                if (!markerAdded)
                {
                    sb.AppendLine();
                    sb.AppendLine("...[TRUNCATED: output exceeded limit]...");
                    markerAdded = true;
                }
            }

            // Keep draining after truncation to avoid blocking the child process.
        }

        return (sb.ToString(), truncated);
    }
}

