using System.Text.Json.Serialization;

namespace Harbora.NodeAgent.Contracts;

/// <summary>
/// The desired state of one deployable unit on a node — a single container or a whole stack.
/// A deploy is a declaration, not a script: the node reconciles towards this and is free to reach
/// it any way it can, which is what makes re-sending the same spec harmless.
/// </summary>
public sealed record WorkloadSpec
{
    /// <summary>Stable across updates. The reconciliation key for everything below.</summary>
    public required string WorkloadId { get; init; }

    /// <summary>DNS-safe; becomes the container-name prefix and network alias base.</summary>
    public required string Name { get; init; }

    /// <summary>
    /// Owning tenant. Every resource the node creates for this workload is labelled with it, and
    /// cross-tenant reads are refused on that label — so isolation survives a control-plane bug
    /// that sends one tenant's workload id in another tenant's command.
    /// </summary>
    public required string TenantId { get; init; }

    public string? AppId { get; init; }
    public string? AppVersion { get; init; }
    public string? TemplateVersion { get; init; }

    public required IReadOnlyList<ContainerSpec> Containers { get; init; }

    public IReadOnlyList<NetworkSpec> Networks { get; init; } = [];
    public IReadOnlyList<VolumeSpec> Volumes { get; init; } = [];
    public IReadOnlyList<HttpRouteSpec> HttpRoutes { get; init; } = [];
    public IReadOnlyList<TcpRouteSpec> TcpRoutes { get; init; } = [];

    public UpgradeStrategy Upgrade { get; init; } = new();

    /// <summary>Architectures this workload's images support. Checked before anything is pulled.</summary>
    public IReadOnlyList<string> SupportedArchitectures { get; init; } = ["amd64", "arm64"];

    /// <summary>Semantic version below which this spec must be refused rather than half-understood.</summary>
    public string? MinimumAgentVersion { get; init; }

    /// <summary>
    /// Verbatim docker-compose document for stack deployments. Still subject to every mount,
    /// privilege and namespace policy the node applies to an ordinary container.
    /// </summary>
    public string? ComposeFile { get; init; }

    public IReadOnlyDictionary<string, string> Labels { get; init; } = new Dictionary<string, string>();
}

/// <summary>One container in a workload.</summary>
public sealed record ContainerSpec
{
    public required string Name { get; init; }
    public required ImageRef Image { get; init; }

    /// <summary>Overrides the image entrypoint's arguments. Passed as an argv array, never a shell string.</summary>
    public IReadOnlyList<string>? Command { get; init; }

    /// <summary>Non-secret environment. Secrets travel in <see cref="Secrets"/> and are handled separately.</summary>
    public IReadOnlyDictionary<string, string> Env { get; init; } = new Dictionary<string, string>();

    public IReadOnlyList<SecretSpec> Secrets { get; init; } = [];

    public IReadOnlyList<PortMapping> Ports { get; init; } = [];
    public IReadOnlyList<MountSpec> Mounts { get; init; } = [];
    public IReadOnlyList<string> NetworkAliases { get; init; } = [];

    public ResourceLimits Resources { get; init; } = new();
    public HealthCheckSpec? HealthCheck { get; init; }
    public RestartPolicySpec RestartPolicy { get; init; } = new();

    /// <summary>UID[:GID] to run as. Left unset the node applies its default non-root policy.</summary>
    public string? User { get; init; }

    public bool ReadOnlyRootFilesystem { get; init; }

    /// <summary>Linux capabilities to add. Every entry is checked against the node's deny-list.</summary>
    public IReadOnlyList<string> CapabilitiesAdd { get; init; } = [];
    public IReadOnlyList<string> CapabilitiesDrop { get; init; } = [];

    /// <summary>
    /// Refused outright unless the host has the privileged feature flag on and the command carries
    /// node-admin scope. A tenant-facing spec can never turn this on.
    /// </summary>
    public bool Privileged { get; init; }

    /// <summary>Refused unless privileged mode is enabled; see <see cref="Privileged"/>.</summary>
    public bool HostNetwork { get; init; }
    public bool HostPidNamespace { get; init; }

    public int StopGracePeriodSeconds { get; init; } = 10;
}

/// <summary>
/// An image, pinned. <see cref="Digest"/> is what is actually pulled — the tag is carried only so
/// logs and the panel can say something a human recognises. A spec with no digest is rejected:
/// "deploy the same thing you tested" is not a property a mutable tag can offer.
/// </summary>
public sealed record ImageRef
{
    /// <summary>Registry + repository, e.g. <c>docker.io/library/postgres</c>.</summary>
    public required string Repository { get; init; }

    /// <summary>Content digest, e.g. <c>sha256:…</c>. Required.</summary>
    public required string Digest { get; init; }

    /// <summary>Human-facing tag, e.g. <c>16-alpine</c>. Never used for resolution.</summary>
    public string? Tag { get; init; }

    /// <summary>Reference the runtime is asked to pull.</summary>
    public string PullReference => $"{Repository}@{Digest}";

    public override string ToString() => Tag is null ? PullReference : $"{Repository}:{Tag}@{Digest}";
}

/// <summary>
/// A secret bound into a container. <see cref="ToString"/> is overridden and the value is
/// annotated so neither a log line, an exception message nor a serialized diagnostic can leak it.
/// </summary>
public sealed record SecretSpec
{
    public required string Name { get; init; }

    /// <summary>The material itself. Only ever read at injection time.</summary>
    [JsonPropertyName("value")]
    public required string Value { get; init; }

