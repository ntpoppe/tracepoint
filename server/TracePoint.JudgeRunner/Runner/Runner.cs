using Tracepoint.JudgeRunner.Docker;
using Tracepoint.JudgeRunner.Process;
using Tracepoint.JudgeRunner.Converters;
using Tracepoint.JudgeRunner.Models;

namespace Tracepoint.JudgeRunner;

public class Runner
{
    private const long MaximumTrxFileSizeBytes = 2_000_000;
    private const int ResourceLimitExitCode = 137;

    private readonly WorkspaceManager _workspaceManager;
    private readonly DockerCommandBuilder _docker;
    private readonly ProcessExecutor _processExecutor;
    private readonly JudgeResultBuilder _resultBuilder;

    public Runner()
        : this(new WorkspaceManager(), new DockerCommandBuilder(), new ProcessExecutor(), new JudgeResultBuilder())
    {
    }

    public Runner(
        WorkspaceManager workspaceManager,
        DockerCommandBuilder docker,
        ProcessExecutor processExecutor,
        JudgeResultBuilder resultBuilder)
    {
        _workspaceManager = workspaceManager;
        _docker = docker;
        _processExecutor = processExecutor;
        _resultBuilder = resultBuilder;
    }

    public JudgeRunResult Run(bool keepWorkspace)
    {
        string submissionId = GenerateSubmissionId();
        WorkspacePaths? workspace = null;

        try
        {
            workspace = _workspaceManager.CreateWorkspace(submissionId);

            var restoreResult = RunRestore(submissionId, workspace);
            if (restoreResult is not null)
                return restoreResult;

            var testResult = RunTests(submissionId, workspace);
            return ProcessTestResults(submissionId, workspace, testResult);
        }
        catch (DirectoryNotFoundException exception)
        {
            return HandleWorkspaceInitializationError(submissionId, exception);
        }
        finally
        {
            CleanupWorkspaceIfNeeded(workspace, keepWorkspace);
        }
    }

    private JudgeRunResult? RunRestore(string submissionId, WorkspacePaths workspace)
    {
        string containerName = BuildRestoreContainerName(submissionId);
        string restoreArguments = _docker.BuildRestoreArguments(
            containerName: containerName,
            workDir: workspace.WorkDir,
            nugetCacheRoot: workspace.NugetCacheRoot);

        ProcessResult restoreResult = _processExecutor.Run(
            fileName: "docker",
            arguments: restoreArguments,
            workingDirectory: workspace.WorkDir,
            timeout: TimeSpan.FromMinutes(1));

        LogProcessOutput("restore", restoreResult);

        if (restoreResult.TimedOut)
        {
            TryDockerCleanup(containerName, workspace.WorkDir);
            return _resultBuilder.CreateTimeoutResult(submissionId);
        }

        if (restoreResult.ExitCode != 0)
        {
            return _resultBuilder.CreateRunnerErrorResult(submissionId, "restore", restoreResult);
        }

        return null;
    }

    private ProcessResult RunTests(string submissionId, WorkspacePaths workspace)
    {
        string containerName = BuildTestContainerName(submissionId);
        string testArguments = _docker.BuildTestArguments(
            containerName: containerName,
            workDir: workspace.WorkDir,
            nugetCacheRoot: workspace.NugetCacheRoot,
            trxFileName: "results.trx");

        ProcessResult testResult = _processExecutor.Run(
            fileName: "docker",
            arguments: testArguments,
            workingDirectory: workspace.WorkDir,
            timeout: TimeSpan.FromSeconds(6));

        LogProcessOutput("test", testResult);

        if (testResult.TimedOut)
        {
            TryDockerCleanup(containerName, workspace.WorkDir);
        }

        return testResult;
    }

    private JudgeRunResult ProcessTestResults(
        string submissionId,
        WorkspacePaths workspace,
        ProcessResult testResult)
    {
        if (testResult.TimedOut)
        {
            return _resultBuilder.CreateTimeoutResult(submissionId);
        }

        string? trxFilePath = _workspaceManager.FindTrxFile(workspace.WorkDir, "results.trx");
        if (trxFilePath is null)
        {
            return HandleMissingTrxFile(submissionId, testResult);
        }

        if (IsTrxFileTooLarge(trxFilePath))
        {
            var fileInfo = new FileInfo(trxFilePath);
            return _resultBuilder.CreateTrxFileSizeLimitResult(
                submissionId,
                fileInfo.Length,
                MaximumTrxFileSizeBytes,
                testResult);
        }

        return ConvertTrxToResult(submissionId, trxFilePath, testResult);
    }

