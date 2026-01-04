namespace JudgeRunner.Models;

/// <summary>
/// Represents the paths and metadata for a workspace directory used in judge executions,
/// including the repository root, template directory, and NuGet cache directory.
/// </summary>
public sealed record WorkspacePaths(
    string RepoRoot,
    string TemplateDir,
    string WorkRoot,
    string WorkDir,
    string NugetCacheRoot
);

