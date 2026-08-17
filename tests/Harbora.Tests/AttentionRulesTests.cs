using FluentAssertions;
using Harbora.Infrastructure.Dashboard;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// What the dashboard opens with.
///
/// The rule this exists to defend: <b>nothing appears here that a person cannot act on</b>. A count of
/// total deployments is decoration; "this deploy failed, here it is" is attention. Every assertion
/// below is about keeping that line in the right place — including the cases that must stay silent,
/// because a dashboard that always has something to say trains people to stop reading it.
///
/// The rule emits resource keys and arguments, never finished sentences: the first version composed
/// English here, which put the panel's most important copy in a language the person never chose.
/// </summary>
public class AttentionRulesTests
{
    private static readonly Guid Deployment = Guid.CreateVersion7();
    private static readonly Guid AppId = Guid.CreateVersion7();

    [Fact]
    public void A_healthy_workspace_produces_nothing()
    {
        var items = AttentionRules.Build(new AttentionFacts { HasAnyApp = true, HasAnyBackupSchedule = true });

        items.Should().BeEmpty("silence is the correct output when nothing is wrong");
    }

    [Fact]
    public void A_failed_deployment_links_to_the_deployment()
    {
        var items = AttentionRules.Build(new AttentionFacts
        {
            HasAnyApp = true, HasAnyBackupSchedule = true,
            FailedDeployments = [("api", Deployment, "The container keeps crashing and being restarted.")]
        });

        var item = items.Should().ContainSingle().Subject;
        item.Level.Should().Be(AttentionLevel.Critical);
        item.TitleKey.Should().Be(AttentionRules.DeployFailedTitle);
        item.TitleArgs.Should().Equal("api");
        item.DetailText.Should().Contain("crashing", "the error itself is data and travels verbatim");
        item.DetailKey.Should().BeNull("a real error message beats the generic fallback");
        item.ActionUrl.Should().Be($"/deployments/details/{Deployment}");
    }

    [Fact]
    public void A_failed_deployment_with_no_error_still_says_something()
    {
        var items = AttentionRules.Build(new AttentionFacts
        {
            HasAnyApp = true, HasAnyBackupSchedule = true,
            FailedDeployments = [("api", Deployment, null)]
        });

        // Otherwise the row renders a headline over an empty line, which reads as a broken page.
        items.Single().DetailKey.Should().Be(AttentionRules.DeployFailedDetail);
    }

    [Fact]
    public void The_most_serious_finding_comes_first()
    {
        // Deliberately built so severity order and the order the rules happen to add things in
        // disagree: a certificate warning is added before a failed backup. An earlier version of this
        // test used a combination where they already matched, so it proved nothing.
        var items = AttentionRules.Build(new AttentionFacts
        {
            HasAnyApp = true,
            NeverDeployed = [("draft", AppId)],
            CertificateProblems = [("shop.example.com", CertificateIssue.ExpiringSoon, "9")],
            FailedBackups = [("shop-db", "pg_dump exited 1")]
        });

        items[0].Level.Should().Be(AttentionLevel.Critical);
        items[0].TitleArgs.Should().Contain("shop-db", "the outage outranks the certificate that is merely due");
        items.Select(i => (int)i.Level).Should().BeInAscendingOrder();
    }

    [Fact]
    public void An_expired_certificate_outranks_one_that_is_merely_due()
    {
        // An expired certificate is a broken site right now; one expiring in ten days is not, yet.
        // Decided on the structured issue, not by sniffing prose for the word "expired" — the first
        // version did that, which was one translation away from always choosing Warning.
        var expired = AttentionRules.Build(new AttentionFacts
        {
            HasAnyApp = true, HasAnyBackupSchedule = true,
            CertificateProblems = [("shop.example.com", CertificateIssue.Expired, "2026-07-20")]
        });
        var soon = AttentionRules.Build(new AttentionFacts
        {
            HasAnyApp = true, HasAnyBackupSchedule = true,
            CertificateProblems = [("shop.example.com", CertificateIssue.ExpiringSoon, "10")]
        });

        expired.Single().Level.Should().Be(AttentionLevel.Critical);
        expired.Single().TitleKey.Should().Be(AttentionRules.CertificateExpiredTitle);
        expired.Single().DetailArgs.Should().Equal("2026-07-20");

        soon.Single().Level.Should().Be(AttentionLevel.Warning);
        // "needs attention", not "expired" — a certificate that is merely due, headlined as already
        // expired, is the dashboard telling the person their site is down when it is not.
        soon.Single().TitleKey.Should().Be(AttentionRules.CertificateAttentionTitle);
        soon.Single().DetailArgs.Should().Equal("10");
    }

