using Harbora.Domain.Common;

namespace Harbora.Domain.Notifications;

/// <summary>
/// What an absent <see cref="NotificationPreference"/> row resolves to — N5, 2026-08-16
/// notification-system spec, "noise control".
///
/// <para>
/// <see cref="NotificationChannel.InApp"/> defaults to <see cref="NotificationPreferenceMode.Immediate"/>
/// for every event, matching N3's own behaviour exactly: shipping this table must not silently change
/// a single existing delivery for a member who has never opened the preferences page. A personal
/// <see cref="NotificationChannel.Email"/> is new with N5 and defaults to
/// <see cref="NotificationPreferenceMode.Off"/> — it is on top of the in-app copy, not instead of it,
/// and turning it on is an opt-in the same way a workspace Alert rule's own email target always was.
/// </para>
/// </summary>
public static class NotificationPreferenceDefaults
{
    public static NotificationPreferenceMode DefaultFor(AlertEvent evt, NotificationChannel channel) => channel switch
    {
        NotificationChannel.InApp => NotificationPreferenceMode.Immediate,
        NotificationChannel.Email => NotificationPreferenceMode.Off,
        _ => NotificationPreferenceMode.Immediate
    };
}
