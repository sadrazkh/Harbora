using System.Net;
using FluentAssertions;
using Harbora.Domain.Common;
using Harbora.Domain.Notifications;
using Harbora.Infrastructure.Notifications;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The preferences page, end to end (N5, 2026-08-16 notification-system spec, "noise control"): the
/// real <c>/notifications/preferences</c> route, real Razor, real production DI wiring. Complements
/// <see cref="NotificationPreferenceServiceTests"/> and <see cref="NotificationPreferenceRoutingTests"/>,
/// which drive the service and the router directly — this drives the controller and the form a person
/// actually submits.
///
/// <para>
/// The panel renders Persian by default, so every assertion here reads a <c>data-</c> attribute or a
/// submitted/selected value rather than visible text — the same idiom the incident timeline and the
/// N3 notifications page already established.
/// </para>
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class NotificationPreferencesHttpTests(HarboraHttpFixture fixture)
{
    private HarboraWebFactory Panel => fixture.Panel;

    [Fact]
    public async Task A_user_who_has_never_touched_preferences_sees_every_event_resolved_to_its_default()
    {
        Panel.GivenUser(fixture.WorkspaceId, "prefs-fresh@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("198.51.100.60", "prefs-fresh@example.com");

        var html = await (await client.GetAsync("/notifications/preferences")).Content.ReadAsStringAsync();

        // A critical event (default in-app Immediate, email Off) and an optional one
        // (ThresholdBreached, same defaults) both render — nobody had to seed a row for either.
        html.Should().Contain($"data-preference-event=\"{AlertEvent.DeployFailed}\"");
        html.Should().Contain($"data-preference-critical=\"true\"");
        html.Should().Contain($"data-preference-event=\"{AlertEvent.ThresholdBreached}\"");
        html.Should().Contain($"data-preference-critical=\"false\"");
        // AlertEvent.Test is excluded — nothing here can ever be set for it.
        html.Should().NotContain($"data-preference-event=\"{AlertEvent.Test}\"");
    }

    [Fact]
    public async Task Muting_in_app_for_an_optional_event_is_accepted_and_reflected_on_the_next_load()
    {
        Panel.GivenUser(fixture.WorkspaceId, "prefs-mute@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("198.51.100.61", "prefs-mute@example.com");
        var token = await client.AntiforgeryTokenFrom("/notifications/preferences");

        var response = await client.PostFormAsync("/notifications/preferences/event", token,
            ("eventType", AlertEvent.ThresholdBreached.ToString()),
            ("channel", NotificationChannel.InApp.ToString()),
            ("mode", NotificationPreferenceMode.Off.ToString()));

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.RedirectPath().Should().Be("/notifications/preferences");

        var row = Panel.Read(db => db.NotificationPreferences.Single(
            p => p.EventType == AlertEvent.ThresholdBreached && p.Channel == NotificationChannel.InApp));
        row.Mode.Should().Be(NotificationPreferenceMode.Off);
    }

    [Fact]
    public async Task Muting_the_last_immediate_channel_of_a_critical_event_is_refused_and_shown_on_the_page()
    {
        Panel.GivenUser(fixture.WorkspaceId, "prefs-critical@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("198.51.100.62", "prefs-critical@example.com");
        var token = await client.AntiforgeryTokenFrom("/notifications/preferences");

        // In-app is Immediate by default and email is Off by default, so turning in-app off too
        // would leave AppCrashed with nowhere to land.
        var response = await client.PostFormAsync("/notifications/preferences/event", token,
            ("eventType", AlertEvent.AppCrashed.ToString()),
            ("channel", NotificationChannel.InApp.ToString()),
            ("mode", NotificationPreferenceMode.Off.ToString()));

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        Panel.Read(db => db.NotificationPreferences
                .Any(p => p.EventType == AlertEvent.AppCrashed && p.Channel == NotificationChannel.InApp))
            .Should().BeFalse("the refused write must never reach the table");

        var landing = await client.GetAsync(response.Headers.Location!.OriginalString);
        var html = await landing.Content.ReadAsStringAsync();
        html.Should().Contain($"data-preference-rejection=\"{NotificationPreferenceRejection.CriticalCoverageLost}\"");
    }

    [Fact]
    public async Task Quiet_hours_the_time_zone_and_the_weekly_opt_in_all_save_together()
    {
        var user = Panel.GivenUser(fixture.WorkspaceId, "prefs-quiet@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("198.51.100.63", "prefs-quiet@example.com");
        var token = await client.AntiforgeryTokenFrom("/notifications/preferences");

        var response = await client.PostFormAsync("/notifications/preferences/quiet-hours", token,
            ("timeZoneId", "Europe/Berlin"), ("quietHoursStartHour", "22"), ("quietHoursEndHour", "6"),
            ("weeklyReportOptIn", "true"));

        response.StatusCode.Should().Be(HttpStatusCode.Found);

        var saved = Panel.Read(db => db.Users.Single(u => u.Id == user.Id));
        saved.TimeZoneId.Should().Be("Europe/Berlin");
        saved.QuietHoursStartHour.Should().Be(22);
        saved.QuietHoursEndHour.Should().Be(6);
        saved.WeeklyReportOptIn.Should().BeTrue();
    }

    [Fact]
    public async Task An_hour_outside_zero_to_twentythree_is_clamped_rather_than_rejected()
    {
        var user = Panel.GivenUser(fixture.WorkspaceId, "prefs-clamp@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("198.51.100.64", "prefs-clamp@example.com");
        var token = await client.AntiforgeryTokenFrom("/notifications/preferences");

        var response = await client.PostFormAsync("/notifications/preferences/quiet-hours", token,
            ("timeZoneId", "UTC"), ("quietHoursStartHour", "99"), ("quietHoursEndHour", "30"),
            ("weeklyReportOptIn", "false"));

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        var saved = Panel.Read(db => db.Users.Single(u => u.Id == user.Id));
        saved.QuietHoursStartHour.Should().Be(23);
        saved.QuietHoursEndHour.Should().Be(23);
    }
}
