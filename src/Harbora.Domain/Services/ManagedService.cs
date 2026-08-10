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

    /// <summary>
    /// Whether connections to this service are actually encrypted, as of the last provision.
    ///
    /// Recorded rather than inferred from the engine type: a PostgreSQL container created before
    /// Harbora configured TLS is still running with ssl=off, and a page that reads "PostgreSQL can
    /// do TLS" as "this one does" tells the customer their connection is encrypted when it is not —
    /// which is worse than saying nothing at all.
    /// </summary>
    public bool TlsEnabled { get; set; }

    /// <summary>
    /// The resource plan this database runs under, or null for one created before databases had
    /// one. Null means unlimited, which is what every managed service was until now: apps were
    /// capped and sized, and a database could take the whole host.
    /// </summary>
    public string? InstanceSizeKey { get; set; }

    /// <summary>Hard memory ceiling handed to the container. Zero means none.</summary>
    public long MemoryLimitBytes { get; set; }

    /// <summary>The disk the chosen tier comes with, for the same reason as on an app. Zero is no ceiling.</summary>
    public long DiskLimitBytes { get; set; }

    /// <summary>CPU ceiling in cores. Zero means none.</summary>
    public double CpuLimit { get; set; }
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

    /// <summary>
    /// Whether this database was running at the moment its workspace was suspended for an empty
    /// balance, so a top-up starts back what the suspension stopped and nothing else. The counterpart
    /// of <c>App.WasRunningAtSuspension</c>, and it exists for the sharper version of the same
    /// reason: a database the customer stopped themselves must not come back and start spending the
    /// money they just put in, and a database the suspension stopped must, because everything else
    /// they are paying to restart needs it.
    ///
    /// <para>
    /// This is the <b>only</b> record that a stopped database was ever running — nothing else
    /// distinguishes it from one the customer stopped — which is why the suspension writes it before
    /// touching a container, and why a resumption that could not start a database keeps it set.
    /// </para>
    /// </summary>
    public bool WasRunningAtSuspension { get; set; }
}
