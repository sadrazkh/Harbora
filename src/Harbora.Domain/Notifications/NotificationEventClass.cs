using Harbora.Domain.Common;

namespace Harbora.Domain.Notifications;

/// <summary>
/// Doc 09 §3's C/O split: whether an <see cref="AlertEvent"/> is critical (re-routable, never
/// silenceable) or optional (fully mutable, including <see cref="NotificationPreferenceMode.Off"/> and
/// quiet-hours digest downgrade) — N5, 2026-08-16 notification-system spec, "noise control".
///
/// <para>
/// <c>NotificationService.cs:111-120</c> already made this argument once, for one event:
/// <c>AlertEvent.LowBalance</c> answers <c>true</c> for every <c>Alert</c> rule rather than reading an
/// opt-in flag, because it is the last message the platform sends before a workspace's apps stop and
/// an install where somebody had quietly unticked it would deliver silence and a suspension. This
/// class generalises that same reasoning to every event a preference can now be set for.
/// </para>
///
/// <para>
/// <b>The default is <c>true</c> (critical), and that is deliberate — the opposite direction from
/// <c>NotificationService.Matches</c>'s own default.</b> There the safe answer is <c>false</c>
/// ("delivered to nobody") because the trap is a forgotten event spamming everyone. Here the trap runs
/// the other way: a forgotten event silently becoming muteable is how a customer stops hearing about
/// something that matters. Appending an <see cref="AlertEvent"/> without a same-day line here costs
/// nothing but the freedom to mute it — the safe direction to fail in.
/// </para>
/// </summary>
public static class NotificationEventClass
{
    public static bool IsCritical(AlertEvent evt) => evt switch
    {
        // ResourceUsageHigh in doc 09's own catalog — a per-app threshold the workspace configured
        // for itself, closer to "tell me if this crosses a line I picked" than "something is broken".
        AlertEvent.ThresholdBreached => false,

        // Same reasoning as ThresholdBreached, and for the same reason: a workspace nearing its own
        // plan cap is "tell me if this crosses a line I picked" (the OnQuotaWarning checkbox), not
        // "something is broken" — the class this switch's default is written to protect.
        AlertEvent.QuotaWarning => false,

        _ => true
    };
}
