using Harbora.NodeAgent.Contracts;

namespace Harbora.NodeAgent.Runtime;

/// <summary>
/// The one seam through which the agent touches a container runtime.
///
/// <para>
/// Every method takes structured arguments — argv arrays, named volumes, typed limits — and none
/// takes a command line. That is the whole point: with no string-to-shell path in the abstraction,
/// there is no place for a crafted workload name to become an instruction, and adding a second
/// runtime later is implementing this interface rather than auditing call sites again.
/// </para>
/// </summary>
public interface IContainerRuntime
{
    /// <summary>Runtime name and version, plus whether it is reachable at all.</summary>
    Task<RuntimeInfo> GetInfoAsync(CancellationToken ct);

    /// <summary>Pull by digest reference. Progress lines are already redacted by the caller's sink.</summary>
    Task PullImageAsync(string reference, IProgress<string>? log, CancellationToken ct);

    /// <summary>The digest an image reference resolves to locally, or null when it is not present.</summary>
    Task<string?> ResolveDigestAsync(string reference, CancellationToken ct);

    /// <summary>Architectures the local copy of an image supports, empty when unknown.</summary>
    Task<IReadOnlyList<string>> GetImageArchitecturesAsync(string reference, CancellationToken ct);

    Task<IReadOnlyList<RuntimeContainer>> ListContainersAsync(
        IReadOnlyDictionary<string, string>? labelFilter, bool includeStopped, CancellationToken ct);

    Task<RuntimeContainer?> InspectAsync(string idOrName, CancellationToken ct);

    /// <summary>
    /// One resource sample for a running container, or null when the runtime would not give one.
    ///
    /// Null rather than a zeroed reading: the stats call fails intermittently on a container that is
    /// starting or going away, and a caller that treats "could not read" as "reading of zero" draws
    /// an idle application at exactly the moment something is wrong with it.
    /// </summary>
    Task<RuntimeContainerStats?> GetStatsAsync(string idOrName, CancellationToken ct);

    Task<string> CreateAndStartAsync(ContainerCreateRequest request, CancellationToken ct);

    Task StopAsync(string idOrName, int gracePeriodSeconds, CancellationToken ct);
    Task StartAsync(string idOrName, CancellationToken ct);
    Task RestartAsync(string idOrName, CancellationToken ct);
    Task RemoveAsync(string idOrName, bool force, CancellationToken ct);

    Task<string> GetLogsAsync(string idOrName, int tailLines, CancellationToken ct);
    Task StreamLogsAsync(string idOrName, int tailLines, IProgress<string> sink, CancellationToken ct);

    Task EnsureNetworkAsync(NetworkSpec spec, IReadOnlyDictionary<string, string> labels, CancellationToken ct);
    Task RemoveNetworkAsync(string name, CancellationToken ct);
    Task ConnectToNetworkAsync(string containerIdOrName, string network, IReadOnlyList<string> aliases, CancellationToken ct);

    Task EnsureVolumeAsync(string name, IReadOnlyDictionary<string, string> labels, CancellationToken ct);
    Task RemoveVolumeAsync(string name, CancellationToken ct);
    Task<bool> VolumeExistsAsync(string name, CancellationToken ct);

    /// <summary>Run a helper container to completion and return its exit code. Removed afterwards.</summary>
    Task<int> RunOneOffAsync(OneOffRequest request, IProgress<string>? log, CancellationToken ct);

    /// <summary>
    /// Execute an argv array inside a running container. Used for engine-native credential
    /// management (<c>psql</c>, <c>mysql</c>, <c>mongosh</c>, <c>redis-cli</c>) where there is no
    /// API but there is a first-party client already in the image.
    /// </summary>
    Task<ExecResult> ExecAsync(
        string containerIdOrName, IReadOnlyList<string> argv,
        IReadOnlyDictionary<string, string>? env, string? stdin, CancellationToken ct);
}

public sealed record RuntimeInfo(
    string Name,
    string Version,
    string ApiVersion,
    int ContainersRunning,
    bool Available,
    string? Error = null);

