using Harbora.Modules.Backup.Contracts;
using Harbora.Domain.Common;

namespace Harbora.Modules.Backup.Domain;

/// <summary>
/// A rule: back up <em>this target</em> into <em>that repository</em>, on this schedule, keeping
/// versions for this long.
/// </summary>
public class BackupPolicy : BaseEntity
{
    public Guid WorkspaceId { get; set; }

    public string Name { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public Guid RepositoryId { get; set; }
    public BackupRepository? Repository { get; set; }

    public BackupTargetType TargetType { get; set; }

    /// <summary>
    /// Loose reference to the target: an app id, a docker volume name, a managed-service id, a
    /// directory. Loose because the target kinds have no common key, and a foreign key per kind
    /// would mean a nullable column per kind.
    /// </summary>
    public string TargetRef { get; set; } = string.Empty;

    /// <summary>Cron expression. Validated on write — an unparseable schedule silently never fires.</summary>
    public string Schedule { get; set; } = "0 3 * * *";

    /// <summary>
    /// IANA timezone the schedule is read in.
    ///
    /// <para>
    /// Stored explicitly rather than assuming the server's. "3am" means the tenant's 3am, and a
    /// server that moves region, or a host whose clock is UTC while its users are not, otherwise
    /// silently shifts every backup window.
    /// </para>
    /// </summary>
    public string Timezone { get; set; } = "UTC";

    public RetentionPolicy Retention { get; set; } = new();

    /// <summary>Engine compression level, or null for the engine's default.</summary>
    public string? CompressionAlgorithm { get; set; }

    /// <summary>Off only for a repository whose storage is already encrypted and trusted.</summary>
    public bool EncryptionEnabled { get; set; } = true;

    /// <summary>Newline-separated globs. Empty means everything under the source.</summary>
    public string? IncludePatterns { get; set; }
    public string? ExcludePatterns { get; set; }

    /// <summary>
    /// Commands run in the target's own container before/after the snapshot, for applications that
    /// need to be quiesced to produce a consistent copy.
    ///
    /// <para>
    /// Executed as an argument list against the container, never through a shell on the host. A hook
    /// is a convenience for consistency, not an arbitrary-code channel into the panel.
    /// </para>
    /// </summary>
    public string? PreBackupHook { get; set; }
    public string? PostBackupHook { get; set; }

    /// <summary>Raise an alert when no successful snapshot has landed within this many hours.</summary>
    public int? AlertAfterHoursWithoutSuccess { get; set; } = 26;

    public DateTimeOffset? LastRunAt { get; set; }
    public DateTimeOffset? LastSuccessAt { get; set; }
    public DateTimeOffset? NextRunAt { get; set; }
}

/// <summary>
/// How many versions to keep, and for how long.
///
/// <para>
/// An owned value on the policy rather than a table of its own: retention has no identity apart from
/// the policy it belongs to, and is always read and written with it.
/// </para>
/// <para>
/// The tiers are independent and additive — a snapshot survives if ANY tier still wants it. Keeping
/// 24 hourly and 30 daily does not mean 30 snapshots total; it means the most recent 24 hours are
/// dense and the previous month is sparse. See <see cref="RetentionCalculator"/>.
/// </para>
/// </summary>
public class RetentionPolicy
{
    /// <summary>
    /// Always keep at least this many of the newest snapshots, whatever the tiers below say.
    ///
    /// <para>
    /// The floor that makes retention safe to misconfigure. Every tier set to zero would otherwise
    /// mean the next prune deletes everything, and the moment that is discovered is the moment
    /// someone needs a restore.
    /// </para>
    /// </summary>
    public int KeepLatest { get; set; } = 3;

    public int KeepHourly { get; set; } = 24;
    public int KeepDaily { get; set; } = 30;
    public int KeepWeekly { get; set; } = 8;
    public int KeepMonthly { get; set; } = 12;
    public int KeepYearly { get; set; } = 3;

    /// <summary>Drop snapshots older than this regardless of tier. Null disables the ceiling.</summary>
    public int? MaximumAgeDays { get; set; }

    /// <summary>
    /// Stop taking new snapshots once the repository exceeds this. Null disables the cap.
    ///
    /// <para>
    /// Deliberately refuses new snapshots rather than deleting old ones to make room: a size cap
    /// that prunes is a size cap that silently destroys history the moment someone backs up a large
    /// file by mistake.
    /// </para>
    /// </summary>
    public long? MaximumRepositorySizeBytes { get; set; }
}
