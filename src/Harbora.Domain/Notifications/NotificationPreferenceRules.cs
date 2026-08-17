using Harbora.Domain.Common;

namespace Harbora.Domain.Notifications;

/// <summary>
/// Pure invariants over a person's resolved preferences for one event — N5, 2026-08-16
/// notification-system spec, "noise control". No clock, no database: the same reason
/// <c>RetentionRule</c> and <c>CertificateAlert</c> are pure, this is the part of the platform that
/// decides whether a person can still be reached, so it is the part that most needs to be exercised
/// directly.
/// </summary>
public static class NotificationPreferenceRules
{
    /// <summary>
    /// True if at least one channel in <paramref name="resolved"/> is
    /// <see cref="NotificationPreferenceMode.Immediate"/>. A critical event must always satisfy this —
    /// "a customer may choose where the last warning before suspension goes, not whether it exists"
    /// (<c>NotificationService.cs:111-120</c>). <see cref="NotificationPreferenceMode.Digest"/> does
    /// not count: quiet hours and digesting both delay a message, which is exactly what a critical
    /// event must never accept on every one of its channels at once.
    /// </summary>
    public static bool HasCriticalCoverage(
        IReadOnlyDictionary<NotificationChannel, NotificationPreferenceMode> resolved) =>
        resolved.Values.Any(m => m == NotificationPreferenceMode.Immediate);

    /// <summary>
    /// Whether <paramref name="mode"/> is a legal choice for <paramref name="channel"/> at all, before
    /// any critical-coverage question is even asked. <see cref="NotificationChannel.InApp"/> never
    /// digests — see <see cref="NotificationPreferenceMode.Digest"/>'s own doc for why a channel with
    /// no "later, bundled" reading experience has nothing to gain from it.
    /// </summary>
    public static bool IsLegalMode(NotificationChannel channel, NotificationPreferenceMode mode) =>
        channel != NotificationChannel.InApp || mode != NotificationPreferenceMode.Digest;
}
