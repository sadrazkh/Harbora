using FluentAssertions;
using Harbora.Infrastructure.Notifications;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// N5 (2026-08-16 notification-system spec, "noise control"), §7 Q5(a): quiet hours hang off a
/// per-user IANA time zone, not a platform-wide one. These tests exercise the pure predicate directly,
/// including the boundary the spec names explicitly — a window that crosses midnight, at both ends.
/// </summary>
public class QuietHoursTests
{
    private static DateTimeOffset Utc(int hour, int minute = 0) =>
        new(2026, 6, 15, hour, minute, 0, TimeSpan.Zero);

    [Fact]
    public void A_window_that_does_not_cross_midnight_is_quiet_only_inside_it()
    {
        QuietHours.IsQuiet(9, 17, "UTC", Utc(8, 59)).Should().BeFalse();
        QuietHours.IsQuiet(9, 17, "UTC", Utc(9, 0)).Should().BeTrue();
        QuietHours.IsQuiet(9, 17, "UTC", Utc(16, 59)).Should().BeTrue();
        QuietHours.IsQuiet(9, 17, "UTC", Utc(17, 0)).Should().BeFalse("the end hour is exclusive");
    }

    [Fact]
    public void A_window_crossing_midnight_is_quiet_at_both_ends()
    {
        // 22:00 -> 06:00: quiet from 22:00 through 23:59, and again from 00:00 through 05:59.
        QuietHours.IsQuiet(22, 6, "UTC", Utc(21, 59)).Should().BeFalse();
        QuietHours.IsQuiet(22, 6, "UTC", Utc(22, 0)).Should().BeTrue("the late end of the window");
        QuietHours.IsQuiet(22, 6, "UTC", Utc(23, 30)).Should().BeTrue();
        QuietHours.IsQuiet(22, 6, "UTC", Utc(0, 0)).Should().BeTrue("just past midnight, still inside the window");
        QuietHours.IsQuiet(22, 6, "UTC", Utc(5, 59)).Should().BeTrue("the early end of the window");
        QuietHours.IsQuiet(22, 6, "UTC", Utc(6, 0)).Should().BeFalse("the end hour is exclusive even across midnight");
        QuietHours.IsQuiet(22, 6, "UTC", Utc(12, 0)).Should().BeFalse("the middle of the day is never inside a night window");
    }

    [Fact]
    public void Either_bound_missing_means_quiet_hours_are_off()
    {
        QuietHours.IsQuiet(null, 17, "UTC", Utc(12)).Should().BeFalse();
        QuietHours.IsQuiet(9, null, "UTC", Utc(12)).Should().BeFalse();
        QuietHours.IsQuiet(null, null, "UTC", Utc(2)).Should().BeFalse();
    }

    [Fact]
    public void A_zero_width_window_is_never_quiet()
    {
        QuietHours.IsQuiet(9, 9, "UTC", Utc(9)).Should().BeFalse();
    }

    [Fact]
    public void The_hour_is_converted_into_the_persons_own_time_zone()
    {
        // Asia/Tehran is UTC+3:30. 21:00 UTC is 00:30 the next day in Tehran — inside a 22->06 window
        // measured locally, even though 21:00 UTC itself is not.
        QuietHours.IsQuiet(22, 6, "UTC", Utc(21, 0)).Should().BeFalse("21:00 UTC is outside a UTC-measured 22->06 window");
        QuietHours.IsQuiet(22, 6, "Asia/Tehran", Utc(21, 0)).Should().BeTrue("21:00 UTC is 00:30 in Tehran, inside 22->06 there");
    }

    [Fact]
    public void An_unrecognised_time_zone_fails_open_to_utc_rather_than_throwing()
    {
        var act = () => QuietHours.IsQuiet(9, 17, "Not/AZone", Utc(12));
        act.Should().NotThrow();
        QuietHours.IsQuiet(9, 17, "Not/AZone", Utc(12)).Should().BeTrue("12:00 UTC falls inside 09->17 read as UTC");
    }

    [Fact]
    public void A_missing_time_zone_id_is_read_as_utc()
    {
        QuietHours.IsQuiet(9, 17, null, Utc(12)).Should().BeTrue();
        QuietHours.IsQuiet(9, 17, "", Utc(20)).Should().BeFalse();
    }
}
