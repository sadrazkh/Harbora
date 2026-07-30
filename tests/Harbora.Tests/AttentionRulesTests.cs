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
        item.Title.Should().Contain("api");
        item.Detail.Should().Contain("crashing");
        item.ActionUrl.Should().Be($"/deployments/details/{Deployment}");
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
            CertificateProblems = [("shop.example.com", "The certificate expires in 9 days and has not renewed yet.")],
            FailedBackups = [("shop-db", "pg_dump exited 1")]
        });

        items[0].Level.Should().Be(AttentionLevel.Critical);
        items[0].Title.Should().Contain("shop-db", "the outage outranks the certificate that is merely due");
        items.Select(i => (int)i.Level).Should().BeInAscendingOrder();
    }

    [Fact]
    public void An_expired_certificate_outranks_one_that_is_merely_due()
    {
        // An expired certificate is a broken site right now; one expiring in ten days is not, yet.
        var expired = AttentionRules.Build(new AttentionFacts
        {
            HasAnyApp = true, HasAnyBackupSchedule = true,
            CertificateProblems = [("shop.example.com", "The certificate expired on 2026-07-20.")]
        });
        var soon = AttentionRules.Build(new AttentionFacts
        {
            HasAnyApp = true, HasAnyBackupSchedule = true,
            CertificateProblems = [("shop.example.com", "The certificate expires in 10 days and has not renewed yet.")]
        });

        expired.Single().Level.Should().Be(AttentionLevel.Critical);
        soon.Single().Level.Should().Be(AttentionLevel.Warning);
    }

    [Fact]
    public void A_channel_that_stopped_delivering_is_surfaced()
    {
        // This is the finding that makes all the others reach anyone. A silent alert channel is why
        // nobody hears about the failed deploy above.
        var items = AttentionRules.Build(new AttentionFacts
        {
            HasAnyApp = true, HasAnyBackupSchedule = true,
            BrokenChannels = [("ops chat", "Backup delivery", "Telegram returned 401 Unauthorized")]
        });

        var item = items.Should().ContainSingle().Subject;
        item.Title.Should().Contain("ops chat");
        item.Detail.Should().Contain("401");
        item.ActionUrl.Should().Be("/backups");
    }

    [Fact]
    public void A_full_disk_is_reported_but_a_normal_one_is_not()
    {
        var quiet = AttentionRules.Build(new AttentionFacts { HasAnyApp = true, HasAnyBackupSchedule = true, DiskUsedRatio = 0.5 });
        var loud = AttentionRules.Build(new AttentionFacts { HasAnyApp = true, HasAnyBackupSchedule = true, DiskUsedRatio = 0.93 });

        quiet.Should().BeEmpty();
        loud.Should().ContainSingle().Which.Detail.Should().Contain("93%");
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
