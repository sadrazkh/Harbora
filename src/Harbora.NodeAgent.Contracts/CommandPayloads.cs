namespace Harbora.NodeAgent.Contracts;

// Payload shapes for the commands whose arguments are too small to deserve their own file.
// Every one of them is a record so an unknown extra field from a newer control plane is ignored
// rather than fatal — the contract grows forward, not sideways.

/// <summary>Payload of <c>DeployWorkload</c> and <c>UpdateWorkload</c>.</summary>
public sealed record DeployWorkloadRequest
{
    public required WorkloadSpec Spec { get; init; }

    /// <summary>Manifest the spec was rendered from, when it came from a Ready App template.</summary>
    public AppManifest? Manifest { get; init; }

    /// <summary>Run validation and report what would happen, without touching the runtime.</summary>
    public bool DryRun { get; init; }
}

public sealed record DeployWorkloadResult
{
    public required string WorkloadId { get; init; }
    public required bool Deployed { get; init; }
    public required WorkloadStatus Status { get; init; }

    /// <summary>Host ports the node allocated, keyed by <c>container:containerPort</c>.</summary>
    public IReadOnlyDictionary<string, int> AllocatedPorts { get; init; } = new Dictionary<string, int>();

    /// <summary>Digest actually pulled per container. Proof of what is running, not what was asked for.</summary>
    public IReadOnlyDictionary<string, string> ResolvedDigests { get; init; } = new Dictionary<string, string>();

    public bool RolledBack { get; init; }
    public string? PreviousVersion { get; init; }
    public long PullDurationMs { get; init; }
    public long DeployDurationMs { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>Payload of the workload lifecycle verbs that need nothing but an id.</summary>
public sealed record WorkloadRequest
{
    public required string WorkloadId { get; init; }
    public required string TenantId { get; init; }
}

/// <summary>Payload of <c>DeleteWorkload</c>.</summary>
public sealed record DeleteWorkloadRequest
{
    public required string WorkloadId { get; init; }
    public required string TenantId { get; init; }

    /// <summary>
    /// Off by default. Deleting a workload and deleting the data it held are different decisions,
    /// and only one of them is reversible.
    /// </summary>
    public bool DeleteVolumes { get; init; }

    public bool Force { get; init; }
}

/// <summary>
/// Payload of <c>ListWorkloads</c>.
///
/// <para>
/// Read-only, and strictly weaker than repeating <c>GetWorkloadStatus</c> for every id the caller
/// already knows — which is exactly why it is safe to add: it tells a control plane what a node
/// holds without letting it do anything new to it. The control plane needs it to retire the
/// containers a previous release left behind, which it cannot do by guessing names.
/// </para>
/// </summary>
public sealed record ListWorkloadsRequest
{
    /// <summary>Required. A node answers only for the tenant the command acts for.</summary>
    public required string TenantId { get; init; }

    /// <summary>Optional filter on the workload's <c>appId</c>, for a control plane that wants one app's set.</summary>
    public string? AppId { get; init; }

    /// <summary>Include the live container state, which costs one runtime inspect per workload.</summary>
    public bool IncludeStatus { get; init; } = true;
}

public sealed record ListWorkloadsResult
{
    public required IReadOnlyList<WorkloadSummary> Workloads { get; init; }
}

/// <summary>One workload as the node knows it.</summary>
public sealed record WorkloadSummary
{
    public required string WorkloadId { get; init; }
    public required string Name { get; init; }
    public required string TenantId { get; init; }
    public string? AppId { get; init; }
    public string? AppVersion { get; init; }
    public required string ReleaseId { get; init; }
    public DateTimeOffset DeployedAt { get; init; }

    /// <summary>Labels the spec carried, so a caller can match its own bookkeeping.</summary>
    public IReadOnlyDictionary<string, string> Labels { get; init; } = new Dictionary<string, string>();

    /// <summary>Null when the caller asked not to pay for a status read.</summary>
    public WorkloadStatus? Status { get; init; }
}

/// <summary>Payload of <c>StreamLogs</c>.</summary>
public sealed record StreamLogsRequest
{
    public required string WorkloadId { get; init; }
    public required string TenantId { get; init; }
    public string? ContainerName { get; init; }
    public int TailLines { get; init; } = 200;

    /// <summary>False returns a snapshot and completes; true streams until cancelled or timed out.</summary>
    public bool Follow { get; init; } = true;
}

/// <summary>Payload of <c>CreateNetwork</c> / <c>DeleteNetwork</c>.</summary>
public sealed record NetworkRequest
{
    public required string TenantId { get; init; }
    public required NetworkSpec Network { get; init; }
}

/// <summary>Payload of <c>CreateVolume</c>.</summary>
public sealed record VolumeRequest
{
    public required string TenantId { get; init; }
    public required VolumeSpec Volume { get; init; }
}

/// <summary>Payload of <c>SnapshotVolume</c>.</summary>
public sealed record SnapshotVolumeRequest
{
    public required string TenantId { get; init; }
    public required string VolumeName { get; init; }
    public required string SnapshotId { get; init; }

    /// <summary>Workload to stop for the duration, when a consistent copy needs it stopped.</summary>
    public string? QuiesceWorkloadId { get; init; }

    /// <summary>Compress the archive. Cheaper to move, slower to make.</summary>
    public bool Compress { get; init; } = true;
}

public sealed record SnapshotVolumeResult
{
    public required string SnapshotId { get; init; }
    public required string Path { get; init; }
    public long SizeBytes { get; init; }
    public required string Sha256 { get; init; }
    public long DurationMs { get; init; }
}

/// <summary>Payload of <c>RestoreVolume</c>.</summary>
public sealed record RestoreVolumeRequest
{
    public required string TenantId { get; init; }
    public required string VolumeName { get; init; }
    public required string SnapshotId { get; init; }

    /// <summary>Verified before anything is written. A corrupt archive must not half-restore.</summary>
    public required string ExpectedSha256 { get; init; }

    public string? QuiesceWorkloadId { get; init; }
}

/// <summary>Payload of <c>RegisterHttpRoute</c>.</summary>
public sealed record RegisterHttpRouteRequest
{
    public required string TenantId { get; init; }
    public required string WorkloadId { get; init; }
    public required HttpRouteSpec Route { get; init; }
}

/// <summary>Payload of <c>RegisterTcpRoute</c>.</summary>
public sealed record RegisterTcpRouteRequest
{
    public required string TenantId { get; init; }
    public required string WorkloadId { get; init; }
    public required TcpRouteSpec Route { get; init; }
}

/// <summary>Payload of <c>RemoveRoute</c>.</summary>
public sealed record RemoveRouteRequest
{
    public required string TenantId { get; init; }
    public required string RouteId { get; init; }
}

public sealed record RouteResult
{
    public required string RouteId { get; init; }
    public required bool Active { get; init; }
    public string? PublicEndpoint { get; init; }
}

/// <summary>Simple confirmation for verbs whose only interesting answer is "yes, and here is the state".</summary>
public sealed record AcknowledgedResult
{
    public required bool Applied { get; init; }
    public string? Detail { get; init; }

    /// <summary>True when the desired state already held and nothing had to change.</summary>
    public bool NoOp { get; init; }
}
