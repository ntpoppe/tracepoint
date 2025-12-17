using System.Diagnostics;

static class Program
{
    public static int Main(string[] args)
    {
        // Toggle cleanup: pass "--keep" to inspect the workspace afterwards.
        bool keep = args.Any(a => a.Equals("--keep", StringComparison.OrdinalIgnoreCase));

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

        string workDir = Path.Combine(workRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);

        Console.WriteLine($"[JudgeRunner] Workspace: {workDir}");
        Console.WriteLine($"[JudgeRunner] Copying template from: {templateDir}");

        try
        {
            CopyDirectory(templateDir, workDir);

            // Run dotnet test INSIDE the workspace.
            var result = RunProcess(
                fileName: "dotnet",
                arguments: "test",
                workingDirectory: workDir,
                timeout: TimeSpan.FromMinutes(2)
            );

            Console.WriteLine("----- dotnet test STDOUT -----");
            Console.WriteLine(result.Stdout);

            Console.WriteLine("----- dotnet test STDERR -----");
            Console.WriteLine(result.Stderr);

            Console.WriteLine($"[JudgeRunner] ExitCode: {result.ExitCode}");
            Console.WriteLine($"[JudgeRunner] TimedOut: {result.TimedOut}");

            // If you want this step to strictly match the acceptance criteria,
            // treat timeout as failure:
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