    private JudgeRunResult HandleMissingTrxFile(string submissionId, ProcessResult testResult)
    {
        if (IndicatesResourceLimit(testResult))
        {
            return _resultBuilder.CreateResourceLimitResult(submissionId, testResult);
        }

        int exitCode = testResult.ExitCode == 0 ? 2 : testResult.ExitCode;
        return _resultBuilder.CreateRunnerErrorResult(submissionId, "test_missing_trx", testResult, exitCode);
    }

    private bool IsTrxFileTooLarge(string trxFilePath)
    {
        var fileInfo = new FileInfo(trxFilePath);
        return fileInfo.Exists && fileInfo.Length > MaximumTrxFileSizeBytes;
    }

    private JudgeRunResult ConvertTrxToResult(
        string submissionId,
        string trxFilePath,
        ProcessResult testResult)
    {
        try
        {
            string json = TrxToJsonConverter.ConvertToJson(
                submissionId,
                status: "Completed",
                trxFilePath);
            return new JudgeRunResult(json, testResult.ExitCode);
        }
        catch (Exception exception)
        {
            ProcessResult converterFailureResult = _resultBuilder.CreateConverterFailureResult(testResult, exception);
            int exitCode = testResult.ExitCode == 0 ? 3 : testResult.ExitCode;
            return _resultBuilder.CreateRunnerErrorResult(submissionId, "trx_parse", converterFailureResult, exitCode);
        }
    }

    private JudgeRunResult HandleWorkspaceInitializationError(
        string submissionId,
        DirectoryNotFoundException exception)
    {
        ProcessResult errorResult = _resultBuilder.CreateWorkspaceInitializationErrorResult(exception);
        return _resultBuilder.CreateRunnerErrorResult(submissionId, "workspace_init", errorResult, exitCode: 1);
    }

    private void CleanupWorkspaceIfNeeded(WorkspacePaths? workspace, bool keepWorkspace)
    {
        if (workspace is not null)
        {
            _workspaceManager.CleanupWorkDirectory(workspace.WorkDir, keepWorkspace);
        }
    }

    private void LogProcessOutput(string phase, ProcessResult result)
    {
        Console.WriteLine($"----- dotnet {phase} STDOUT -----");
        Console.WriteLine(result.Stdout);
        Console.WriteLine($"----- dotnet {phase} STDERR -----");
        Console.WriteLine(result.Stderr);
        Console.WriteLine($"[JudgeRunner] {phase} ExitCode: {result.ExitCode}");
        Console.WriteLine($"[JudgeRunner] {phase} TimedOut: {result.TimedOut}");
    }

    private void TryDockerCleanup(string containerName, string workingDirectory)
    {
        TryKillContainer(containerName, workingDirectory);
        TryRemoveContainer(containerName, workingDirectory);
    }

    private void TryKillContainer(string containerName, string workingDirectory)
    {
        try
        {
            _processExecutor.Run(
                "docker",
                $"kill {containerName}",
                workingDirectory,
                TimeSpan.FromSeconds(5));
        }
        catch
        {
        }
    }

    private void TryRemoveContainer(string containerName, string workingDirectory)
    {
        try
        {
            _processExecutor.Run(
                "docker",
                $"rm -f {containerName}",
                workingDirectory,
                TimeSpan.FromSeconds(5));
        }
        catch
        {
        }
    }

    private static bool IndicatesResourceLimit(ProcessResult processResult)
    {
        if (processResult.ExitCode == ResourceLimitExitCode)
            return true;

        string combinedOutput = (processResult.Stderr ?? "") + "\n" + (processResult.Stdout ?? "");
        return combinedOutput.Contains("Out of memory", StringComparison.OrdinalIgnoreCase) ||
               combinedOutput.Contains("OutOfMemoryException", StringComparison.OrdinalIgnoreCase) ||
               combinedOutput.Contains("Killed", StringComparison.OrdinalIgnoreCase) ||
               combinedOutput.Contains("Test host process crashed", StringComparison.OrdinalIgnoreCase) ||
               combinedOutput.Contains("Test Run Aborted", StringComparison.OrdinalIgnoreCase);
    }

    private static string GenerateSubmissionId()
        => Guid.NewGuid().ToString("N");

    private static string BuildRestoreContainerName(string submissionId)
        => $"tracepoint-restore-{submissionId}";

    private static string BuildTestContainerName(string submissionId)
        => $"tracepoint-test-{submissionId}";
}

