using System.Diagnostics;
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

        // Find repo root and template directory (relative to this executable's working directory).
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
                $"--pids-limit=128 " +
                $"-v \"{workDir}:/workspace\" " +
                $"-v \"{nugetCacheRoot}:{nugetMountPath}\" " +
                $"-e NUGET_PACKAGES={nugetMountPath} " +
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

            string testResultsRoot = Path.Combine(workDir, "TestResults");
            string? trxFilePath = FindTrxFile(testResultsRoot, "results.trx");

            if (trxFilePath is null)
            {
                if (LooksLikeResourceLimit(testResult))
                {
                    Console.WriteLine(BuildResourceLimitJson(guidString, testResult));
                    return testResult.ExitCode == 0 ? 137 : testResult.ExitCode;
                }

                Console.WriteLine(BuildRunnerErrorJson(guidString, "test", testResult));
                return 127;
            }

            var json = TrxToJsonConverter.ConvertToJson(guidString, status: "Completed", trxFilePath);
            Console.WriteLine(json);

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

        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();

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

        Task.WaitAll(new Task[] { stdoutTask, stderrTask }, TimeSpan.FromSeconds(5));

        return new ProcessResult(
            ExitCode: timedOut ? -1 : proc.ExitCode,
            Stdout: stdoutTask.IsCompleted ? stdoutTask.Result : "",
            Stderr: stderrTask.IsCompleted ? stderrTask.Result : "",
            TimedOut: timedOut
        );
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
               s.Contains("Killed", StringComparison.OrdinalIgnoreCase);
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
                note = "Memory limit exceeded; container was terminated.",
                exitCode = r.ExitCode,
                stdout = r.Stdout,
                stderr = r.Stderr
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
                stderr = r.Stderr
            }
        });
    }

    private sealed record ProcessResult(int ExitCode, string Stdout, string Stderr, bool TimedOut);
}
