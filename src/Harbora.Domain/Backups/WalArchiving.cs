using Harbora.Domain.Common;

namespace Harbora.Domain.Backups;

/// <summary>
/// Point-in-time recovery health for one PostgreSQL <see cref="Harbora.Domain.Services.ManagedService"/>
/// instance (3.1, round-2 market-gaps plan). One row per instance that has ever had WAL archiving
/// turned on; created the first time <c>WalArchivingService.SetEnabledAsync</c> enables it.
///
/// <para>
/// This is the ONLY source of truth for whether PITR is actually working, deliberately kept separate
/// from <see cref="Harbora.Domain.Services.ManagedService.PitrEnabled"/> (the requested setting) and
/// from <see cref="Harbora.Domain.Services.ManagedService.HasUnpublishedChanges"/> (whether that
/// setting has reached the running container yet). A customer's real question — "as of right now,
/// what is the most recent moment I could restore to" — has three ways to be wrong that must never
/// collapse into one flag: never configured, configured but not yet applied, and applied but
/// currently failing to ship segments. <c>Harbora.Infrastructure.Backups.PitrRecoveryWindow</c> is
/// where those three states, plus the newest base backup, are turned into the sentence the panel
/// actually shows.
/// </para>
///
/// <para>
/// <see cref="LastSuccessAt"/> only ever moves forward on an archiving run that actually shipped a
/// segment. A run that fails updates <see cref="LastAttemptAt"/>, <see cref="ConsecutiveFailures"/>
/// and <see cref="LastError"/> and leaves <see cref="LastSuccessAt"/> exactly where it was — which is
/// what makes the recoverable window shrink (relative to "now") the moment archiving starts failing,
/// rather than silently keep claiming a window that stopped growing hours ago.
/// </para>
/// </summary>
public class WalArchivingStatus : BaseEntity
{
    public Guid WorkspaceId { get; set; }
    public Guid ManagedServiceId { get; set; }

    /// <summary>When archiving last ran at all, successful or not.</summary>
    public DateTimeOffset? LastAttemptAt { get; set; }

    /// <summary>
    /// When archiving last shipped a segment successfully. The latest point-in-time this instance can
    /// actually be recovered to, never advanced on a failed or skipped run.
    /// </summary>
    public DateTimeOffset? LastSuccessAt { get; set; }

    /// <summary>Runs since the last success. Reset to zero the moment one succeeds.</summary>
    public int ConsecutiveFailures { get; set; }

    /// <summary>The most recent failure's own words, or null when the last attempt succeeded.</summary>
    public string? LastError { get; set; }

    /// <summary>Running count of segments ever shipped, for the instance's own history — not a
    /// figure retention prunes; it only ever grows.</summary>
    public long SegmentsArchived { get; set; }
}

/// <summary>
/// One WAL segment Harbora has copied off a PostgreSQL instance into object storage (3.1, round-2
/// market-gaps plan) — the record that makes WAL retention possible without either orphaning a base
/// backup or keeping segments for ever. A row is written only after
/// <see cref="Harbora.Application.Abstractions.IBackupStorage.PutFileAsync"/> has confirmed the bytes
/// actually reached the destination, mirroring the engine-first-then-row law
/// <c>LogicalDatabaseService</c>'s own class doc states for logical databases: a row here that the
/// destination does not have is the same defect class, just one WAL segment at a time.
/// </summary>
public class WalSegment : BaseEntity
{
    public Guid WorkspaceId { get; set; }
    public Guid ManagedServiceId { get; set; }
    public Guid DestinationId { get; set; }
    public BackupDestination? Destination { get; set; }

    /// <summary>The segment's own file name, exactly as PostgreSQL's <c>archive_command</c> named it
    /// (<c>%f</c>) — never reconstructed or guessed at here.</summary>
    public string FileName { get; set; } = string.Empty;

    public DateTimeOffset ArchivedAt { get; set; }

    /// <summary>Where <see cref="Destination"/> actually stored it — what a restore reads back.</summary>
    public string ArtifactPath { get; set; } = string.Empty;

    public long SizeBytes { get; set; }
}