public sealed record RuntimeContainer(
    string Id,
    string Name,
    string Image,
    string? ImageDigest,
    string State,
    string Status,
    bool? Healthy,
    int RestartCount,
    DateTimeOffset? StartedAt,
    IReadOnlyDictionary<string, string> Labels,
    IReadOnlyDictionary<int, int> PublishedPorts,
    IReadOnlyDictionary<string, string> NetworkIpAddresses);

/// <summary>
/// One resource sample. Every figure is nullable and stays null when the runtime did not report it,
/// so "not measured" survives the whole way to the screen instead of arriving there as a zero.
/// </summary>
public sealed record RuntimeContainerStats(
    double? CpuPercent,
    long? MemoryUsedBytes,
    long? MemoryLimitBytes,
    long? NetRxBytes,
    long? NetTxBytes);

/// <summary>
/// A container about to be created. Everything the runtime needs, with the security-relevant knobs
/// stated explicitly rather than defaulted by the daemon.
/// </summary>
public sealed record ContainerCreateRequest
{
    public required string Name { get; init; }
    public required string ImageReference { get; init; }

    public IReadOnlyList<string>? Command { get; init; }

    /// <summary>Includes injected secrets. Never rendered into the command line.</summary>
    public IReadOnlyDictionary<string, string> Env { get; init; } = new Dictionary<string, string>();

    public IReadOnlyDictionary<string, string> Labels { get; init; } = new Dictionary<string, string>();

    public string? Network { get; init; }
    public IReadOnlyList<string> NetworkAliases { get; init; } = [];

    /// <summary>Named volumes only — the type system offers no way to express a host bind.</summary>
    public IReadOnlyList<VolumeMount> Mounts { get; init; } = [];

    /// <summary>Files written into a tmpfs inside the container. How file-mounted secrets arrive.</summary>
    public IReadOnlyList<TmpfsFile> TmpfsFiles { get; init; } = [];

    public IReadOnlyList<PortPublication> Ports { get; init; } = [];

    public ResourceLimits Resources { get; init; } = new();
    public HealthCheckSpec? HealthCheck { get; init; }
    public RestartPolicySpec RestartPolicy { get; init; } = new();

    public string? User { get; init; }
    public bool ReadOnlyRootFilesystem { get; init; }
    public IReadOnlyList<string> CapabilitiesAdd { get; init; } = [];
    public IReadOnlyList<string> CapabilitiesDrop { get; init; } = ["ALL"];

    /// <summary>
    /// Already policy-checked by the time it reaches the runtime. The runtime does not re-decide;
    /// it would be a second place for the answer to be different.
    /// </summary>
    public bool Privileged { get; init; }
    public bool HostNetwork { get; init; }
    public bool HostPidNamespace { get; init; }

    /// <summary>Blocks the container from gaining privileges through setuid binaries.</summary>
    public bool NoNewPrivileges { get; init; } = true;

    public int StopGracePeriodSeconds { get; init; } = 10;
}

public sealed record VolumeMount(string VolumeName, string MountPath, bool ReadOnly);

public sealed record TmpfsFile(string Path, string Content, bool Executable = false);

public sealed record PortPublication(int ContainerPort, int? HostPort, string Protocol);

public sealed record OneOffRequest
{
    public required string ImageReference { get; init; }
    public required IReadOnlyList<string> Command { get; init; }
    public IReadOnlyList<VolumeMount> Mounts { get; init; } = [];
    public IReadOnlyDictionary<string, string> Env { get; init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, string> Labels { get; init; } = new Dictionary<string, string>();
    public string? Network { get; init; }
    public string? WorkingDirectory { get; init; }
    public ResourceLimits Resources { get; init; } = new();
    public int TimeoutSeconds { get; init; } = 3600;
}

public sealed record ExecResult(int ExitCode, string Stdout, string Stderr);

/// <summary>Thrown when the runtime refuses an operation for a reason the caller can map to a contract code.</summary>
public sealed class ContainerRuntimeException(NodeErrorCode code, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public NodeErrorCode Code { get; } = code;
    public bool Retryable { get; init; }
}