    public SecretMount MountAs { get; init; } = SecretMount.Environment;

    /// <summary>Required when <see cref="MountAs"/> is <see cref="SecretMount.File"/>.</summary>
    public string? TargetPath { get; init; }

    public override string ToString() => $"SecretSpec {{ Name = {Name}, Value = ***, MountAs = {MountAs} }}";
}

public enum SecretMount
{
    /// <summary>Injected as an environment variable on the container (never on the command line).</summary>
    Environment = 0,

    /// <summary>Written to a tmpfs-backed file inside the container at <c>TargetPath</c>.</summary>
    File,
}

public sealed record PortMapping
{
    public required int ContainerPort { get; init; }

    /// <summary>Left null the node allocates from its declared range and reports what it chose.</summary>
    public int? HostPort { get; init; }

    public string Protocol { get; init; } = "tcp";

    /// <summary>
    /// When false (the default) the port is reachable only on the workload's private network.
    /// Publishing to the host is an explicit decision, so nothing becomes internet-facing by accident.
    /// </summary>
    public bool PublishToHost { get; init; }
}

public sealed record MountSpec
{
    /// <summary>Named Docker volume. Bind mounts of host paths are not expressible on purpose.</summary>
    public required string VolumeName { get; init; }
    public required string MountPath { get; init; }
    public bool ReadOnly { get; init; }
}

public sealed record VolumeSpec
{
    public required string Name { get; init; }
    public IReadOnlyDictionary<string, string> Labels { get; init; } = new Dictionary<string, string>();

    /// <summary>Kept when the workload is deleted unless the delete command says otherwise.</summary>
    public bool Persistent { get; init; } = true;
}

public sealed record NetworkSpec
{
    public required string Name { get; init; }

    /// <summary>No egress to the outside world. The default for a tenant's private network.</summary>
    public bool Internal { get; init; }

    public IReadOnlyDictionary<string, string> Labels { get; init; } = new Dictionary<string, string>();
}

public sealed record ResourceLimits
{
    /// <summary>Fractional cores, e.g. 0.5. Zero means "no limit" and is only honoured for admin workloads.</summary>
    public double CpuCores { get; init; }
    public long MemoryBytes { get; init; }
    public long MemoryReservationBytes { get; init; }

    /// <summary>Cap on processes. A fork bomb in one tenant's container must not take the node down.</summary>
    public int PidsLimit { get; init; } = 512;

    public long? DiskBytes { get; init; }
}

public sealed record HealthCheckSpec
{
    public HealthCheckKind Kind { get; init; } = HealthCheckKind.ContainerLiveness;
    public string? Path { get; init; }
    public int? Port { get; init; }
    public IReadOnlyList<string>? Command { get; init; }
    public int IntervalSeconds { get; init; } = 10;
    public int TimeoutSeconds { get; init; } = 5;
    public int Retries { get; init; } = 5;

    /// <summary>Grace before the first probe counts. Slow starters are not failures.</summary>
    public int StartPeriodSeconds { get; init; } = 15;

    public int ExpectedStatus { get; init; } = 200;
}

public enum HealthCheckKind
{
    /// <summary>The container is up and has not exited. The weakest signal, and the fallback.</summary>
    ContainerLiveness = 0,
    Http,
    Tcp,
    Command,
}

public sealed record RestartPolicySpec
{
    public RestartMode Mode { get; init; } = RestartMode.UnlessStopped;
    public int MaxRetries { get; init; }
}

public enum RestartMode
{
    No = 0,
    OnFailure,
    Always,
    UnlessStopped,
}

public sealed record UpgradeStrategy
{
    public UpgradeMode Mode { get; init; } = UpgradeMode.Recreate;

    /// <summary>Seconds to wait for post-deploy health before deciding the release failed.</summary>
    public int HealthGraceSeconds { get; init; } = 60;

    /// <summary>
    /// On by default. A release that never became healthy is worse than the one it replaced, and
    /// the node is the only party that can undo it quickly enough to matter.
    /// </summary>
    public bool AutoRollbackOnFailure { get; init; } = true;

    /// <summary>Rolling updates only: how many replicas may be down at once.</summary>
    public int MaxUnavailable { get; init; } = 1;

    public string? MigrationNotes { get; init; }
}

public enum UpgradeMode
{
    /// <summary>Stop the old container, start the new one. Brief downtime, no extra resources.</summary>
    Recreate = 0,

    /// <summary>Replace containers one at a time. Requires more than one replica to mean anything.</summary>
    RollingUpdate,

    /// <summary>Start the new version alongside, health-check it, then cut traffic over.</summary>
    BlueGreen,
}

public sealed record HttpRouteSpec
{
    public required string RouteId { get; init; }
    public required string Domain { get; init; }
    public string? PathPrefix { get; init; }
    public required string TargetContainer { get; init; }
    public required int TargetPort { get; init; }
    public bool Tls { get; init; } = true;
    public bool RedirectHttpToHttps { get; init; } = true;
    public bool WebSocket { get; init; }
    public bool StripPrefix { get; init; }
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();
}

public sealed record TcpRouteSpec
{
    public required string RouteId { get; init; }
    public required string TargetContainer { get; init; }
    public required int TargetPort { get; init; }

    /// <summary>
    /// Port on the Harbora TCP gateway, not on this host. The node dials the gateway outbound;
    /// nothing new is opened on the customer's firewall.
    /// </summary>
    public int? GatewayPort { get; init; }

    public bool Tls { get; init; }
    public IReadOnlyList<string> IpAllowlist { get; init; } = [];
}
