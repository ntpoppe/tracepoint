using System.Diagnostics;
using System.Text;
using System.Text.Json;

static class Program
{
    public static int Main(string[] args)
    {
        // Toggle cleanup: pass "--keep" to inspect the workspace afterwards.
        bool keep = args.Any(a => a.Equals("--keep", StringComparison.OrdinalIgnoreCase));

        // User and Group ID for TRX file permissions, perhaps this should be better handled, not sure how though
        var uid = "1000";
        var gid = "1000";

        // Find repo root and template directory
        string repoRoot = FindRepoRoot();
        string templateDir = Path.Combine(repoRoot, "judge-template");

        if (!Directory.Exists(templateDir))
        {
            Console.Error.WriteLine($"ERROR: judge-template directory not found at: {templateDir}");
            return 1;
        }

        // Create a unique workspace under OS temp.
        string workRoot = Path.Combine(Path.GetTempPath(), "tracepoint-workspaces");
        Directory.CreateDirectory(workRoot);

        string guidString = Guid.NewGuid().ToString("N");
        string workDir = Path.Combine(workRoot, guidString);
        Directory.CreateDirectory(workDir);

        Console.WriteLine($"[JudgeRunner] Workspace: {workDir}");
        Console.WriteLine($"[JudgeRunner] Copying template from: {templateDir}");

        try
        {
            CopyDirectory(templateDir, workDir);

            // To restore NuGet packages since the test run will not have a network
            string nugetCacheRoot = Path.Combine(workRoot, "_nuget-cache");
            Directory.CreateDirectory(nugetCacheRoot);

            // Mount point for test container
            const string nugetMountPath = "/nuget";

            // Run restore with network
            var restoreContainerName = $"tracepoint-restore-{guidString}";
            var restoreArgs =
                $"run --rm " +
                $"--name {restoreContainerName} " +
                $"--user {uid}:{gid} " +
                $"--cpus=1 " +
                $"--memory=512m " +
                $"--memory-swap=512m " +
                $"--pids-limit=128 " +
                $"-v \"{workDir}:/workspace\" " +
                $"-v \"{nugetCacheRoot}:{nugetMountPath}\" " +
                $"-e NUGET_PACKAGES={nugetMountPath} " +
                $"-e DOTNET_SKIP_WORKLOAD_INTEGRITY_CHECK=1 " +
                $"-e DOTNET_CLI_TELEMETRY_OPTOUT=1 " +
                $"-e DOTNET_NOLOGO=1 " +
                $"-w /workspace " +
                $"mcr.microsoft.com/dotnet/sdk:10.0 " +
                $"dotnet restore";

            var restoreResult = RunProcess(
                fileName: "docker",
                arguments: restoreArgs,
                workingDirectory: workDir,
                timeout: TimeSpan.FromMinutes(1)
            );

            Console.WriteLine("----- dotnet restore STDOUT -----");
            Console.WriteLine(restoreResult.Stdout);
            Console.WriteLine("----- dotnet restore STDERR -----");
            Console.WriteLine(restoreResult.Stderr);
            Console.WriteLine($"[JudgeRunner] Restore ExitCode: {restoreResult.ExitCode}");
            Console.WriteLine($"[JudgeRunner] Restore TimedOut: {restoreResult.TimedOut}");

            if (restoreResult.TimedOut)
            {
                TryDockerCleanup(restoreContainerName, workDir);
                Console.WriteLine(BuildTimeoutJson(guidString));
                return 124;
            }

            if (restoreResult.ExitCode != 0)
            {
                Console.WriteLine(BuildRunnerErrorJson(guidString, "restore", restoreResult));
                return restoreResult.ExitCode;
            }

            // Run dotnet test with no network
            var testContainerName = $"tracepoint-test-{guidString}";
            var testArgs =
                $"run --rm " +
                $"--name {testContainerName} " +
                $"--network none " +
                $"--init " +
                $"--user {uid}:{gid} " +
                $"--cpus=1 " +
                $"--memory=512m " +
                $"--memory-swap=512m " +
                $"--pids-limit=128 " +
                $"-v \"{workDir}:/workspace\" " +
                $"-v \"{nugetCacheRoot}:{nugetMountPath}\" " +
                $"-e NUGET_PACKAGES={nugetMountPath} " +
                $"-e DOTNET_SKIP_WORKLOAD_INTEGRITY_CHECK=1 " +
                $"-e DOTNET_CLI_TELEMETRY_OPTOUT=1 " +
                $"-e DOTNET_NOLOGO=1 " +
                $"-w /workspace " +
                $"mcr.microsoft.com/dotnet/sdk:10.0 " +
                $"dotnet test --no-restore --logger \"trx;LogFileName=results.trx\"";

            var testResult = RunProcess(
                fileName: "docker",
                arguments: testArgs,
                workingDirectory: workDir,
                timeout: TimeSpan.FromSeconds(6)
            );

            Console.WriteLine("----- dotnet test STDOUT -----");
            Console.WriteLine(testResult.Stdout);

            Console.WriteLine("----- dotnet test STDERR -----");
            Console.WriteLine(testResult.Stderr);

            Console.WriteLine($"[JudgeRunner] ExitCode: {testResult.ExitCode}");
            Console.WriteLine($"[JudgeRunner] TimedOut: {testResult.TimedOut}");

            if (testResult.TimedOut)
            {
                TryDockerCleanup(testContainerName, workDir);
                Console.WriteLine(BuildTimeoutJson(guidString));
                return 124;
            }

            Console.WriteLine("----- PARSING TRX FILE -----");

            string? trxFilePath = FindTrxFile(workDir, "results.trx");
            if (trxFilePath is null)
            {
                if (LooksLikeResourceLimit(testResult))
                {
                    Console.WriteLine(BuildResourceLimitJson(guidString, testResult));
                    return testResult.ExitCode == 0 ? 137 : testResult.ExitCode;
                }

                // If dotnet test returned success but we can't find TRX, treat as runner error (missing artifact).
                Console.WriteLine(BuildRunnerErrorJson(guidString, "test_missing_trx", testResult));
                return testResult.ExitCode == 0 ? 2 : testResult.ExitCode;
            }

            // Flood-hardening: do not parse/return a massive TRX (usually caused by stdout spam).
            // Keep the threshold small for MVP; tune later.
            const long maxTrxBytes = 2_000_000; // 2 MB
            var trxInfo = new FileInfo(trxFilePath);
            if (trxInfo.Exists && trxInfo.Length > maxTrxBytes)
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    submissionId = guidString,
                    status = "resource_limit",
                    diagnostics = new
                    {
                        note = "TRX exceeded maximum size (likely output flooding).",
                        trxBytes = trxInfo.Length,
                        maxTrxBytes,
                        exitCode = testResult.ExitCode
                    }
                }));
                return testResult.ExitCode == 0 ? 137 : testResult.ExitCode;
            }

            try
            {
                var json = TrxToJsonConverter.ConvertToJson(guidString, status: "Completed", trxFilePath);
                Console.WriteLine(json);
            }
            catch (Exception ex)
            {
                // Converter failures should not take down the runner; surface diagnostic safely.
                var converterFail = new ProcessResult(
                    ExitCode: testResult.ExitCode,
                    Stdout: testResult.Stdout,
                    Stderr: (testResult.Stderr ?? "") + "\n[TRX PARSE ERROR] " + ex.GetType().Name + ": " + ex.Message,
                    TimedOut: false,
                    StdoutTruncated: testResult.StdoutTruncated,
                    StderrTruncated: testResult.StderrTruncated
                );

                Console.WriteLine(BuildRunnerErrorJson(guidString, "trx_parse", converterFail));
                return testResult.ExitCode == 0 ? 3 : testResult.ExitCode;
            }

            return testResult.ExitCode;
        }
        finally
        {
            if (!keep)
            {
                try
                {
                    Directory.Delete(workDir, recursive: true);
                    Console.WriteLine($"[JudgeRunner] Cleaned workspace: {workDir}");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[JudgeRunner] WARNING: Failed to cleanup workspace: {ex.Message}");
                }
            }
            else
            {
                Console.WriteLine($"[JudgeRunner] Keeping workspace (per --keep): {workDir}");
            }
        }
    }

    private static void TryDockerCleanup(string containerName, string workingDirectory)
    {
        RunDockerCleanup($"kill {containerName}", workingDirectory, TimeSpan.FromSeconds(5));
        RunDockerCleanup($"rm -f {containerName}", workingDirectory, TimeSpan.FromSeconds(5));
    }

    private static bool RunDockerCleanup(string args, string workingDirectory, TimeSpan timeout)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "docker",
                Arguments = args,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var p = Process.Start(psi);
            if (p is null) return false;

            var stdoutTask = p.StandardOutput.ReadToEndAsync();
            var stderrTask = p.StandardError.ReadToEndAsync();

            if (!p.WaitForExit((int)timeout.TotalMilliseconds))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return false;
            }

            Task.WaitAll(new Task[] { stdoutTask, stderrTask }, TimeSpan.FromSeconds(2));

            var err = stderrTask.IsCompleted ? stderrTask.Result : "";

            if (p.ExitCode != 0)
            {
                if (err.Contains("No such container", StringComparison.OrdinalIgnoreCase) ||
                    err.Contains("already in progress", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string FindRepoRoot()
    {
        string dir = Directory.GetCurrentDirectory();

        while (!string.IsNullOrWhiteSpace(dir))
        {
            if (Directory.Exists(Path.Combine(dir, "judge-template")) &&
                Directory.Exists(Path.Combine(dir, "server")))
            {
                return dir;
            }

            var parent = Directory.GetParent(dir);
            if (parent is null) break;
            dir = parent.FullName;
        }

        return Directory.GetCurrentDirectory();
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            string destFile = Path.Combine(destDir, Path.GetFileName(file));
            File.Copy(file, destFile, overwrite: true);
        }

        foreach (var subDir in Directory.GetDirectories(sourceDir))
        {
            string destSubDir = Path.Combine(destDir, Path.GetFileName(subDir));
            Directory.CreateDirectory(destSubDir);
            CopyDirectory(subDir, destSubDir);
        }
    }

    private static ProcessResult RunProcess(
        string fileName,
        string arguments,
        string workingDirectory,
        TimeSpan timeout)
    {
        const int maxStdoutChars = 64_000;
        const int maxStderrChars = 64_000;

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var proc = new Process { StartInfo = psi };
        proc.Start();

        var stdoutPump = PumpLimitedAsync(proc.StandardOutput, maxStdoutChars);
        var stderrPump = PumpLimitedAsync(proc.StandardError, maxStderrChars);

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

    private static string? FindTrxFile(string testResultsRoot, string preferredName)
    {
        if (!Directory.Exists(testResultsRoot)) return null;

        var exact = Directory.GetFiles(testResultsRoot, preferredName, SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

        if (exact is not null) return exact;

        return Directory.GetFiles(testResultsRoot, "*.trx", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    private static bool LooksLikeResourceLimit(ProcessResult r)
    {
        if (r.ExitCode == 137) return true;

        var s = (r.Stderr ?? "") + "\n" + (r.Stdout ?? "");
        return s.Contains("Out of memory", StringComparison.OrdinalIgnoreCase) ||
               s.Contains("OutOfMemoryException", StringComparison.OrdinalIgnoreCase) ||
               s.Contains("Killed", StringComparison.OrdinalIgnoreCase) ||
               s.Contains("Test host process crashed", StringComparison.OrdinalIgnoreCase) ||
               s.Contains("Test Run Aborted", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildTimeoutJson(string submissionId)
    {
        return JsonSerializer.Serialize(new
        {
            submissionId,
            status = "timed_out"
        });
    }

    private static string BuildResourceLimitJson(string submissionId, ProcessResult r)
    {
        return JsonSerializer.Serialize(new
        {
            submissionId,
            status = "resource_limit",
            diagnostics = new
            {
                note = "Resource limit exceeded; container or test host was terminated.",
                exitCode = r.ExitCode,
                stdout = r.Stdout,
                stdoutTruncated = r.StdoutTruncated,
                stderr = r.Stderr,
                stderrTruncated = r.StderrTruncated
            }
        });
    }

    private static string BuildRunnerErrorJson(string submissionId, string phase, ProcessResult r)
    {
        return JsonSerializer.Serialize(new
        {
            submissionId,
            status = "runner_error",
            diagnostics = new
            {
                phase,
                exitCode = r.ExitCode,
                stdout = r.Stdout,
                stdoutTruncated = r.StdoutTruncated,
                stderr = r.Stderr,
                stderrTruncated = r.StderrTruncated
            }
        });
    }

    private sealed record ProcessResult(
        int ExitCode,
        string Stdout,
        string Stderr,
        bool TimedOut,
        bool StdoutTruncated,
        bool StderrTruncated
    );
}
