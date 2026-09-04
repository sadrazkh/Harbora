using Harbora.Domain.Common;

namespace Harbora.Domain.Services;

/// <summary>
/// The most recent attempt to measure how far behind its primary one read replica is (3.2, round-2
/// market-gaps plan). One row per replica <see cref="ManagedService"/> — created the first time
/// <c>Harbora.Infrastructure.Backups.ReplicationLagMonitor</c> checks it.
///
/// <para>
/// This is the ONLY source of truth for a replica's lag, and it is deliberately kept apart from
/// <see cref="ManagedService.Status"/>: a customer's real question — "if I route a read here right
/// now, how stale is the answer" — has more than one way to be wrong, and none of them may collapse
/// into a green dot or a bare zero. <see cref="LastSuccessAt"/> only ever moves forward on a
/// measurement that actually got an answer back from the replica; a failed or a stale attempt leaves
/// it exactly where it was, which is what lets the panel say "unknown" instead of repeating an old
/// number as if it were current. See
/// <c>Harbora.Infrastructure.Backups.ReplicationLagPresenter</c> for how this row is turned into the
/// sentence the panel actually shows — the same split
/// <c>Harbora.Domain.Backups.WalArchivingStatus</c>/<c>PitrRecoveryWindow</c> already draws for
/// point-in-time recovery's own "is it actually working right now" question.
/// </para>
/// </summary>
public class ReplicationLagStatus : BaseEntity
{
    public Guid WorkspaceId { get; set; }

    /// <summary>The replica this status describes — never the primary.</summary>
    public Guid ManagedServiceId { get; set; }

    /// <summary>When lag was last checked at all, successful or not.</summary>
    public DateTimeOffset? LastAttemptAt { get; set; }

    /// <summary>When a check last got a real answer back from the replica.</summary>
    public DateTimeOffset? LastSuccessAt { get; set; }

    /// <summary>
    /// The lag, in seconds, as of <see cref="LastSuccessAt"/> — or null when the query itself
    /// succeeded but PostgreSQL had no answer to give (<c>pg_last_xact_replay_timestamp()</c> returns
    /// NULL on a standby that has not yet replayed a single transaction with a commit timestamp, which
    /// is the ordinary state for a few moments right after <c>pg_basebackup</c> finishes). Null here
    /// is "the engine said it does not know", never "zero" — the query returning a real, small number
    /// is the only way this field is ever actually zero.
    /// </summary>
    public double? LagSeconds { get; set; }

    /// <summary>Checks since the last success. Reset to zero the moment one succeeds.</summary>
    public int ConsecutiveFailures { get; set; }

    /// <summary>The most recent failure's own words, or null when the last attempt succeeded.</summary>
    public string? LastError { get; set; }
}
