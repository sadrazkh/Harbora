using Harbora.Domain.Common;

namespace Harbora.Domain.Nodes;

/// <summary>Where a node is in its life, from the control plane's point of view.</summary>
public enum NodeStatus
{
    /// <summary>Enrolled but has never opened a channel.</summary>
    Pending = 0,

    Online = 1,

    /// <summary>Enrolled and healthy last we heard, but not currently connected.</summary>
    Offline = 2,

    /// <summary>Connected and deliberately taking no new work.</summary>
    Draining = 3,

    /// <summary>Its credential has been withdrawn. It cannot renew and must be re-enrolled.</summary>
    Revoked = 4,
}

/// <summary>
/// A server running the Harbora node agent.
///
/// <para>
/// Deliberately a separate aggregate from <see cref="Servers.Server"/> rather than more columns on
/// it. A Server is "somewhere the platform can run containers", and the existing inbound agent is
/// one way to reach one; a Node is a specific relationship with a specific protocol, credential and
/// session. Folding them together would mean every Server row carrying a dozen columns that are null
/// for the ones enrolled the old way — and would make the two migration paths hard to tell apart.
/// <see cref="ServerId"/> links them when a node is also a scheduling target.
/// </para>
/// </summary>
public class Node : BaseEntity
{
    /// <summary>The id the agent knows itself by. Stable across re-enrollment and agent updates.</summary>
    public string NodeId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Hash of the machine id the agent reported. Lets a re-install of the same box be recognised
    /// instead of silently becoming a second node competing for the same containers.
    /// </summary>
    public string? MachineFingerprint { get; set; }

    public NodeStatus Status { get; set; } = NodeStatus.Pending;

    /// <summary>Last health word the node reported: healthy, degraded, draining, unhealthy.</summary>
    public string Health { get; set; } = "unknown";

    public bool Draining { get; set; }

    // --- what the node is ---

    public string AgentVersion { get; set; } = string.Empty;
    public int ProtocolVersion { get; set; } = 1;

    public string? Region { get; set; }
    public string? Environment { get; set; }

    /// <summary>JSON object of placement labels. Kept opaque so new hints need no migration.</summary>
    public string LabelsJson { get; set; } = "{}";

    public string OsName { get; set; } = string.Empty;
    public string OsVersion { get; set; } = string.Empty;
    public string KernelVersion { get; set; } = string.Empty;
    public string Architecture { get; set; } = string.Empty;

    public string ContainerRuntime { get; set; } = string.Empty;
    public string ContainerRuntimeVersion { get; set; } = string.Empty;

    public int CpuCores { get; set; }
    public long TotalMemoryBytes { get; set; }
    public long TotalDiskBytes { get; set; }
    public long FreeDiskBytes { get; set; }
    public long FreeMemoryBytes { get; set; }
    public double Load1 { get; set; }

    public string IpAddressesJson { get; set; } = "[]";

    /// <summary>
    /// The full inventory and capability reports as the node sent them.
    ///
    /// <para>
    /// Stored verbatim alongside the columns above rather than instead of them. The columns are what
    /// the scheduler queries; the JSON is what an operator reads when a node behaves in a way the
    /// columns do not explain, and it survives the contract growing fields this schema has no place
    /// for yet.
    /// </para>
    /// </summary>
    public string InventoryJson { get; set; } = "{}";
    public string CapabilitiesJson { get; set; } = "{}";

    public int RunningWorkloads { get; set; }
    public int ActiveDatabaseGrants { get; set; }
    public int ActiveTunnels { get; set; }

    // --- credential ---

    public string CertificateThumbprint { get; set; } = string.Empty;
    public string CertificateSerial { get; set; } = string.Empty;
    public DateTimeOffset? CertificateNotAfter { get; set; }
    public int CertificateGeneration { get; set; }

    /// <summary>JSON array of the scopes this node's credential carries.</summary>
    public string GrantedScopesJson { get; set; } = "[]";

    public DateTimeOffset? RevokedAt { get; set; }
    public string? RevokedReason { get; set; }
    public Guid? RevokedByUserId { get; set; }

    // --- session ---

    /// <summary>Opaque token the node presents to resume its session after a reconnect.</summary>
    public string? ResumeToken { get; set; }

    /// <summary>Highest sequence the control plane has durably processed from this node.</summary>
    public long LastReceivedSequence { get; set; }

    /// <summary>Highest sequence the control plane has sent to this node.</summary>
    public long LastSentSequence { get; set; }

    public DateTimeOffset? EnrolledAt { get; set; }
    public DateTimeOffset? LastConnectedAt { get; set; }
    public DateTimeOffset? LastHeartbeatAt { get; set; }
    public DateTimeOffset? DisconnectedAt { get; set; }

    /// <summary>Scheduling target this node backs, when it is one.</summary>
    public Guid? ServerId { get; set; }

    /// <summary>Whether the credential is usable right now.</summary>
    public bool IsRevoked => RevokedAt is not null;

    /// <summary>
    /// Whether the node has gone quiet. Three missed heartbeats rather than one: a single missed
    /// beat is a network hiccup, and marking a node offline for one would flap the whole fleet
    /// every time the panel's own network blinked.
    /// </summary>
    public bool IsStale(DateTimeOffset now, TimeSpan heartbeatInterval) =>
        LastHeartbeatAt is null || now - LastHeartbeatAt > heartbeatInterval * 3;
}
