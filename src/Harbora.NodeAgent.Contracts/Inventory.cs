namespace Harbora.NodeAgent.Contracts;

/// <summary>
/// Everything the control plane needs to schedule onto this node. Sent at enrollment, on every
/// connect, and whenever a value materially changes — never on the heartbeat, which stays small
/// enough to send every few seconds without being a bandwidth decision.
/// </summary>
public sealed record NodeInventory
{
    public required string NodeName { get; init; }
    public required string Hostname { get; init; }

    public required string OsName { get; init; }
    public required string OsVersion { get; init; }
    public required string KernelVersion { get; init; }

    /// <summary>Normalised: <c>amd64</c> or <c>arm64</c>. Anything else is reported verbatim and refused work.</summary>
    public required string Architecture { get; init; }

    public required string ContainerRuntime { get; init; }
    public required string ContainerRuntimeVersion { get; init; }

    public int CpuCores { get; init; }
    public long TotalMemoryBytes { get; init; }
    public long TotalDiskBytes { get; init; }
    public long FreeDiskBytes { get; init; }

    /// <summary>Addresses worth telling the control plane about; loopback and link-local are excluded.</summary>
    public IReadOnlyList<string> IpAddresses { get; init; } = [];

    public string? Region { get; init; }
    public string? Environment { get; init; }
    public IReadOnlyDictionary<string, string> Labels { get; init; } = new Dictionary<string, string>();

    /// <summary>Host port range the node will allocate from when a workload needs to be published.</summary>
    public PortRange AvailablePortRange { get; init; } = new(30000, 32767);

    /// <summary>Ports already taken on the host, so the scheduler does not propose a collision.</summary>
    public IReadOnlyList<int> UsedPorts { get; init; } = [];

    /// <summary>Docker networks the agent manages on this node.</summary>
    public IReadOnlyList<string> ManagedNetworks { get; init; } = [];

    public StorageCapacity Storage { get; init; } = new(0, 0, "/var/lib/harbora-node");
}

public sealed record PortRange(int Start, int End);

public sealed record StorageCapacity(long TotalBytes, long FreeBytes, string DataRoot);

/// <summary>
/// What this agent build can actually do. The control plane must consult this rather than infer
/// from the version string: a node may have Docker but no rootless support, or an ancient kernel
/// that rules out an isolated workspace, and only the node can tell.
/// </summary>
public sealed record NodeCapabilities
{
    public required string AgentVersion { get; init; }
    public required IReadOnlyList<int> SupportedProtocolVersions { get; init; }

    /// <summary>Commands this build implements. A subset of <see cref="NodeCommandCatalog.All"/>.</summary>
    public required IReadOnlyList<string> SupportedCommands { get; init; }

    /// <summary>Database engines this node can mint and revoke access grants for.</summary>
    public IReadOnlyList<string> SupportedDatabaseEngines { get; init; } = [];

    public bool SupportsComposeStacks { get; init; }
    public bool SupportsRollingUpdate { get; init; }
    public bool SupportsVolumeSnapshots { get; init; }
    public bool SupportsTcpTunnel { get; init; }

    /// <summary>
    /// True when this build can serve HTTP ingress over an outbound tunnel — what makes a node
    /// behind NAT usable at all. Separate from <see cref="SupportsTcpTunnel"/> because the two
    /// differ in what may be reached through them: a database tunnel dials one target fixed when it
    /// registered, an ingress tunnel dials whichever published port the gateway names, and nothing
    /// but a published port.
    /// </summary>
    public bool SupportsHttpIngressTunnel { get; init; }

    /// <summary>True when an isolated per-tenant Docker workspace can be provisioned here.</summary>
    public bool SupportsIsolatedDockerWorkspace { get; init; }

    /// <summary>
    /// True only when an admin explicitly enabled the privileged feature flag on this host. Left
    /// false, every spec asking for privileged mode is refused — including one from an admin.
    /// </summary>
    public bool PrivilegedModeEnabled { get; init; }

    public bool SupportsSelfUpdate { get; init; }
}

/// <summary>Point-in-time view of what is running, answered by <c>GetWorkloadStatus</c>.</summary>
public sealed record WorkloadStatus
{
    public required string WorkloadId { get; init; }
    public required string State { get; init; }
    public string? ContainerId { get; init; }
    public string? ImageDigest { get; init; }
    public string? AppVersion { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public int RestartCount { get; init; }
    public bool Healthy { get; init; }
    public string? LastError { get; init; }
    public IReadOnlyList<ContainerStatus> Containers { get; init; } = [];
}

public sealed record ContainerStatus(
    string Name,
    string ContainerId,
    string State,
    string Image,
    bool Healthy,
    int RestartCount);

/// <summary>
/// A resource reading for one workload, answered by <c>GetWorkloadStats</c>.
///
/// Every figure is nullable and stays null when the runtime did not report it. A control plane that
/// reads a missing value as zero draws an idle application, which is the opposite of what an
/// unmeasured one means — and the panel had no per-container figures from a node at all until this
/// existed, so every chart for a node-hosted app was empty and looked like silence rather than like
/// an unanswered question.
/// </summary>
public sealed record WorkloadStats
{
    public required string WorkloadId { get; init; }
    public DateTimeOffset SampledAt { get; init; }
    public IReadOnlyList<WorkloadContainerStats> Containers { get; init; } = [];
}

/// <summary>One container inside a workload. Named apart from the control plane's own
/// <c>ContainerStats</c>, which is a different shape for a different purpose.</summary>
public sealed record WorkloadContainerStats
{
    public required string Name { get; init; }
    public string? ContainerId { get; init; }
    public double? CpuPercent { get; init; }
    public long? MemoryUsedBytes { get; init; }
    public long? MemoryLimitBytes { get; init; }
    public long? NetRxBytes { get; init; }
    public long? NetTxBytes { get; init; }
}
