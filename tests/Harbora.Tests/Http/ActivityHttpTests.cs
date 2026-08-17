using System.Net;
using FluentAssertions;
using Harbora.Domain.Common;
using Harbora.Domain.Identity;
using Harbora.Domain.Jobs;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// <c>/activity</c> end to end (P5, 2026-08-17 app-environment-management design): the real route,
/// real Razor, real DI — driving the whole panel through HTTP the way <c>NotificationsHttpTests</c>
/// already does for the sibling page N3 built.
///
/// <para>
/// The test that matters most here is the scoping one: <c>Job</c> carries no query filter (it is one
/// of the deliberately unfiltered platform tables), so whatever proves the page still shows only the
/// caller's own workspace's rows is proving that <c>ActivityController</c>'s hand-written filter on
/// the denormalised <c>WorkspaceId</c> actually does the job a global filter would have done for a
/// filtered table.
/// </para>
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class ActivityHttpTests(HarboraHttpFixture fixture)
{
    private HarboraWebFactory Panel => fixture.Panel;

    private Guid SeedJob(Guid workspaceId, JobKind kind = JobKind.Deployment,
        JobStatus status = JobStatus.Pending, string? error = null)
    {
        var id = Guid.CreateVersion7();
        Panel.Seed(db => db.Jobs.Add(new Job
        {
            Id = id,
            Kind = kind,
            TargetId = Guid.CreateVersion7(),
            WorkspaceId = workspaceId,
            Status = status,
            Error = error,
            CreatedAt = DateTimeOffset.UtcNow
        }));
        return id;
    }

    private (Guid WorkspaceId, User User) GivenAnotherWorkspaceWithAUser(string email)
    {
        var workspaceId = Guid.CreateVersion7();
        Panel.Seed(db => db.Workspaces.Add(new Workspace
        {
            Id = workspaceId, Name = "Someone Else Co", Slug = "someone-else-co-" + workspaceId
        }));
        var user = Panel.GivenUser(workspaceId, email, SystemRole.Owner);
        return (workspaceId, user);
    }

    [Fact]
    public async Task The_activity_page_shows_a_job_from_the_callers_own_workspace_and_not_one_from_another()
    {
        Panel.GivenUser(fixture.WorkspaceId, "activity-me@example.com", SystemRole.Owner);
        var (otherWorkspaceId, _) = GivenAnotherWorkspaceWithAUser("activity-someone-else@example.com");

        var mine = SeedJob(fixture.WorkspaceId);
        var theirs = SeedJob(otherWorkspaceId);

        var client = await Panel.SignedInAs("198.51.100.70", "activity-me@example.com");
        var html = await (await client.GetAsync("/activity")).Content.ReadAsStringAsync();

        html.Should().Contain($"data-job-id=\"{mine}\"", "the caller's own workspace's job must be listed");
        html.Should().NotContain($"data-job-id=\"{theirs}\"",
            "another workspace's job must never appear, even though Job itself carries no tenant filter");
    }

    /// <summary>Job rows with no workspace at all (a billing tick, a pre-login email) must not leak
    /// onto every workspace's page — the failure mode a filter of "null or mine" would have produced,
    /// which is exactly why <see cref="Harbora.Web.Controllers.ActivityController"/> compares to the
    /// caller's own real <c>WorkspaceId</c> rather than admitting a null.</summary>
    [Fact]
    public async Task A_platform_level_job_with_no_workspace_never_appears_on_anyones_page()
    {
        Panel.GivenUser(fixture.WorkspaceId, "activity-platform@example.com", SystemRole.Owner);
        var platformJobId = Guid.CreateVersion7();
        Panel.Seed(db => db.Jobs.Add(new Job
        {
            Id = platformJobId, Kind = JobKind.BillingHour, TargetId = Guid.CreateVersion7(),
            WorkspaceId = null, Status = JobStatus.Pending, CreatedAt = DateTimeOffset.UtcNow
        }));

        var client = await Panel.SignedInAs("198.51.100.71", "activity-platform@example.com");
        var html = await (await client.GetAsync("/activity")).Content.ReadAsStringAsync();

        html.Should().NotContain($"data-job-id=\"{platformJobId}\"");
    }

    [Fact]
    public async Task The_activity_page_carries_status_and_kind_as_data_attributes_not_sentences()
    {
        Panel.GivenUser(fixture.WorkspaceId, "activity-attrs@example.com", SystemRole.Owner);
        var jobId = SeedJob(fixture.WorkspaceId, kind: JobKind.BackupSnapshot, status: JobStatus.Failed,
            error: "disk full");

        var client = await Panel.SignedInAs("198.51.100.72", "activity-attrs@example.com");
        var html = await (await client.GetAsync("/activity")).Content.ReadAsStringAsync();

        html.Should().Contain($"data-job-id=\"{jobId}\"");
        html.Should().Contain("data-job-kind=\"BackupSnapshot\"");
        html.Should().Contain("data-job-status=\"Failed\"");
    }

    [Fact]
    public async Task The_activity_page_filters_by_status_using_the_query_string()
    {
        Panel.GivenUser(fixture.WorkspaceId, "activity-filter@example.com", SystemRole.Owner);
        var pending = SeedJob(fixture.WorkspaceId, status: JobStatus.Pending);
        var succeeded = SeedJob(fixture.WorkspaceId, status: JobStatus.Succeeded);

        var client = await Panel.SignedInAs("198.51.100.73", "activity-filter@example.com");
        var html = await (await client.GetAsync("/activity?status=Succeeded")).Content.ReadAsStringAsync();

        html.Should().Contain($"data-job-id=\"{succeeded}\"");
        html.Should().NotContain($"data-job-id=\"{pending}\"");
    }

    [Fact]
    public async Task Cancelling_a_pending_job_from_the_callers_own_workspace_settles_it_cancelled()
    {
        Panel.GivenUser(fixture.WorkspaceId, "activity-cancel-me@example.com", SystemRole.Owner);
        var jobId = SeedJob(fixture.WorkspaceId, status: JobStatus.Pending);

        var client = await Panel.SignedInAs("198.51.100.74", "activity-cancel-me@example.com");
        var token = await client.AntiforgeryTokenFrom("/activity");
        var response = await client.PostFormAsync($"/activity/{jobId}/cancel", token);

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        Panel.Read(db => db.Jobs.Single(j => j.Id == jobId)).Status.Should().Be(JobStatus.Cancelled,
            "a Pending job is settled the instant IJobQueue.RequestCancellationAsync is asked");
    }

    /// <summary>The same guard <c>NotificationsController.MarkRead</c> uses: an id posted from outside
    /// the row's own workspace must find nothing, never touch it via the id alone.</summary>
    [Fact]
    public async Task Cancelling_a_job_from_another_workspace_by_id_changes_nothing()
    {
        Panel.GivenUser(fixture.WorkspaceId, "activity-attacker@example.com", SystemRole.Owner);
        var (otherWorkspaceId, _) = GivenAnotherWorkspaceWithAUser("activity-victim@example.com");
        var victimsJob = SeedJob(otherWorkspaceId, status: JobStatus.Pending);

        var client = await Panel.SignedInAs("198.51.100.75", "activity-attacker@example.com");
        var token = await client.AntiforgeryTokenFrom("/activity");
        var response = await client.PostFormAsync($"/activity/{victimsJob}/cancel", token);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        Panel.Read(db => db.Jobs.Single(j => j.Id == victimsJob)).Status.Should().Be(JobStatus.Pending,
            "another workspace's job id must not be enough to touch it");
    }

    /// <summary>
    /// One of the "queued. It runs in the background" messages 0026 names as the defect exactly — the
    /// row exists, is cancellable in principle, and used to dead-end there. <c>BackupsController.Run</c>
    /// now sets <c>TempData["MessageLinksToActivity"]</c> alongside the message, and
    /// <c>Views/Backups/Index.cshtml</c> renders it as a real link — asserted on the <c>data-</c>
    /// attribute and the href, never on the (Persian-by-default) sentence.
    /// </summary>
    [Fact]
    public async Task Queuing_a_backup_leaves_a_message_that_links_to_activity()
    {
        Panel.GivenUser(fixture.WorkspaceId, "activity-link-backup@example.com", SystemRole.Owner);
        var destinationId = Guid.CreateVersion7();
        Panel.Seed(db => db.BackupDestinations.Add(new Harbora.Domain.Backups.BackupDestination
        {
            Id = destinationId, WorkspaceId = fixture.WorkspaceId, Name = "Local",
            Type = BackupDestinationType.Local, IsDefault = true
        }));

        var client = await Panel.SignedInAs("198.51.100.76", "activity-link-backup@example.com");
        var token = await client.AntiforgeryTokenFrom("/backups");
        var runResponse = await client.PostFormAsync("/backups/run", token,
            ("target", "FullPlatform|platform"), ("destinationId", destinationId.ToString()));
        runResponse.StatusCode.Should().Be(HttpStatusCode.Found, "a queued backup redirects back to the list");

        var html = await (await client.GetAsync("/backups")).Content.ReadAsStringAsync();

        html.Should().Contain("data-message-activity-link");
        html.Should().Contain("href=\"/activity\"");
    }
}
