using Harbora.Domain.Common;

namespace Harbora.Domain.Notifications;

/// <summary>
/// One already-rendered line waiting for this person's next digest or weekly-report email — N5
/// (2026-08-16 notification-system spec, "noise control").
///
/// <para>
/// <b>The digest must not become a way to lose things.</b> N1 made a delivery durable precisely so
/// this is possible: a row here is written the moment an event resolves to
/// <see cref="NotificationPreferenceMode.Digest"/> (or is downgraded to it by quiet hours), and it
/// sits in this table — surviving a restart, a crash, a digest job that does not run for a week —
/// until <c>NotificationDigestRunner</c> next runs and folds it into a <c>NotificationDelivery</c>.
/// There is no "the window passed" outcome: a window passing without the job running simply means the
/// next run has more to say, not that something was dropped. <see cref="DeliveryId"/> is null for
/// exactly as long as that is true.
/// </para>
///
/// <para>
/// <b>Once flushed, N1 owns the rest.</b> <see cref="DeliveryId"/> points at the
/// <c>NotificationDelivery</c> row the digest runner created for it — from that moment the row's own
/// fate (retry, backoff, <c>Suppressed</c>/<c>Failed</c> with a reason) is exactly like every other
/// delivery, visible in the same log. This entry's own job ends the moment it is folded in; it is not
/// re-flushed if that delivery later fails, the same way a <c>UserNotification</c> row is not
/// rewritten by a later, unrelated failure.
/// </para>
///
/// <para>
/// Unfiltered by EF and keyed by <see cref="UserId"/>, the same pattern <c>UserNotification</c> uses.
/// </para>
/// </summary>
public class NotificationDigestEntry : BaseEntity
{
    public Guid UserId { get; set; }

    public Guid WorkspaceId { get; set; }

    public AlertEvent EventType { get; set; }

    public AlertSeverity Severity { get; set; } = AlertSeverity.Info;

    /// <summary>Already rendered in this person's own <c>PreferredCulture</c> at write time — the
    /// same "a row records what was said at the time" choice <c>UserNotification</c> makes.</summary>
    public string Title { get; set; } = string.Empty;

    public string Body { get; set; } = string.Empty;

    /// <summary>
    /// Null while this entry is still waiting for a digest run. Set once
    /// <c>NotificationDigestRunner</c> folds it into a <c>NotificationDelivery</c> — never cleared
    /// again, and never re-used for a second delivery.
    /// </summary>
    public Guid? DeliveryId { get; set; }
}
