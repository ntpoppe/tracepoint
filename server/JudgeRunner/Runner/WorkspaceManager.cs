public sealed class WorkspaceManager
{
    private readonly string _workRoot;

    public WorkspaceManager(string? workRoot = null)
    {
        _workRoot = workRoot ?? Path.Combine(Path.GetTempPath(), "tracepoint-workspaces");
    }

    public WorkspacePaths CreateWorkspace(string submissionId)
    {
        string repoRoot = FindRepoRoot();
        string templateDir = Path.Combine(repoRoot, "judge-template");

        if (!Directory.Exists(templateDir))
        {
            throw new DirectoryNotFoundException($"judge-template directory not found at: {templateDir}");
        }

        Directory.CreateDirectory(_workRoot);

        string workDir = Path.Combine(_workRoot, submissionId);
        Directory.CreateDirectory(workDir);

        Console.WriteLine($"[JudgeRunner] Workspace: {workDir}");
        Console.WriteLine($"[JudgeRunner] Copying template from: {templateDir}");

        CopyDirectory(templateDir, workDir);

        string nugetCacheRoot = Path.Combine(_workRoot, "_nuget-cache");
        Directory.CreateDirectory(nugetCacheRoot);

        return new WorkspacePaths(
            RepoRoot: repoRoot,
            TemplateDir: templateDir,
            WorkRoot: _workRoot,
            WorkDir: workDir,
            NugetCacheRoot: nugetCacheRoot
        );
    }

    public void CleanupWorkspace(WorkspacePaths workspace, bool keep)
    {
        if (keep)
        {
            Console.WriteLine($"[JudgeRunner] Keeping workspace (per --keep): {workspace.WorkDir}");
            return;
        }

        try
        {
            Directory.Delete(workspace.WorkDir, recursive: true);
            Console.WriteLine($"[JudgeRunner] Cleaned workspace: {workspace.WorkDir}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[JudgeRunner] WARNING: Failed to cleanup workspace: {ex.Message}");
        }
    }

    public string? FindTrxFile(string testResultsRoot, string preferredName)
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
}

public sealed record WorkspacePaths(
    string RepoRoot,
    string TemplateDir,
    string WorkRoot,
    string WorkDir,
    string NugetCacheRoot
);


