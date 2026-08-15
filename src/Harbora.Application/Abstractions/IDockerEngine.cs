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

    /// <summary>Tagged images present on this node, optionally filtered to those whose tag starts with a prefix.</summary>
    Task<IReadOnlyList<ImageInfo>> ListImagesAsync(string? tagPrefix, CancellationToken ct);

    /// <summary>
    /// Whether an image reference resolves on this node. Artifact rollback re-releases a prior
    /// image, so this is what makes "instant rollback" checkable before we promise it.
    /// </summary>
    Task<bool> ImageExistsAsync(string imageRef, CancellationToken ct);

    /// <summary>Best-effort image removal. An image still in use by a container is left alone.</summary>
    Task RemoveImageAsync(string imageRef, CancellationToken ct);

    /// <summary>
    /// The container ports an image declares (its <c>EXPOSE</c> lines). Empty when it declares none,
    /// which is common and means "we cannot tell" rather than "it listens nowhere".
    /// </summary>
    Task<IReadOnlyList<int>> GetImagePortsAsync(string imageRef, CancellationToken ct);

    Task<string> RunContainerAsync(DockerRunRequest request, CancellationToken ct);
    Task StopContainerAsync(string containerId, CancellationToken ct);
    Task RemoveContainerAsync(string containerId, bool force, CancellationToken ct);
    Task RestartContainerAsync(string containerId, CancellationToken ct);

    Task StreamLogsAsync(string containerId, IProgress<string> sink, CancellationToken ct);

    /// <summary>Non-following snapshot of the last <paramref name="tailLines"/> log lines.</summary>
    Task<string> GetLogsAsync(string containerId, int tailLines, CancellationToken ct);

    Task<IReadOnlyList<ContainerInfo>> ListContainersAsync(string? labelFilter, CancellationToken ct);
    Task<ContainerStats?> GetStatsAsync(string containerId, CancellationToken ct);

    /// <summary>One container in detail, or null when the engine has no such container.</summary>
    Task<ContainerDetail?> InspectAsync(string containerNameOrId, CancellationToken ct);

    Task EnsureNetworkAsync(string name, CancellationToken ct);
    /// <summary>Attach an existing container to a network (idempotent) — used to give Traefik ingress into per-tenant networks.</summary>
    Task ConnectNetworkAsync(string containerNameOrId, string network, CancellationToken ct);
    Task EnsureVolumeAsync(string name, CancellationToken ct);
    Task RemoveVolumeAsync(string name, CancellationToken ct);

    /// <summary>
    /// Runs a short-lived container to completion (used by the backup engine to tar/untar
    /// volumes) and returns its exit code. The container is removed afterwards.
    /// </summary>
    Task<int> RunOneOffAsync(DockerOneOffRequest request, IProgress<string>? log, CancellationToken ct);

    Task<HostInfo> GetHostInfoAsync(CancellationToken ct);

    /// <summary>
    /// Attaches a shell to a running container and returns the two-way stream.
    ///
    /// Behind the seam on purpose, and not because the panel might one day speak to something other
    /// than docker: an engine that cannot offer this must be able to <b>say so</b>. A node engine
    /// throws <see cref="NotSupportedException"/> rather than returning something that looks like a
    /// terminal and never carries a byte, which is the failure this codebase keeps finding.
    /// </summary>
    Task<IContainerExec> ExecAsync(
        string containerId, IReadOnlyList<string> command, int columns, int rows, CancellationToken ct);
}

/// <summary>
/// A live shell inside a container: bytes in, bytes out, and a size.
///
/// Deliberately bytes rather than lines. A terminal is not line-oriented — a keystroke matters
/// before the newline, and the escape sequences that draw the screen are not lines at all.
/// </summary>
public interface IContainerExec : IAsyncDisposable
{
    /// <summary>Reads whatever the shell has produced. Zero means it has ended.</summary>
    Task<int> ReadAsync(Memory<byte> buffer, CancellationToken ct);

    /// <summary>Sends keystrokes.</summary>
    Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct);

    /// <summary>Tells the shell how big the window is, so anything full-screen draws correctly.</summary>
    Task ResizeAsync(uint columns, uint rows, CancellationToken ct);
}

public record DockerOneOffRequest(
    string Image,
    IReadOnlyList<string> Command,
    IReadOnlyList<(string Source, string Target, bool ReadOnly)> Binds,
    /// <summary>Environment for the helper — a database dump needs the password here.</summary>
    IReadOnlyDictionary<string, string>? Env = null,
    /// <summary>
    /// Docker network mode. Tar helpers need none, but a helper that must reach another container
    /// does: <c>container:harbora-panel</c> gives it exactly the panel's own connectivity, so a
    /// hostname that works for the panel works here without restating any network configuration.
    /// </summary>
    string? NetworkMode = null);

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
    IReadOnlyList<string>? Command = null,
    int? PublishToHostPort = null,
    /// <summary>
    /// Extra DNS names this container answers to on its network. Compose stacks need them: a service
    /// written to connect to <c>db:5432</c> must resolve <c>db</c>, not the versioned container name
    /// that lets old and new coexist during a cutover.
    /// </summary>
    IReadOnlyList<string>? NetworkAliases = null,
    /// <summary>Additional TCP container-to-host port publications for multi-protocol services.</summary>
    IReadOnlyDictionary<int, int>? AdditionalPublishedPorts = null);

public record ContainerInfo(string Id, string Name, string Image, string State, string Status, IReadOnlyDictionary<string, string> Labels);

/// <summary>
/// One container, asked about directly.
///
/// <see cref="ContainerInfo"/> is what a listing can cheaply say — a state and a status line. This is
/// what an inspect adds: which image is actually running, how long it has been up, how often it has
/// restarted, and whether its health check is passing.
///
/// Every figure that a runtime may decline to report is nullable, and stays null rather than
/// defaulting. The reason is the same one <c>RuntimeContainerStats</c> states on the node side: a
/// zero is a specific claim, and making it because nobody answered is the panel asserting something
/// it does not know. <paramref name="Healthy"/> in particular is null when no health check is
/// configured — that is "we were not told how to ask", not "failing".
/// </summary>
public record ContainerDetail(
    string Id,
    string Name,
    string Image,
    string? ImageDigest,
    string State,
    string Status,
    bool? Healthy,
    int? RestartCount,
    DateTimeOffset? StartedAt);

public record ImageInfo(string Id, string Tag, DateTimeOffset CreatedAt, long SizeBytes);
public record ContainerStats(double CpuPercent, long MemoryUsedBytes, long MemoryLimitBytes, long NetRxBytes, long NetTxBytes);
/// <param name="Architecture">
/// What the host runs — <c>amd64</c>, <c>arm64</c>. Optional and last, so an agent that does not
/// report it still deserialises. Unknown stays unknown rather than defaulting to a guess: a wrong
/// guess here refuses images that would have run perfectly well.
/// </param>
public record HostInfo(
    int CpuCores, long TotalMemoryBytes, long TotalDiskBytes, long FreeDiskBytes,
    string DockerVersion, int ContainersRunning, string? Architecture = null);
