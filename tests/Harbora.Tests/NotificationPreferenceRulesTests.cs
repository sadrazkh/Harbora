using FluentAssertions;
using Harbora.Domain.Common;
using Harbora.Domain.Notifications;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// N5 (2026-08-16 notification-system spec, "noise control") — the pure rules a preference write goes
/// through before it is ever saved: which events are critical, what an absent row resolves to, and the
/// invariant that keeps a critical event re-routable rather than mutable.
/// </summary>
public class NotificationPreferenceRulesTests
{
    [Theory]
    [InlineData(AlertEvent.DeployFailed)]
    [InlineData(AlertEvent.AppCrashed)]
    [InlineData(AlertEvent.SslExpiring)]
    [InlineData(AlertEvent.DiskWarning)]
    [InlineData(AlertEvent.BackupFailed)]
    [InlineData(AlertEvent.LowBalance)]
    public void Every_event_doc_09_names_as_critical_is_classified_that_way(AlertEvent evt) =>
        NotificationEventClass.IsCritical(evt).Should().BeTrue();

    [Fact]
    public void ThresholdBreached_is_the_one_optional_event_today() =>
        NotificationEventClass.IsCritical(AlertEvent.ThresholdBreached).Should().BeFalse();

    [Fact]
    public void A_newly_appended_event_defaults_to_critical_not_optional()
    {
        // The safe direction to fail in, opposite of NotificationService.Matches's own default: a
        // forgotten arm here must not become quietly muteable.
        var newest = Enum.GetValues<AlertEvent>().Cast<int>().Max() + 1;
        NotificationEventClass.IsCritical((AlertEvent)newest).Should().BeTrue();
    }

    [Fact]
    public void An_absent_row_resolves_in_app_to_immediate_for_every_event()
    {
        foreach (var evt in Enum.GetValues<AlertEvent>())
            NotificationPreferenceDefaults.DefaultFor(evt, NotificationChannel.InApp)
                .Should().Be(NotificationPreferenceMode.Immediate,
                    "N3's guarantee — everyone gets everything in-app — must survive N5 shipping unchanged");
    }

    [Fact]
    public void An_absent_row_resolves_personal_email_to_off_for_every_event()
    {
        foreach (var evt in Enum.GetValues<AlertEvent>())
            NotificationPreferenceDefaults.DefaultFor(evt, NotificationChannel.Email)
                .Should().Be(NotificationPreferenceMode.Off, "a personal email is opt-in, on top of in-app");
    }

    [Fact]
    public void Coverage_holds_when_at_least_one_channel_is_immediate()
    {
        var resolved = new Dictionary<NotificationChannel, NotificationPreferenceMode>
        {
            [NotificationChannel.InApp] = NotificationPreferenceMode.Off,
            [NotificationChannel.Email] = NotificationPreferenceMode.Immediate
        };
        NotificationPreferenceRules.HasCriticalCoverage(resolved).Should().BeTrue(
            "re-routed to email only is still covered — a customer may choose where, not whether");
    }

    [Fact]
    public void Coverage_fails_when_every_channel_is_off()
    {
        var resolved = new Dictionary<NotificationChannel, NotificationPreferenceMode>
        {
            [NotificationChannel.InApp] = NotificationPreferenceMode.Off,
            [NotificationChannel.Email] = NotificationPreferenceMode.Off
        };
        NotificationPreferenceRules.HasCriticalCoverage(resolved).Should().BeFalse();
    }

    [Fact]
    public void Coverage_fails_when_the_only_channel_left_on_is_digested_not_immediate()
    {
        // Digest and quiet hours both delay — neither is a legal resting state for the one channel
        // that is supposed to be a critical event's guarantee.
        var resolved = new Dictionary<NotificationChannel, NotificationPreferenceMode>
        {
            [NotificationChannel.InApp] = NotificationPreferenceMode.Off,
            [NotificationChannel.Email] = NotificationPreferenceMode.Digest
        };
        NotificationPreferenceRules.HasCriticalCoverage(resolved).Should().BeFalse();
    }

    [Fact]
    public void InApp_may_not_be_set_to_digest()
    {
        NotificationPreferenceRules.IsLegalMode(NotificationChannel.InApp, NotificationPreferenceMode.Digest)
            .Should().BeFalse();
        NotificationPreferenceRules.IsLegalMode(NotificationChannel.InApp, NotificationPreferenceMode.Immediate)
            .Should().BeTrue();
        NotificationPreferenceRules.IsLegalMode(NotificationChannel.InApp, NotificationPreferenceMode.Off)
            .Should().BeTrue();
    }

    [Fact]
    public void Email_may_be_set_to_any_mode()
    {
        foreach (var mode in Enum.GetValues<NotificationPreferenceMode>())
            NotificationPreferenceRules.IsLegalMode(NotificationChannel.Email, mode).Should().BeTrue();
    }
}
