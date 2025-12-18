using System.Text.Json;

public sealed class JudgeRunner
{
    private readonly WorkspaceManager _workspaceManager;
    private readonly DockerCommandBuilder _docker;
    private readonly ProcessExecutor _processExecutor;

    public JudgeRunner()
        : this(new WorkspaceManager(), new DockerCommandBuilder(), new ProcessExecutor())
    {
    }

    public JudgeRunner(
        WorkspaceManager workspaceManager,
        DockerCommandBuilder docker,
        ProcessExecutor processExecutor)
    {
        _workspaceManager = workspaceManager;
        _docker = docker;
        _processExecutor = processExecutor;
    }

    public JudgeRunResult Run(bool keepWorkspace)
    {
        string submissionId = Guid.NewGuid().ToString("N");
        WorkspacePaths? workspace = null;

        try
        {
            workspace = _workspaceManager.CreateWorkspace(submissionId);

            // Run restore with network
            var restoreContainerName = $"tracepoint-restore-{submissionId}";
            var restoreArgs = _docker.BuildRestoreArguments(
                containerName: restoreContainerName,
                workDir: workspace.WorkDir,
                nugetCacheRoot: workspace.NugetCacheRoot);

            var restoreResult = _processExecutor.Run(
                fileName: "docker",
                arguments: restoreArgs,
                workingDirectory: workspace.WorkDir,
                timeout: TimeSpan.FromMinutes(1));

            Console.WriteLine("----- dotnet restore STDOUT -----");
            Console.WriteLine(restoreResult.Stdout);
            Console.WriteLine("----- dotnet restore STDERR -----");
            Console.WriteLine(restoreResult.Stderr);
            Console.WriteLine($"[JudgeRunner] Restore ExitCode: {restoreResult.ExitCode}");
            Console.WriteLine($"[JudgeRunner] Restore TimedOut: {restoreResult.TimedOut}");

            if (restoreResult.TimedOut)
            {
                TryDockerCleanup(restoreContainerName, workspace.WorkDir);
                var json = BuildTimeoutJson(submissionId);
                return new JudgeRunResult(json, 124);
            }

            if (restoreResult.ExitCode != 0)
            {
                var json = BuildRunnerErrorJson(submissionId, "restore", restoreResult);
                return new JudgeRunResult(json, restoreResult.ExitCode);
            }

            // Run dotnet test with no network
            var testContainerName = $"tracepoint-test-{submissionId}";
            var testArgs = _docker.BuildTestArguments(
                containerName: testContainerName,
                workDir: workspace.WorkDir,
                nugetCacheRoot: workspace.NugetCacheRoot,
                trxFileName: "results.trx");

            var testResult = _processExecutor.Run(
                fileName: "docker",
                arguments: testArgs,
                workingDirectory: workspace.WorkDir,
                timeout: TimeSpan.FromSeconds(6));

            Console.WriteLine("----- dotnet test STDOUT -----");
            Console.WriteLine(testResult.Stdout);

            Console.WriteLine("----- dotnet test STDERR -----");
            Console.WriteLine(testResult.Stderr);

            Console.WriteLine($"[JudgeRunner] ExitCode: {testResult.ExitCode}");
            Console.WriteLine($"[JudgeRunner] TimedOut: {testResult.TimedOut}");

            if (testResult.TimedOut)
            {
                TryDockerCleanup(testContainerName, workspace.WorkDir);
                var json = BuildTimeoutJson(submissionId);
                return new JudgeRunResult(json, 124);
            }

            Console.WriteLine("----- PARSING TRX FILE -----");

            string? trxFilePath = _workspaceManager.FindTrxFile(workspace.WorkDir, "results.trx");
            if (trxFilePath is null)
            {
                if (LooksLikeResourceLimit(testResult))
                {
                    var json = BuildResourceLimitJson(submissionId, testResult);
                    var exit = testResult.ExitCode == 0 ? 137 : testResult.ExitCode;
                    return new JudgeRunResult(json, exit);
                }

                // If dotnet test returned success but we can't find TRX, treat as runner error (missing artifact).
                var missingTrxJson = BuildRunnerErrorJson(submissionId, "test_missing_trx", testResult);
                var missingTrxExit = testResult.ExitCode == 0 ? 2 : testResult.ExitCode;
                return new JudgeRunResult(missingTrxJson, missingTrxExit);
            }

            // Flood-hardening: do not parse/return a massive TRX (usually caused by stdout spam).
            // Keep the threshold small for MVP; tune later.
            const long maxTrxBytes = 2_000_000; // 2 MB
            var trxInfo = new FileInfo(trxFilePath);
            if (trxInfo.Exists && trxInfo.Length > maxTrxBytes)
            {
                var json = JsonSerializer.Serialize(new
                {
                    submissionId,
                    status = "resource_limit",
                    diagnostics = new
                    {
                        note = "TRX exceeded maximum size (likely output flooding).",
                        trxBytes = trxInfo.Length,
                        maxTrxBytes,
                        exitCode = testResult.ExitCode
                    }
                });

                var exit = testResult.ExitCode == 0 ? 137 : testResult.ExitCode;
                return new JudgeRunResult(json, exit);
            }

            try
            {
                var json = TrxToJsonConverter.ConvertToJson(submissionId, status: "Completed", trxFilePath);
                return new JudgeRunResult(json, testResult.ExitCode);
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

                var json = BuildRunnerErrorJson(submissionId, "trx_parse", converterFail);
                var exit = testResult.ExitCode == 0 ? 3 : testResult.ExitCode;
                return new JudgeRunResult(json, exit);
            }
        }
        catch (DirectoryNotFoundException ex)
        {
            // Template or repo root not found: treat as runner_error.
            var r = new ProcessResult(
                ExitCode: 1,
                Stdout: "",
                Stderr: ex.Message,
                TimedOut: false,
                StdoutTruncated: false,
                StderrTruncated: false
            );

            var json = BuildRunnerErrorJson(submissionId, "workspace_init", r);
            return new JudgeRunResult(json, 1);
        }
        finally
        {
            if (workspace is not null)
            {
                _workspaceManager.CleanupWorkspace(workspace, keepWorkspace);
            }
        }
    }

    private void TryDockerCleanup(string containerName, string workingDirectory)
    {
        try
        {
            _processExecutor.Run("docker", $"kill {containerName}", workingDirectory, TimeSpan.FromSeconds(5));
        }
        catch
        {
            // best-effort only
        }

        try
        {
            _processExecutor.Run("docker", $"rm -f {containerName}", workingDirectory, TimeSpan.FromSeconds(5));
        }
        catch
        {
            // best-effort only
        }
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
}

public sealed record JudgeRunResult(string Json, int ExitCode);


