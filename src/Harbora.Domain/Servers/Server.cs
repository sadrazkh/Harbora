using Harbora.Domain.Common;

namespace Harbora.Domain.Servers;

/// <summary>
/// A host that runs containers. For the MVP there is exactly one "local" server whose
/// agent runs in-process. The model already carries the fields a remote agent needs so
/// multi-server is a data + transport change, not a schema change.
/// </summary>
public class Server : BaseEntity
{
    public string Name { get; set; } = string.Empty;
    public string Hostname { get; set; } = "localhost";
    public bool IsLocal { get; set; } = true;

    /// <summary>Base address of the remote agent (null for the in-process local agent).</summary>
    public string? AgentEndpoint { get; set; }

    /// <summary>Encrypted agent bearer token the panel presents on outbound calls (remote servers only).</summary>
    public string? AgentTokenHash { get; set; }

    /// <summary>When true, the panel also presents a client certificate (mTLS) to the agent.</summary>
    public bool AgentUseMtls { get; set; }

    /// <summary>Encrypted base64 PKCS#12 (PFX, empty export password) client certificate for mTLS.</summary>
    public string? AgentClientCertPfx { get; set; }

    public ServerStatus Status { get; set; } = ServerStatus.Unknown;
    public DateTimeOffset? LastHeartbeatAt { get; set; }

    /// <summary>Pool tag used by plans to restrict placement (e.g. "shared", "premium"). Empty = general.</summary>
    public string Pool { get; set; } = string.Empty;

    /// <summary>Fraction of memory kept free as headroom for the host + overhead (0–1, e.g. 0.15).</summary>
    public double ReservedMemoryRatio { get; set; } = 0.15;

    /// <summary>Allowed CPU overcommit factor (1.0 = no overcommit, 2.0 = 2x).</summary>
    public double CpuOvercommitFactor { get; set; } = 1.0;

    // Last reported capacity snapshot (updated by monitoring engine).
    public int CpuCores { get; set; }
    public long TotalMemoryBytes { get; set; }
    public long TotalDiskBytes { get; set; }
    public string? DockerVersion { get; set; }
}
