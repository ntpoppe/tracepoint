public sealed class DockerCommandBuilder
{
    private readonly string _uid;
    private readonly string _gid;
    private readonly string _image;
    private const string NugetMountPath = "/nuget";

    public DockerCommandBuilder(
        string uid = "1000",
        string gid = "1000",
        string image = "mcr.microsoft.com/dotnet/sdk:10.0")
    {
        _uid = uid;
        _gid = gid;
        _image = image;
    }

    public string BuildRestoreArguments(string containerName, string workDir, string nugetCacheRoot)
    {
        return
            $"run --rm " +
            $"--name {containerName} " +
            $"--user {_uid}:{_gid} " +
            $"--cpus=1 " +
            $"--memory=512m " +
            $"--memory-swap=512m " +
            $"--pids-limit=128 " +
            $"-v \"{workDir}:/workspace\" " +
            $"-v \"{nugetCacheRoot}:{NugetMountPath}\" " +
            $"-e NUGET_PACKAGES={NugetMountPath} " +
            $"-e DOTNET_SKIP_WORKLOAD_INTEGRITY_CHECK=1 " +
            $"-e DOTNET_CLI_TELEMETRY_OPTOUT=1 " +
            $"-e DOTNET_NOLOGO=1 " +
            $"-w /workspace " +
            $"{_image} " +
            $"dotnet restore";
    }

    public string BuildTestArguments(string containerName, string workDir, string nugetCacheRoot, string trxFileName)
    {
        return
            $"run --rm " +
            $"--name {containerName} " +
            $"--network none " +
            $"--init " +
            $"--user {_uid}:{_gid} " +
            $"--cpus=1 " +
            $"--memory=512m " +
            $"--memory-swap=512m " +
            $"--pids-limit=128 " +
            $"-v \"{workDir}:/workspace\" " +
            $"-v \"{nugetCacheRoot}:{NugetMountPath}\" " +
            $"-e NUGET_PACKAGES={NugetMountPath} " +
            $"-e DOTNET_SKIP_WORKLOAD_INTEGRITY_CHECK=1 " +
            $"-e DOTNET_CLI_TELEMETRY_OPTOUT=1 " +
            $"-e DOTNET_NOLOGO=1 " +
            $"-w /workspace " +
            $"{_image} " +
            $"dotnet test --no-restore --logger \"trx;LogFileName={trxFileName}\"";
    }
}


