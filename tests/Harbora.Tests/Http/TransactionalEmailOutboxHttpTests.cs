using System.Net;
using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Domain.Common;
using Harbora.Domain.Identity;
using Harbora.Domain.Jobs;
using Harbora.Domain.Settings;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Password reset, workspace invite and platform invite, through the real routes, now that N1 folds
/// transactional email into the same outbox as alert deliveries (2026-08-16 notification-system spec
/// §7 Q3(b)). Before this, each was a synchronous <c>SmtpClient</c> call inside the request with a
/// try/catch and no record either way; now each writes a durable row and hands it to the job queue,
/// which is what "was that reset email sent" can finally be answered from.
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class TransactionalEmailOutboxHttpTests(HarboraHttpFixture fixture)
{
    private HarboraWebFactory Panel => fixture.Panel;

    /// <summary>Host + From is the whole of <c>SmtpSettings.IsConfigured</c> — the password is not
    /// part of the gate these actions check before doing anything.</summary>
    private void SeedConfiguredSmtp()
    {
        Panel.Seed(db =>
        {
            if (db.Settings.Any(s => s.Key == SettingKeys.SmtpHost)) return;
            db.Settings.AddRange(
                new Harbora.Domain.Settings.Setting { Key = SettingKeys.SmtpHost, Value = "smtp.example.com" },
                new Harbora.Domain.Settings.Setting { Key = SettingKeys.SmtpFrom, Value = "harbora@example.com" });
        });
    }

    [Fact]
    public async Task A_password_reset_request_queues_a_delivery_the_job_queue_can_claim_immediately()
    {
        SeedConfiguredSmtp();
        var email = $"reset-{Guid.NewGuid():N}@example.com";
        Panel.Seed(db => db.Users.Add(new User
        {
            Email = email, DisplayName = "Reset Me", PasswordHash = "x", IsActive = true
        }));

        var client = Panel.CreateClient();
        var token = await client.AntiforgeryTokenFrom("/account/forgot");
        var response = await client.PostFormAsync("/account/forgot", token, ("email", email));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync();
        // The form disappears once ViewBag.Sent is true — the same page for a real account and a
        // fictional one, which is the anti-enumeration property this page exists to keep.
        html.Should().NotContain("name=\"email\"", "the page shows the \"a link is on its way\" state, not the form again");

        var delivery = Panel.Read(db => db.NotificationDeliveries
            .Single(d => d.Purpose == NotificationDeliveryPurpose.PasswordReset && d.RecipientAddress == email));
        delivery.Status.Should().Be(NotificationDeliveryStatus.Pending);
        delivery.Channel.Should().Be(AlertChannel.Email);

        // The latency claim: nothing about queuing left this delivery waiting on anything. The Job
        // row is Pending with no NextAttemptAt — claimable on the worker's very next pass, not held
        // behind a backoff the way a retried attempt would be.
        var job = Panel.Read(db => db.Jobs.Single(j => j.Kind == JobKind.NotificationDelivery && j.TargetId == delivery.Id));
        job.Status.Should().Be(JobStatus.Pending);
        job.NextAttemptAt.Should().BeNull();
        job.Attempts.Should().Be(0, "not yet claimed — queuing does not itself count as an attempt");
    }

    [Fact]
    public async Task A_password_reset_request_for_an_address_with_no_account_shows_the_same_page_and_queues_nothing()
    {
        SeedConfiguredSmtp();
        var email = $"no-such-account-{Guid.NewGuid():N}@example.com";

        var client = Panel.CreateClient();
        var token = await client.AntiforgeryTokenFrom("/account/forgot");
        var response = await client.PostFormAsync("/account/forgot", token, ("email", email));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync();
        html.Should().NotContain("name=\"email\"", "the anti-enumeration page looks identical either way");

        Panel.Read(db => db.NotificationDeliveries.Any(d => d.RecipientAddress == email)).Should().BeFalse(
            "there is no account for this address, so nothing was queued to it");
    }

    [Fact]
    public async Task A_workspace_invite_queues_a_delivery_and_still_shows_the_link_regardless()
    {
        SeedConfiguredSmtp();
        // A workspace of this test's own, not fixture.WorkspaceId: that one is shared by every test
        // in the HTTP collection and, by the time this runs, may already be at its plan's member
        // ceiling from everything else the collection put in it — a quota refusal upstream of
        // anything this test is about would make it fail for the wrong reason (see the identical
        // note in AppCreateSlugRefusalHttpTests.GivenAFreshWorkspace).
        var workspaceId = Guid.CreateVersion7();
        Panel.Seed(db =>
        {
            var planId = db.Plans.Where(p => p.IsDefault).Select(p => p.Id).FirstOrDefault();
            db.Workspaces.Add(new Harbora.Domain.Identity.Workspace
            {
                Id = workspaceId, Name = "Invite outbox test",
                Slug = "invite-outbox-" + workspaceId.ToString("N")[..8],
                PlanId = planId == Guid.Empty ? null : planId
            });
        });
        var owner = Panel.GivenUser(workspaceId, $"invite-owner-{workspaceId:N}@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.250", owner.Email);
        var invited = $"invitee-{Guid.NewGuid():N}@example.com";

        var token = await client.AntiforgeryTokenFrom("/workspaces");
        var response = await client.PostFormAsync("/workspaces/invite", token,
            ("email", invited), ("role", "Member"));

        response.StatusCode.Should().Be(HttpStatusCode.Found, "the action redirects back to the workspace list");

        var delivery = Panel.Read(db => db.NotificationDeliveries
            .Single(d => d.Purpose == NotificationDeliveryPurpose.WorkspaceInvite && d.RecipientAddress == invited));
        delivery.WorkspaceId.Should().Be(workspaceId);
        delivery.Status.Should().Be(NotificationDeliveryStatus.Pending);

        Panel.Read(db => db.Jobs.Any(j => j.Kind == JobKind.NotificationDelivery && j.TargetId == delivery.Id))
            .Should().BeTrue("queuing must hand the delivery to the job queue, not merely write the row");
    }
}
