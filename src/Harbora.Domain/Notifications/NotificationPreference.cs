using Harbora.Domain.Common;

namespace Harbora.Domain.Notifications;

/// <summary>
/// One person's explicit choice for one (event type, channel) pair — N5 (2026-08-16
/// notification-system spec, "noise control").
///
/// <para>
/// <b>An absent row means the default</b> (see <see cref="NotificationPreferenceDefaults"/>), not
/// "off". Storing a row per user × event × channel eagerly the day this table shipped would mean a
/// new <c>AlertEvent</c> added next month silently arrives switched off for everybody who registered
/// before it existed — the exact shape of defect this codebase keeps removing (see
/// <c>NotificationService.Matches</c>'s own warning about a forgotten arm). Only a person who actually
/// opened the preferences page and changed something gets a row.
/// </para>
///
/// <para>
/// <b>Critical events are re-routable, not mutable.</b> <c>NotificationEventClass.IsCritical</c>
/// marks the events doc 09 §3 calls C-class — the last warning before something stops, the same
/// argument <c>NotificationService.cs:111-120</c> already makes for <c>AlertEvent.LowBalance</c>. A
/// row for a critical event may set one channel to <see cref="NotificationPreferenceMode.Off"/> only
/// if the other still resolves to <see cref="NotificationPreferenceMode.Immediate"/> —
/// <c>NotificationPreferenceService.SetAsync</c> is where that is enforced, not here; this type is a
/// plain record of what was asked for.
/// </para>
///
/// <para>
/// Unfiltered by EF and keyed by <see cref="UserId"/> alone, the same pattern
/// <c>UserNotification</c> and <c>ApiToken</c> already use (doc 14 §3): a preference is a person's,
/// not a workspace's, and a workspace-scoped filter would still merge every member's rows into one
/// set.
/// </para>
///
/// <para>
/// <b>No retention knob.</b> Unlike every other table N1-N4 added, this one is not history — its
/// cardinality is bounded by users × event types × channels, the same reason <c>Alert</c> and
/// <c>User</c> carry none either. See <c>RetentionOptions</c>'s own doc comment on why a config table
/// is not swept.
/// </para>
/// </summary>
public class NotificationPreference : BaseEntity
{
    public Guid UserId { get; set; }

    public AlertEvent EventType { get; set; }

    public NotificationChannel Channel { get; set; }

    public NotificationPreferenceMode Mode { get; set; }
}