    [Fact]
    public void A_failed_issuance_shows_the_real_error_when_there_is_one()
    {
        var items = AttentionRules.Build(new AttentionFacts
        {
            HasAnyApp = true, HasAnyBackupSchedule = true,
            CertificateProblems = [("shop.example.com", CertificateIssue.IssueFailed, "acme: DNS-01 challenge failed")]
        });

        var item = items.Single();
        item.Level.Should().Be(AttentionLevel.Warning);
        item.DetailText.Should().Contain("DNS-01");
    }

    [Fact]
    public void A_failed_service_provision_links_to_the_database()
    {
        var serviceId = Guid.CreateVersion7();
        var items = AttentionRules.Build(new AttentionFacts
        {
            HasAnyApp = true, HasAnyBackupSchedule = true,
            FailedServices = [("orders-db", serviceId, "The database refused the new password.")]
        });

        var item = items.Should().ContainSingle().Subject;
        item.Level.Should().Be(AttentionLevel.Critical);
        item.TitleKey.Should().Be(AttentionRules.ServiceFailedTitle);
        item.TitleArgs.Should().Equal("orders-db");
        item.DetailText.Should().Contain("password", "the error itself is data and travels verbatim");
        item.DetailKey.Should().BeNull("a real error message beats the generic fallback");
        item.ActionUrl.Should().Be($"/databases/{serviceId}");
    }

    [Fact]
    public void A_failed_service_provision_with_no_error_still_says_something()
    {
        var items = AttentionRules.Build(new AttentionFacts
        {
            HasAnyApp = true, HasAnyBackupSchedule = true,
            FailedServices = [("orders-db", Guid.CreateVersion7(), null)]
        });

        items.Single().DetailKey.Should().Be(AttentionRules.ServiceFailedDetail);
    }

    [Fact]
    public void A_channel_that_stopped_delivering_is_surfaced()
    {
        // This is the finding that makes all the others reach anyone. A silent alert channel is why
        // nobody hears about the failed deploy above.
        var items = AttentionRules.Build(new AttentionFacts
        {
            HasAnyApp = true, HasAnyBackupSchedule = true,
            BrokenChannels = [("ops chat", ChannelKind.BackupDelivery, "Telegram returned 401 Unauthorized")]
        });

        var item = items.Should().ContainSingle().Subject;
        item.TitleArgs.Should().Contain("ops chat");
        item.DetailKey.Should().Be(AttentionRules.ChannelBackupDetail);
        item.DetailArgs.Single().Should().Contain("401");
        item.ActionUrl.Should().Be("/backups");
    }

    [Fact]
    public void An_alert_channel_points_at_alerts_not_backups()
    {
        var items = AttentionRules.Build(new AttentionFacts
        {
            HasAnyApp = true, HasAnyBackupSchedule = true,
            BrokenChannels = [("ops chat", ChannelKind.Alert, "410 Gone")]
        });

        items.Single().DetailKey.Should().Be(AttentionRules.ChannelAlertDetail);
        items.Single().ActionUrl.Should().Be("/monitoring");
    }

    [Fact]
    public void A_full_disk_is_reported_but_a_normal_one_is_not()
    {
        var quiet = AttentionRules.Build(new AttentionFacts { HasAnyApp = true, HasAnyBackupSchedule = true, DiskUsedRatio = 0.5 });
        var loud = AttentionRules.Build(new AttentionFacts { HasAnyApp = true, HasAnyBackupSchedule = true, DiskUsedRatio = 0.93 });

        quiet.Should().BeEmpty();
        loud.Should().ContainSingle().Which.DetailArgs.Should().Equal("93");
    }

