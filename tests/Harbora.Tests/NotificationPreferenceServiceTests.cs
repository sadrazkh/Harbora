using FluentAssertions;
using Harbora.Data;
using Harbora.Domain.Common;
using Harbora.Domain.Notifications;
using Harbora.Infrastructure.Notifications;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// N5 (2026-08-16 notification-system spec, "noise control") — the service every write to a person's
/// own preferences goes through. The critical-coverage invariant is exercised here directly, at write
/// time, rather than only trusted to a form the UI happens to render correctly.
/// </summary>
public class NotificationPreferenceServiceTests
{
    private static (NotificationPreferenceService Service, HarboraDbContext Db) Build()
    {
        var db = new HarboraDbContext(new DbContextOptionsBuilder<HarboraDbContext>()
            .UseInMemoryDatabase("prefs-" + Guid.NewGuid()).Options);
        return (new NotificationPreferenceService(db), db);
    }

    [Fact]
    public async Task A_user_who_has_never_touched_preferences_resolves_every_event_to_the_default()
    {
        var (service, _) = Build();
        var user = Guid.NewGuid();

        // Proves absent-means-default even for an event kind that did not exist when this user
        // registered: nothing seeded a row for it, and the resolution still comes back Immediate.
        var newest = (AlertEvent)(Enum.GetValues<AlertEvent>().Cast<int>().Max() + 1);

        (await service.ResolveAsync(user, newest, NotificationChannel.InApp, default))
            .Should().Be(NotificationPreferenceMode.Immediate);
        (await service.ResolveAsync(user, newest, NotificationChannel.Email, default))
            .Should().Be(NotificationPreferenceMode.Off);
    }

    [Fact]
    public async Task Setting_one_channel_does_not_disturb_an_explicit_row_on_the_other()
    {
        var (service, _) = Build();
        var user = Guid.NewGuid();

        await service.SetAsync(user, AlertEvent.ThresholdBreached, NotificationChannel.Email,
            NotificationPreferenceMode.Digest, default);
        await service.SetAsync(user, AlertEvent.ThresholdBreached, NotificationChannel.InApp,
            NotificationPreferenceMode.Off, default);

        var resolved = await service.ResolveAllAsync(user, AlertEvent.ThresholdBreached, default);
        resolved[NotificationChannel.Email].Should().Be(NotificationPreferenceMode.Digest);
        resolved[NotificationChannel.InApp].Should().Be(NotificationPreferenceMode.Off);
    }

    [Fact]
    public async Task A_second_write_to_the_same_pair_overwrites_rather_than_duplicates()
    {
        var (service, db) = Build();
        var user = Guid.NewGuid();

        await service.SetAsync(user, AlertEvent.ThresholdBreached, NotificationChannel.Email,
            NotificationPreferenceMode.Digest, default);
        await service.SetAsync(user, AlertEvent.ThresholdBreached, NotificationChannel.Email,
            NotificationPreferenceMode.Off, default);

        db.NotificationPreferences.Should().ContainSingle().Which.Mode.Should().Be(NotificationPreferenceMode.Off);
    }

    // ---- the critical-coverage invariant --------------------------------------------------

    [Fact]
    public async Task A_critical_event_may_be_re_routed_to_email_only()
    {
        var (service, _) = Build();
        var user = Guid.NewGuid();

        var toEmail = await service.SetAsync(user, AlertEvent.DeployFailed, NotificationChannel.Email,
            NotificationPreferenceMode.Immediate, default);
        toEmail.Ok.Should().BeTrue();

        var offInApp = await service.SetAsync(user, AlertEvent.DeployFailed, NotificationChannel.InApp,
            NotificationPreferenceMode.Off, default);
        offInApp.Ok.Should().BeTrue("email is still Immediate, so the event still has somewhere to land");

        var resolved = await service.ResolveAllAsync(user, AlertEvent.DeployFailed, default);
        NotificationPreferenceRules.HasCriticalCoverage(resolved).Should().BeTrue();
    }

    [Fact]
    public async Task A_critical_event_cannot_be_switched_off_on_every_channel_at_once()
    {
        var (service, db) = Build();
        var user = Guid.NewGuid();

        // In-app starts Immediate by default; turning it off with email still Off (its own default)
        // would leave AppCrashed with nowhere to land at all.
        var result = await service.SetAsync(user, AlertEvent.AppCrashed, NotificationChannel.InApp,
            NotificationPreferenceMode.Off, default);

        result.Ok.Should().BeFalse();
        result.Rejection.Should().Be(NotificationPreferenceRejection.CriticalCoverageLost);
        db.NotificationPreferences.Should().BeEmpty("a refused write must not be persisted");
    }

    [Fact]
    public async Task Turning_off_the_last_remaining_channel_of_a_critical_event_is_refused()
    {
        var (service, db) = Build();
        var user = Guid.NewGuid();

        // Re-route to email first (legal — in-app is still Immediate by default at this point).
        (await service.SetAsync(user, AlertEvent.LowBalance, NotificationChannel.Email,
            NotificationPreferenceMode.Immediate, default)).Ok.Should().BeTrue();
        (await service.SetAsync(user, AlertEvent.LowBalance, NotificationChannel.InApp,
            NotificationPreferenceMode.Off, default)).Ok.Should().BeTrue();

        // Now email is the ONLY Immediate channel left — turning it off too must be refused.
        var result = await service.SetAsync(user, AlertEvent.LowBalance, NotificationChannel.Email,
            NotificationPreferenceMode.Off, default);

        result.Ok.Should().BeFalse();
        result.Rejection.Should().Be(NotificationPreferenceRejection.CriticalCoverageLost);
        (await service.ResolveAsync(user, AlertEvent.LowBalance, NotificationChannel.Email, default))
            .Should().Be(NotificationPreferenceMode.Immediate, "the refused write must not have landed");
    }

    [Fact]
    public async Task An_optional_event_may_be_switched_off_on_every_channel()
    {
        var (service, _) = Build();
        var user = Guid.NewGuid();

        var offInApp = await service.SetAsync(user, AlertEvent.ThresholdBreached, NotificationChannel.InApp,
            NotificationPreferenceMode.Off, default);
        var offEmail = await service.SetAsync(user, AlertEvent.ThresholdBreached, NotificationChannel.Email,
            NotificationPreferenceMode.Off, default);

        offInApp.Ok.Should().BeTrue();
        offEmail.Ok.Should().BeTrue("ThresholdBreached is optional — full silence is a legal choice");
    }

    [Fact]
    public async Task InApp_may_not_be_set_to_digest()
    {
        var (service, db) = Build();
        var user = Guid.NewGuid();

        var result = await service.SetAsync(user, AlertEvent.ThresholdBreached, NotificationChannel.InApp,
            NotificationPreferenceMode.Digest, default);

        result.Ok.Should().BeFalse();
        result.Rejection.Should().Be(NotificationPreferenceRejection.IllegalMode);
        db.NotificationPreferences.Should().BeEmpty();
    }
}
