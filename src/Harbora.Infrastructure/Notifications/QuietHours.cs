namespace Harbora.Infrastructure.Notifications;

/// <summary>
/// Whether a moment falls inside a person's own quiet hours — N5, 2026-08-16 notification-system
/// spec, "noise control", §7 Q5(a). Pure: no clock, no database, the same reason
/// <c>Monitoring.CertificateAlert.Evaluate</c> is pure — this is the rule a routing decision hangs on,
/// so it is the part worth exercising directly rather than through a whole notification send.
///
/// <para>
/// <b>Only ever asked about the optional half of an event.</b> Doc 09 §4.2: "deliveries in window
/// downgrade to digest (except C-class)" — <c>NotificationService</c> never calls
/// <see cref="IsQuiet"/> for a critical event at all, so there is no branch here that could hold one
/// back. That is enforced at the call site, not this type: a pure predicate about a clock and a time
/// zone has no notion of "critical" to get wrong.
/// </para>
/// </summary>
public static class QuietHours
{
    /// <summary>
    /// True if <paramref name="nowUtc"/>, read in the local hour <paramref name="timeZoneId"/> gives
    /// it, falls inside <c>[startHour, endHour)</c>.
    ///
    /// <para>
    /// Either bound missing means quiet hours are off — half a window is not a window, so a person who
    /// has set only one of the pair is read as never-quiet rather than always- or never- by accident.
    /// A window whose start equals its end is zero-width and likewise never quiet: an accidental
    /// double-click that leaves both fields at the same hour must not read as "quiet all day".
    /// </para>
    /// <para>
    /// A window that does not cross midnight (09 → 17) is an ordinary half-open range. One that does
    /// (22 → 06) is read as everything from <c>start</c> to midnight <i>or</i> midnight to
    /// <c>end</c> — the boundary the spec explicitly asks be exercised at both ends.
    /// </para>
    /// </summary>
    public static bool IsQuiet(int? startHour, int? endHour, string? timeZoneId, DateTimeOffset nowUtc)
    {
        if (startHour is not { } start || endHour is not { } end) return false;
        if (start == end) return false;

        var localHour = LocalHour(timeZoneId, nowUtc);

        return start < end
            ? localHour >= start && localHour < end
            : localHour >= start || localHour < end;
    }

    /// <summary>
    /// <paramref name="nowUtc"/>'s hour in <paramref name="timeZoneId"/>. An unknown or missing zone
    /// fails open to UTC rather than throwing — a bad or unsupported IANA id (no tzdata on a minimal
    /// image, a typo nobody validated) must degrade to "quiet hours measured in UTC", not take down
    /// the routing decision every event now passes through.
    /// </summary>
    private static int LocalHour(string? timeZoneId, DateTimeOffset nowUtc)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId)) return nowUtc.UtcDateTime.Hour;

        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            return TimeZoneInfo.ConvertTime(nowUtc, tz).Hour;
        }
        catch (TimeZoneNotFoundException) { return nowUtc.UtcDateTime.Hour; }
        catch (InvalidTimeZoneException) { return nowUtc.UtcDateTime.Hour; }
    }
}