    [Fact]
    public void A_configured_disk_ratio_lower_than_the_default_flags_a_disk_the_default_would_ignore()
    {
        // MonitoringOptions.DiskWarnRatio is the number AttentionService actually passes; the shipped
        // 0.85 default here is only what a caller gets for free when it passes none.
        var facts = new AttentionFacts { HasAnyApp = true, HasAnyBackupSchedule = true, DiskUsedRatio = 0.70 };

        AttentionRules.Build(facts).Should().BeEmpty("70% is under the shipped default of 85%");
        AttentionRules.Build(facts, diskWarnRatio: 0.60).Should()
            .ContainSingle("70% is over the configured 60%, even though it is under the shipped default");
    }

    [Fact]
    public void A_configured_disk_ratio_higher_than_the_default_quiets_a_disk_the_default_would_flag()
    {
        var facts = new AttentionFacts { HasAnyApp = true, HasAnyBackupSchedule = true, DiskUsedRatio = 0.90 };

        AttentionRules.Build(facts).Should().ContainSingle("90% is over the shipped default of 85%");
        AttentionRules.Build(facts, diskWarnRatio: 0.95).Should()
            .BeEmpty("90% is under the configured 95%, even though it is over the shipped default");
    }

    [Fact]
    public void A_workspace_with_apps_and_no_backup_schedule_is_nudged_once()
    {
        var items = AttentionRules.Build(new AttentionFacts { HasAnyApp = true, HasAnyBackupSchedule = false });

        items.Should().ContainSingle().Which.Level.Should().Be(AttentionLevel.Info);
    }

    [Fact]
    public void An_empty_workspace_is_not_nudged_about_backups()
    {
        // There is nothing to protect yet, and a brand-new account should not open on a warning.
        var items = AttentionRules.Build(new AttentionFacts { HasAnyApp = false, HasAnyBackupSchedule = false });

        items.Should().BeEmpty();
    }

    [Fact]
    public void The_list_is_capped_so_it_stays_a_list()
    {
        var many = Enumerable.Range(0, 30).Select(i => ($"app{i}", Guid.CreateVersion7())).ToList();

        var items = AttentionRules.Build(new AttentionFacts { HasAnyApp = true, HasAnyBackupSchedule = true, CrashedApps = many });

        items.Should().HaveCount(AttentionRules.MaxItems);
    }

    [Fact]
    public void Every_key_the_rules_can_emit_is_declared()
    {
        // AllKeys is what the localisation guard walks. A key used in Build but absent from AllKeys
        // escapes that guard, so this pins the two together: every emitted key must be declared.
        var facts = new AttentionFacts
        {
            HasAnyApp = true, HasAnyBackupSchedule = false,
            FailedDeployments = [("a", Deployment, null), ("b", Deployment, "x. y")],
            CrashedApps = [("c", AppId)],
            FailedBackups = [("d", null), ("e", "x. y")],
            FailedServices = [("svc1", AppId, null), ("svc2", AppId, "x. y")],
            BrokenChannels = [("f", ChannelKind.Alert, "x"), ("g", ChannelKind.BackupDelivery, "x")],
            CertificateProblems =
            [
                ("h1", CertificateIssue.Expired, "2026-01-01"),
                ("h2", CertificateIssue.ExpiringSoon, "3"),
                ("h3", CertificateIssue.IssueFailed, null)
            ],
            DiskUsedRatio = 0.99,
            NeverDeployed = [("i", AppId)]
        };

        var emitted = AttentionRules.Build(facts)
            .SelectMany(i => new[] { i.TitleKey, i.DetailKey, i.ActionKey })
            .Where(k => k is not null)
            .Cast<string>()
            .Distinct();

        emitted.Should().BeSubsetOf(AttentionRules.AllKeys);
    }

    [Theory]
    [InlineData("The container keeps crashing. Its last output was: FATAL something very long indeed",
                "The container keeps crashing.")]
    [InlineData("Single sentence with no stop", "Single sentence with no stop")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Only_the_first_sentence_of_an_error_reaches_the_dashboard(string? error, string? expected)
    {
        // The full text stays on the page it came from. A dashboard that reprints a stack trace is
        // not a dashboard.
        AttentionRules.Summarise(error).Should().Be(expected);
    }

    [Fact]
    public void A_very_long_first_sentence_is_still_trimmed()
    {
        var long_ = new string('x', 400);

        AttentionRules.Summarise(long_)!.Length.Should().BeLessThan(200);
    }
}
