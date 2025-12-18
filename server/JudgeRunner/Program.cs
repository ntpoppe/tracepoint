using System.Diagnostics;

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

            // Run dotnet test INSIDE the workspace.
            var result = RunProcess(
                fileName: "docker",
                arguments:
                    $"run --rm " +
                    $"--user {uid}:{gid} " +
                    $"--cpus=1 " +
                    $"--memory=512m " +
                    $"--pids-limit=128 " +
                    $"-v \"{workDir}:/workspace\" " +
                    $"-w /workspace " +
                    $"mcr.microsoft.com/dotnet/sdk:10.0 " +
                    $"dotnet test --logger \"trx;LogFileName=results.trx\"",
                workingDirectory: workDir,
                timeout: TimeSpan.FromMinutes(2)
            );

            Console.WriteLine("----- dotnet test STDOUT -----");
            Console.WriteLine(result.Stdout);

            Console.WriteLine("----- dotnet test STDERR -----");
            Console.WriteLine(result.Stderr);

            Console.WriteLine($"[JudgeRunner] ExitCode: {result.ExitCode}");
            Console.WriteLine($"[JudgeRunner] TimedOut: {result.TimedOut}");

            Console.WriteLine("----- PARSING TRX FILE -----");
            string testsDir = Path.Combine(workDir, "tests");
            string testsProject = Path.Combine(testsDir, "Challenge.Tests");
            string testResultsDir = Path.Combine(testsProject, "TestResults");
            string trxFilePath = Path.Combine(testResultsDir, "results.trx");

            if (!File.Exists(trxFilePath))
            {
                Console.Error.WriteLine($"ERROR: TRX file not found at: {trxFilePath}");
                return 1;
            }

            var json = TrxToJsonConverter.ConvertToJson(guidString, status: "Completed", trxFilePath);
            Console.WriteLine(json);

            // Treat timeout as failure.
            int exitCode = result.TimedOut ? 124 : result.ExitCode;

            return exitCode;
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

    private static string FindRepoRoot()
    {
        // Walk upward until we find a directory containing "judge-template".
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

        // Fall back to current directory; will fail later with clear error if missing.
        return Directory.GetCurrentDirectory();
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        // Copy all files
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            string destFile = Path.Combine(destDir, Path.GetFileName(file));
            File.Copy(file, destFile, overwrite: true);
        }

        // Copy all subdirectories
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

        // Read streams asynchronously to avoid deadlocks.
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();

        bool exited = proc.WaitForExit((int)timeout.TotalMilliseconds);
        bool timedOut = !exited;

        if (timedOut)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
        }
        else
        {
            // Ensure the async reads complete.
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

    private sealed record ProcessResult(int ExitCode, string Stdout, string Stderr, bool TimedOut);
}
