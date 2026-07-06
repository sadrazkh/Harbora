namespace Harbora.Application.Abstractions;

/// <summary>
/// The single seam through which the platform touches the container runtime. Every Docker
/// operation goes through here (backed by Docker.DotNet) — no shell strings anywhere else,
/// which removes a whole class of command-injection risks.
/// </summary>
public interface IDockerEngine
{
    Task<string> BuildImageAsync(DockerBuildRequest request, IProgress<string> log, CancellationToken ct);
    Task PullImageAsync(string image, IProgress<string> log, CancellationToken ct);

    Task<string> RunContainerAsync(DockerRunRequest request, CancellationToken ct);
    Task StopContainerAsync(string containerId, CancellationToken ct);
    Task RemoveContainerAsync(string containerId, bool force, CancellationToken ct);
    Task RestartContainerAsync(string containerId, CancellationToken ct);

    Task StreamLogsAsync(string containerId, IProgress<string> sink, CancellationToken ct);

    Task<IReadOnlyList<ContainerInfo>> ListContainersAsync(string? labelFilter, CancellationToken ct);
    Task<ContainerStats?> GetStatsAsync(string containerId, CancellationToken ct);

    Task EnsureNetworkAsync(string name, CancellationToken ct);
    Task EnsureVolumeAsync(string name, CancellationToken ct);
    Task RemoveVolumeAsync(string name, CancellationToken ct);

    /// <summary>
    /// Runs a short-lived container to completion (used by the backup engine to tar/untar
    /// volumes) and returns its exit code. The container is removed afterwards.
    /// </summary>
    Task<int> RunOneOffAsync(DockerOneOffRequest request, IProgress<string>? log, CancellationToken ct);

    Task<HostInfo> GetHostInfoAsync(CancellationToken ct);
}

public record DockerOneOffRequest(
    string Image,
    IReadOnlyList<string> Command,
    IReadOnlyList<(string Source, string Target, bool ReadOnly)> Binds);

public record DockerBuildRequest(
    string ContextPath,
    string Dockerfile,
    string ImageTag,
    IReadOnlyDictionary<string, string> BuildArgs);

public record DockerRunRequest(
    string Image,
    string ContainerName,
    string NetworkName,
    IReadOnlyDictionary<string, string> Env,
    IReadOnlyDictionary<string, string> Labels,
    IReadOnlyList<(string VolumeName, string MountPath, bool ReadOnly)> Volumes,
    int? ContainerPort,
    long MemoryLimitBytes,
    double CpuLimit,
    string? HealthCheckPath,
    IReadOnlyList<string>? Command = null);

public record ContainerInfo(string Id, string Name, string Image, string State, string Status, IReadOnlyDictionary<string, string> Labels);
public record ContainerStats(double CpuPercent, long MemoryUsedBytes, long MemoryLimitBytes, long NetRxBytes, long NetTxBytes);
public record HostInfo(int CpuCores, long TotalMemoryBytes, long TotalDiskBytes, long FreeDiskBytes, string DockerVersion, int ContainersRunning);
