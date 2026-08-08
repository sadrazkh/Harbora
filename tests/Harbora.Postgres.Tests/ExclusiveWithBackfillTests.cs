using FluentAssertions;
using Harbora.Domain.Jobs;
using Xunit;
using static Harbora.Postgres.Tests.UpgradeFromPreviousRelease;

namespace Harbora.Postgres.Tests;

/// <summary>
/// The <c>JobExclusiveWith</c> migration's backfill, branch by branch.
///
/// <para>
/// The statement stamps the queue's unfinished deployment jobs with the app they must not double up
/// on. It is the only chance those rows will ever get: they were written by the build being upgraded
/// away from, so no enqueue path can have stamped them, and the parallel worker released alongside
/// the column would otherwise run two <c>docker build</c>s for one app.
/// </para>
///
/// <para>
/// Each fact below is one term of its <c>WHERE</c>. Delete a term and exactly one of them fails.
/// </para>
/// </summary>
[Collection(PostgresLane.Collection)]
public sealed class ExclusiveWithBackfillTests(PostgresLane lane)
{
    [PostgresFact]
    public async Task A_queued_deployment_job_is_stamped_with_its_apps_id()
    {
        var upgraded = await lane.UpgradedAsync();

        var job = await UpgradedReads.JobAsync(upgraded.ConnectionString, Seeded.PendingDeploymentJob);

        job.ExclusiveWith.Should().Be(Seeded.AppOne);
        job.ExcludesOn.Should().Be(Seeded.AppOne);
    }

    [PostgresFact]
    public async Task A_running_deployment_job_is_stamped_too()
    {
        // Running matters as much as Pending: the row is what a restart resumes from, and a resumed
        // deployment with no key excludes on its own deployment id, which is unique per redeploy.
        var upgraded = await lane.UpgradedAsync();

        var job = await UpgradedReads.JobAsync(upgraded.ConnectionString, Seeded.RunningDeploymentJob);

        job.ExclusiveWith.Should().Be(Seeded.AppOne);
    }

    [PostgresFact]
    public async Task Two_queued_deployments_of_one_app_end_up_excluding_on_the_same_thing()
    {
        // The whole reason the column exists. Before it, these two were different targets and were
        // free to run beside each other.
        var upgraded = await lane.UpgradedAsync();

        var first = await UpgradedReads.JobAsync(upgraded.ConnectionString, Seeded.PendingDeploymentJob);
        var second = await UpgradedReads.JobAsync(upgraded.ConnectionString, Seeded.RunningDeploymentJob);

        first.TargetId.Should().NotBe(second.TargetId, "every redeploy is its own Deployment row");
        first.ExcludesOn.Should().Be(second.ExcludesOn);
    }

    [PostgresFact]
    public async Task A_deployment_of_another_app_gets_that_other_apps_id()
    {
        var upgraded = await lane.UpgradedAsync();

        var job = await UpgradedReads.JobAsync(upgraded.ConnectionString, Seeded.OtherAppDeploymentJob);

        job.ExclusiveWith.Should().Be(Seeded.AppTwo);
    }

    [PostgresFact]
    public async Task A_deployment_job_whose_deployment_is_gone_is_left_null_rather_than_given_an_empty_id()
    {
        // The sharpest branch. The join is what makes it safe: an id it cannot read is an id it does
        // not write. Guid.Empty here would make this job exclude against every other keyless
        // deployment on the platform, quietly serialising the whole install.
        var upgraded = await lane.UpgradedAsync();

        var job = await UpgradedReads.JobAsync(upgraded.ConnectionString, Seeded.OrphanedDeploymentJob);

        job.ExclusiveWith.Should().BeNull();
        job.ExcludesOn.Should().Be(job.TargetId, "the fallback is its own target, which is what it always was");
    }

    [PostgresFact]
    public async Task Finished_deployment_jobs_are_left_alone()
    {
        var upgraded = await lane.UpgradedAsync();

        var succeeded = await UpgradedReads.JobAsync(upgraded.ConnectionString, Seeded.SucceededDeploymentJob);
        var failed = await UpgradedReads.JobAsync(upgraded.ConnectionString, Seeded.FailedDeploymentJob);

        succeeded.Status.Should().Be(JobStatus.Succeeded);
        succeeded.ExclusiveWith.Should().BeNull("nothing will claim it again");
        failed.Status.Should().Be(JobStatus.Failed);
        failed.ExclusiveWith.Should().BeNull();
    }

    [PostgresFact]
    public async Task A_job_of_another_kind_is_not_stamped_even_when_its_target_looks_like_a_deployment()
    {
        // Ids come from different tables and can collide by nothing but chance. Without the Kind
        // term this backup job would be given an app id and would then exclude against deployments.
        var upgraded = await lane.UpgradedAsync();

        var job = await UpgradedReads.JobAsync(
            upgraded.ConnectionString, Seeded.BackupJobPointingAtADeploymentId);

        job.Kind.Should().Be(JobKind.Backup);
        job.TargetId.Should().Be(Seeded.DeploymentOfAppOne, "the collision is the point of this row");
        job.ExclusiveWith.Should().BeNull();
    }

    [PostgresFact]
    public async Task The_column_it_added_is_the_only_thing_it_changed()
    {
        // The statement writes one column. A stray SET — on Status, say — would settle work the
        // upgrade is supposed to resume.
        var upgraded = await lane.UpgradedAsync();

        var pending = await UpgradedReads.JobAsync(upgraded.ConnectionString, Seeded.PendingDeploymentJob);
        var running = await UpgradedReads.JobAsync(upgraded.ConnectionString, Seeded.RunningDeploymentJob);

        pending.Status.Should().Be(JobStatus.Pending);
        pending.Attempts.Should().Be(0);
        pending.CreatedAt.Should().BeCloseTo(BeforeTheUpgrade, TimeSpan.FromSeconds(1));
        // The statement sets no NOW() of its own — unlike the settling ones, which do — so a
        // stamped row still carries the timestamp it was queued with.
        pending.UpdatedAt.Should().BeCloseTo(pending.CreatedAt, TimeSpan.FromMilliseconds(1));
        running.Status.Should().Be(JobStatus.Running);

        // NextAttemptAt arrived in the migration before this one and must be empty on every carried
        // row: a value would hold the job out of the queue until a moment nobody chose.
        pending.NextAttemptAt.Should().BeNull();
        running.NextAttemptAt.Should().BeNull();
    }
}
