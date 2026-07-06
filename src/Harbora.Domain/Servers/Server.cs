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

    /// <summary>SHA-256 hash of the agent bearer token (remote servers only).</summary>
    public string? AgentTokenHash { get; set; }

    public ServerStatus Status { get; set; } = ServerStatus.Unknown;
    public DateTimeOffset? LastHeartbeatAt { get; set; }

    // Last reported capacity snapshot (updated by monitoring engine).
    public int CpuCores { get; set; }
    public long TotalMemoryBytes { get; set; }
    public long TotalDiskBytes { get; set; }
    public string? DockerVersion { get; set; }
}
