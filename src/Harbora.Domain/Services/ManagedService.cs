using Harbora.Domain.Common;

namespace Harbora.Domain.Services;

/// <summary>
/// A managed backing service (database/cache) that Harbora provisions as a container and
/// can attach to apps. Credentials are stored encrypted and surfaced as a connection string.
/// </summary>
public class ManagedService : BaseEntity
{
    public Guid WorkspaceId { get; set; }

    /// <summary>The environment this resource belongs to; nullable during the transition (see App).</summary>
    public Guid? EnvironmentId { get; set; }
    public Harbora.Domain.Projects.Environment? Environment { get; set; }
    public Guid ServerId { get; set; }

    public string Name { get; set; } = string.Empty;
    public ManagedServiceType Type { get; set; }
    /// <summary>
    /// The version asked for. Not defaulted to "latest": a database that silently jumps a major
    /// version when its container is recreated will refuse to start on the data directory it already
    /// has, and that is discovered at the worst possible moment.
    /// </summary>
    public string Version { get; set; } = string.Empty;
    public ServiceStatus Status { get; set; } = ServiceStatus.Provisioning;

    public string ContainerName { get; set; } = string.Empty;
    public int InternalPort { get; set; }

    public string Username { get; set; } = string.Empty;
    /// <summary>Encrypted at rest.</summary>
    public string EncryptedPassword { get; set; } = string.Empty;
    public string DatabaseName { get; set; } = string.Empty;

    public string VolumeName { get; set; } = string.Empty;

    /// <summary>
    /// Size of the data volume when it was last measured, and when that was. Stored rather than read
    /// on demand because measuring means walking the whole directory in a container — cheap for a
    /// small database, minutes for a large one. A figure with no timestamp beside it is the kind of
    /// number people trust for longer than they should.
    /// </summary>
    public long? StorageBytes { get; set; }
    public DateTimeOffset? StorageMeasuredAt { get; set; }

    /// <summary>
    /// The image tag the container is actually running, as opposed to <see cref="Version"/>, which is
    /// what was asked for. They differ after someone pulls a moving tag, and a database that quietly
    /// changed major version is the one failure here nobody recovers from quickly.
    /// </summary>
    public string? RunningImage { get; set; }
}
