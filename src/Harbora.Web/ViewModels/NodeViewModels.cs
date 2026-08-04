using Harbora.Domain.Common;
using Harbora.Domain.Nodes;

namespace Harbora.Web.ViewModels;

/// <summary>
/// One node as the fleet list shows it.
///
/// <para>
/// <see cref="Connected"/> is separate from <see cref="Status"/> on purpose: the row says what the
/// database last recorded, and the flag says whether this panel instance is holding the node's
/// socket right now. They disagree in exactly the case an operator needs to see — a node that is
/// online, but attached to a different replica, and therefore cannot be sent a command from here.
/// </para>
/// </summary>
public sealed record NodeRow(
    string NodeId,
    string Name,
    NodeStatus Status,
    string Health,
    bool Connected,
    bool Draining,
    bool Revoked,
    string AgentVersion,
    string? Region,
    string? Environment,
    string Architecture,
    string OsName,
    string ContainerRuntimeVersion,
    int CpuCores,
    long TotalMemoryBytes,
    long FreeMemoryBytes,
    long TotalDiskBytes,
    long FreeDiskBytes,
    double Load1,
    int RunningWorkloads,
    int ActiveDatabaseGrants,
    DateTimeOffset? LastHeartbeatAt,
    DateTimeOffset? CertificateNotAfter,
    IReadOnlyList<string> IpAddresses)
{
    /// <summary>
    /// The tone the status pill carries. A revoked node is an error rather than merely offline: one
    /// is a machine that went quiet, the other is a decision somebody made.
    /// </summary>
    public string Tone =>
        Revoked ? ViewModels.Tone.Error
        : Status == NodeStatus.Online && Health == "healthy" ? ViewModels.Tone.Ok
        : Status == NodeStatus.Online && Health == "degraded" ? ViewModels.Tone.Warn
        : Status == NodeStatus.Draining ? ViewModels.Tone.Warn
        : Status == NodeStatus.Offline ? ViewModels.Tone.Error
        : Status == NodeStatus.Pending ? ViewModels.Tone.Info
        : ViewModels.Tone.Idle;

    /// <summary>
    /// True when the credential expires within a fortnight. The agent renews at two thirds of its
    /// lifetime on its own, so this being lit means renewal has been failing — which is worth
    /// showing before it becomes an outage rather than after.
    /// </summary>
    public bool CertificateExpiringSoon(DateTimeOffset now) =>
        CertificateNotAfter is { } expiry && expiry - now < TimeSpan.FromDays(14);
}

/// <summary>An outstanding or spent enrollment token, as the list shows it.</summary>
public sealed record EnrollmentTokenRow(
    string Prefix,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? UsedAt,
    string? UsedByNodeId,
    DateTimeOffset? RevokedAt,
    string? NodeNameHint)
{
    public bool IsUsable(DateTimeOffset now) => UsedAt is null && RevokedAt is null && now < ExpiresAt;
}

/// <summary>The fleet page.</summary>
public sealed record NodeListViewModel(
    IReadOnlyList<NodeRow> Nodes,
    IReadOnlyList<EnrollmentTokenRow> Tokens,
    DateTimeOffset Now,
    string ControlPlaneUrl,
    /// <summary>
    /// Shown once, immediately after minting. The panel stores only a hash, so this is the single
    /// moment the value exists anywhere it can be read.
    /// </summary>
    string? NewToken = null,
    string? NewTokenInstallCommand = null)
{
    public int Online => Nodes.Count(n => n.Status == NodeStatus.Online);
    public int Offline => Nodes.Count(n => n.Status == NodeStatus.Offline);
    public int Draining => Nodes.Count(n => n.Draining);
    public int Workloads => Nodes.Sum(n => n.RunningWorkloads);
}

/// <summary>One command the panel sent to a node, as the detail page shows it.</summary>
public sealed record NodeCommandRow(
    string CommandId,
    string Command,
    NodeCommandStatus Status,
    DateTimeOffset IssuedAt,
    DateTimeOffset? CompletedAt,
    string? ErrorCode,
    string? ErrorMessage,
    bool IdempotentReplay,
    string? Actor)
{
    public string Tone => Status switch
    {
        NodeCommandStatus.Succeeded => ViewModels.Tone.Ok,
        NodeCommandStatus.Failed or NodeCommandStatus.Rejected => ViewModels.Tone.Error,
        NodeCommandStatus.TimedOut => ViewModels.Tone.Warn,
        NodeCommandStatus.Cancelled => ViewModels.Tone.Idle,
        _ => ViewModels.Tone.Info,
    };

    public TimeSpan? Duration => CompletedAt is { } done ? done - IssuedAt : null;
}

/// <summary>Something the node reported that nobody asked about.</summary>
public sealed record NodeEventRow(string Kind, string Message, string? WorkloadId, DateTimeOffset At)
{
    /// <summary>
    /// Derived from the kind rather than stored, so a node emitting a kind this panel has never
    /// heard of renders as neutral instead of throwing.
    /// </summary>
    public string Tone =>
        Kind.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
        Kind.Contains("rolled-back", StringComparison.OrdinalIgnoreCase) ? ViewModels.Tone.Error
        : Kind.StartsWith("pressure.", StringComparison.Ordinal) ||
          Kind.Contains("expir", StringComparison.OrdinalIgnoreCase) ? ViewModels.Tone.Warn
        : Kind.Contains("completed", StringComparison.OrdinalIgnoreCase) ? ViewModels.Tone.Ok
        : ViewModels.Tone.Info;
}

/// <summary>One node in full.</summary>
public sealed record NodeDetailViewModel(
    NodeRow Node,
    IReadOnlyList<string> Scopes,
    IReadOnlyList<string> SupportedCommands,
    IReadOnlyList<string> DatabaseEngines,
    bool PrivilegedModeEnabled,
    bool IsolatedWorkspaceSupported,
    string KernelVersion,
    string OsVersion,
    string? MachineFingerprint,
    DateTimeOffset? EnrolledAt,
    DateTimeOffset? LastConnectedAt,
    int CertificateGeneration,
    string? RevokedReason,
    IReadOnlyList<NodeCommandRow> Commands,
    IReadOnlyList<NodeEventRow> Events,
    DateTimeOffset Now,
    NodeSchedulingViewModel Scheduling);

/// <summary>
/// Whether the scheduler may place work on this node, and what is already there.
///
/// <para>
/// <see cref="ServerId"/> is null when the node is enrolled but not a scheduling target — either
/// because auto-registration is off, or because an operator detached it. Everything else is only
/// meaningful when it is set.
/// </para>
/// </summary>
public sealed record NodeSchedulingViewModel(
    Guid? ServerId,
    string Hostname,
    string Pool,
    ServerStatus Status,
    int Apps,
    int Services,
    long AllocatableMemoryBytes,
    long CommittedMemoryBytes,
    double AllocatableCpu,
    double CommittedCpu,
    bool AutoRegisterEnabled)
{
    public bool IsAttached => ServerId is not null;

    /// <summary>What the scheduler asks: is this node accepting placements right now.</summary>
    public bool AcceptsWork => IsAttached && Status == ServerStatus.Online;

    /// <summary>Blocked while anything is placed, because detaching would orphan it.</summary>
    public bool CanDetach => IsAttached && Apps + Services == 0;

    public double MemoryUsedRatio =>
        AllocatableMemoryBytes > 0 ? Math.Clamp((double)CommittedMemoryBytes / AllocatableMemoryBytes, 0, 1) : 0;

    public double CpuUsedRatio =>
        AllocatableCpu > 0 ? Math.Clamp(CommittedCpu / AllocatableCpu, 0, 1) : 0;
}
