using Harbora.Domain.Common;

namespace Harbora.Domain.Monitoring;

/// <summary>
/// One thing that fired and, eventually, one of three ways it stopped firing.
///
/// <para>
/// Per condition, not per rule (2026-08-16 monitoring-alerting spec §M4, decision 1): a single rule
/// can opt into a per-app memory threshold and workspace disk warnings at once, and if both breach
/// that is two of these rows, not one that can only say "still open" for whichever recovers last.
/// <see cref="WorkspaceId"/> + <see cref="Condition"/> + <see cref="SubjectRef"/> together are what
/// tells two incidents apart; a second breach of the same condition and subject while one is already
/// open refreshes that row (see <c>IncidentService.OpenAsync</c>) rather than opening a second.
/// </para>
/// </summary>
public class AlertIncident : BaseEntity
{
    public Guid WorkspaceId { get; set; }

    /// <summary>
    /// What raised this. Reuses <see cref="AlertEvent"/> — the same vocabulary the notification
    /// router matches on — rather than inventing a parallel enum. <see cref="AlertEvent.Test"/> never
    /// appears here: a test message is a person checking a channel, not a condition.
    /// </summary>
    public AlertEvent Condition { get; set; }

    /// <summary>
    /// What the condition is about: an app id for a threshold breach or a crash, a server id for a
    /// disk warning, a certificate host for SSL, a deployment id for a failed deploy, a backup id for
    /// a failed backup. Null for a workspace-wide condition (a low balance) — <see cref="WorkspaceId"/>
    /// is already the whole subject there.
    /// </summary>
    public string? SubjectRef { get; set; }

    public AlertSeverity Severity { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;

    public DateTimeOffset OpenedAt { get; set; }

    /// <summary>
    /// Refreshed every time the still-breaching condition is observed again, so a standing problem
    /// reads as one row for as long as it lasts rather than one row per collector tick.
    /// </summary>
    public DateTimeOffset LastObservedAt { get; set; }

    public DateTimeOffset? ClosedAt { get; set; }

    /// <summary>
    /// Null while open. Once set, which of the three ways this closed — see
    /// <see cref="IncidentClosedReason"/>. An incident that only ever says "closed" has lost the one
    /// interesting fact about it, which matters most for a deploy or backup failure: neither ever
    /// clears on its own, so this is never <see cref="IncidentClosedReason.Resolved"/> for either.
    /// </summary>
    public IncidentClosedReason? ClosedReason { get; set; }
}
