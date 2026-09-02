using Harbora.Domain.Common;

namespace Harbora.Domain.Services;

/// <summary>
/// A managed backing service (database/cache) that Harbora provisions as a container and
/// can attach to apps. Credentials are stored encrypted and surfaced as a connection string.
/// </summary>
public class ManagedService : BaseEntity
{
    public Guid WorkspaceId { get; set; }

    /// <summary>The environment this resource belongs to. Required (P2, 2026-08-17
    /// app-environment-management design) — see App.</summary>
    public Guid EnvironmentId { get; set; }
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

    /// <summary>
    /// Why the last provision attempt failed, or null when the last attempt did not fail (including
    /// "has never failed" and "failed, then a later attempt succeeded" — a success clears this).
    ///
    /// <para>
    /// The pattern this follows exists five times over already — <c>Deployment.ErrorMessage</c>,
    /// <c>CronRun.Error</c>, <c>Job.Error</c>, <c>Alert.LastError</c>,
    /// <c>NotificationDelivery.LastError</c> — and none of them landed on <c>ManagedService</c>, which
    /// is why a failed database used to say only <see cref="Status"/>'s bare <c>Failed</c> and nothing
    /// about why. Modelled closest on <c>Deployment.ErrorMessage</c>: both are set from the same catch
    /// block that also flips a status enum to its failed member, both are cleared the moment the same
    /// operation succeeds again, and both are shown on the resource's own page rather than only in a
    /// log a customer cannot read.
    /// </para>
    /// </summary>
    public string? ErrorMessage { get; set; }

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

    /// <summary>
    /// A Redis instance's <c>maxmemory-policy</c> — one of <see cref="Harbora.Infrastructure.Services.RedisMemoryPolicy.Choices"/>,
    /// or null for an instance that predates this and has never had one chosen. Null is distinct
    /// from <see cref="Harbora.Infrastructure.Services.RedisMemoryPolicy.NoEviction"/>: both produce
    /// the same behaviour today (Redis's own compiled default), but only the latter was a deliberate
    /// choice, and collapsing them into one value would make an untouched instance indistinguishable
    /// from one somebody chose "never evict" for. Meaningless for every other engine.
    /// </summary>
    public string? RedisEvictionPolicy { get; set; }

    /// <summary>
    /// A Redis instance's <c>maxmemory</c>, in bytes. Zero is Redis's own "no cap" — the state every
    /// instance is in until this is set. Meaningless for every other engine.
    /// </summary>
    public long RedisMaxMemoryBytes { get; set; }

    /// <summary>
    /// Whether this instance's own definition — <see cref="RedisEvictionPolicy"/>/
    /// <see cref="RedisMaxMemoryBytes"/>, and since 1.7 <see cref="PgVectorEnabled"/> too — has been
    /// recorded but not yet baked into the running container's own launch command or image. The
    /// <see cref="Apps.AppConfigGroup.HasUnpublishedChanges"/> idiom, reused rather than reinvented:
    /// Redis takes both its settings live through <c>CONFIG SET</c>, which is what makes that change
    /// reach the running instance the moment it is saved — but neither setting is written to a config
    /// file, so a plain restart (as opposed to a rebuild) starts the same container with the launch
    /// arguments it already had, silently reverting a live change that was never also baked in.
    /// <see cref="PgVectorEnabled"/> has no live-apply story at all — it changes which <em>image</em>
    /// the container runs, which only ever happens on a rebuild — so for it this flag is the only
    /// place "requested" and "actually running" can be told apart. This stays true even immediately
    /// after a successful live apply, and only clears once <c>ManagedServiceEngine.ProvisionAsync</c>
    /// recreates the container from the current row — the same moment that makes every other queued
    /// instance setting durable.
    /// </summary>
    public bool HasUnpublishedChanges { get; set; }

    /// <summary>
    /// Whether this PostgreSQL instance should run an image that carries the pgvector extension's
    /// files (1.7, pgvector-as-option plan). Meaningless for every other engine — pgvector is a
    /// PostgreSQL extension (<see cref="Harbora.Infrastructure.Services.DatabaseGrantSql.SupportsVectorExtension"/>).
    ///
    /// <para>
    /// Requested state, not observed state, exactly like <see cref="RedisEvictionPolicy"/>: the stock
    /// <c>postgres</c> image this platform otherwise runs carries no pgvector files at all, so turning
    /// this on only changes what the <em>next</em> rebuild pulls
    /// (<see cref="Harbora.Infrastructure.Services.PgVectorImage.For"/>) — see <see cref="HasUnpublishedChanges"/>.
    /// A logical database's own <c>CREATE EXTENSION</c> never consults this flag; it is answered by
    /// the engine directly, so the two can never quietly disagree.
    /// </para>
    /// </summary>
    public bool PgVectorEnabled { get; set; }

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

    /// <summary>Apps attached to this service (C1, 2026-08-22 config-delivery plan) — see
    /// <see cref="AppManagedService"/>.</summary>
    public ICollection<AppManagedService> Apps { get; set; } = new List<AppManagedService>();

    /// <summary>
    /// The logical databases inside this instance (D1, 2026-08-25 shared-databases plan) — see
    /// <see cref="ManagedServiceDatabase"/>. Empty for an instance whose engine has no database name
    /// at all (Redis, RabbitMQ, NATS), and for one created before this shipped and never migrated.
    /// </summary>
    public ICollection<ManagedServiceDatabase> Databases { get; set; } = new List<ManagedServiceDatabase>();
}
