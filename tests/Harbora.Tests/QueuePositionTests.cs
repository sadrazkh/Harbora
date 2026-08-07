using FluentAssertions;
using Harbora.Domain.Jobs;
using Harbora.Infrastructure.Deployments;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Why a queued deployment has not started yet.
///
/// The number shown to a user is only worth showing if it is the number the worker would arrive at,
/// so every case here is a rule the claim itself has: Pending only, a retry backoff is not claimable,
/// oldest first, and a row whose (kind, exclusion key) is already held is skipped rather than waited
/// for. A position that counted rows the claim will skip would be a confident lie, which is the exact
/// failure mode this phase exists to remove.
/// </summary>
public class QueuePositionTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 7, 12, 0, 0, TimeSpan.Zero);

    private static Guid Id(int n) => new($"00000000-0000-0000-0000-{n:D12}");

    /// <summary>A Jobs row, with the fields this rule reads and sensible defaults for the rest.</summary>
    private static QueuedJob Job(
        int id, JobKind kind = JobKind.Deployment, int excludesOn = 1,
        JobStatus status = JobStatus.Pending, int minutesOld = 0, DateTimeOffset? nextAttemptAt = null,
        bool cancelRequested = false) =>
        new(Id(id), kind, Id(excludesOn + 1000), status,
            Now.AddMinutes(-minutesOld), nextAttemptAt, cancelRequested);

    // ---- position ----

    [Fact]
    public void A_queued_job_with_an_empty_table_is_next()
    {
        var place = QueuePosition.For([Job(1)], Id(1), Now, maxConcurrency: 4);

        place.Wait.Should().Be(QueueWait.Next);
        place.Position.Should().Be(1);
        place.Ahead.Should().BeEmpty();
    }

    [Fact]
    public void The_position_counts_the_claimable_rows_in_front_of_it()
    {
        var place = QueuePosition.For(
        [
            Job(1, JobKind.Backup, excludesOn: 7, minutesOld: 10),
            Job(2, JobKind.Deployment, excludesOn: 8, minutesOld: 5),
            Job(3, JobKind.Deployment, excludesOn: 1, minutesOld: 1)
        ], Id(3), Now, maxConcurrency: 1);

        place.Position.Should().Be(3);
        place.Ahead.Should().Equal(JobKind.Backup, JobKind.Deployment);
        place.Wait.Should().Be(QueueWait.Behind);
    }

    [Fact]
    public void A_row_that_arrived_after_this_one_is_not_in_front_of_it()
    {
        var place = QueuePosition.For(
        [
            Job(1, excludesOn: 1, minutesOld: 5),
            Job(2, JobKind.Backup, excludesOn: 9, minutesOld: 1)
        ], Id(1), Now, maxConcurrency: 1);

        place.Position.Should().Be(1);
        place.Ahead.Should().BeEmpty();
    }

    [Fact]
    public void A_row_serving_a_retry_backoff_is_not_in_front_of_anything()
    {
        // The claim skips it — `NextAttemptAt <= now` is a term in the query — so counting it would
        // tell someone they are behind work that is not going to run.
        var place = QueuePosition.For(
        [
            Job(1, JobKind.Backup, excludesOn: 7, minutesOld: 10, nextAttemptAt: Now.AddMinutes(20)),
            Job(2, JobKind.Deployment, excludesOn: 1, minutesOld: 1)
        ], Id(2), Now, maxConcurrency: 1);

        place.Position.Should().Be(1);
        place.Ahead.Should().BeEmpty();
    }

    [Fact]
    public void A_backoff_that_has_come_due_is_in_front_again()
    {
        var place = QueuePosition.For(
        [
            Job(1, JobKind.Backup, excludesOn: 7, minutesOld: 10, nextAttemptAt: Now.AddMinutes(-1)),
            Job(2, JobKind.Deployment, excludesOn: 1, minutesOld: 1)
        ], Id(2), Now, maxConcurrency: 1);

        place.Position.Should().Be(2);
        place.Ahead.Should().Equal(JobKind.Backup);
    }

    [Fact]
    public void A_row_the_claim_would_skip_is_not_in_front_of_anything()
    {
        // A snapshot of a target that already has one running is removed from the claim's search,
        // and the claim goes on to the next eligible row — which is this deployment.
        var place = QueuePosition.For(
        [
            Job(1, JobKind.BackupSnapshot, excludesOn: 7, status: JobStatus.Running, minutesOld: 30),
            Job(2, JobKind.BackupSnapshot, excludesOn: 7, minutesOld: 10),
            Job(3, JobKind.Deployment, excludesOn: 1, minutesOld: 1)
        ], Id(3), Now, maxConcurrency: 4);

        place.Position.Should().Be(1, "the queued snapshot cannot be claimed while its target is busy");
        place.Ahead.Should().BeEmpty();
        place.Running.Should().Be(1);
    }

    [Fact]
    public void A_row_marked_for_cancellation_is_not_in_front_of_anything()
    {
        // JobWorker.ClaimNextAsync settles a CancelRequested Pending row to Cancelled the instant it
        // is claimed, without running it — reachable after a shutdown released a claim on a job that
        // had already been asked to stop. Counting it as ahead would count work that never happens.
        var place = QueuePosition.For(
        [
            Job(1, JobKind.Backup, excludesOn: 7, minutesOld: 10, cancelRequested: true),
            Job(2, JobKind.Deployment, excludesOn: 1, minutesOld: 1)
        ], Id(2), Now, maxConcurrency: 1);

        place.Position.Should().Be(1);
        place.Ahead.Should().BeEmpty();
    }

    [Fact]
    public void A_same_key_row_marked_for_cancellation_does_not_hold_the_key_either()
    {
        var place = QueuePosition.For(
        [
            Job(1, JobKind.Deployment, excludesOn: 1, minutesOld: 4, cancelRequested: true),
            Job(2, JobKind.Deployment, excludesOn: 1, minutesOld: 1)
        ], Id(2), Now, maxConcurrency: 4);

        place.Wait.Should().Be(QueueWait.Next);
    }

    // ---- the exclusion, which is the truthful answer ----

    [Fact]
    public void A_running_deployment_of_the_same_app_is_the_answer_rather_than_a_number()
    {
        // A deployment excludes on its app, not on its own row, so this is the ordinary reason a
        // redeploy waits — and "third in the queue" would be a true-sounding way of not saying it.
        var place = QueuePosition.For(
        [
            Job(1, JobKind.Deployment, excludesOn: 1, status: JobStatus.Running, minutesOld: 4),
            Job(2, JobKind.Backup, excludesOn: 7, minutesOld: 3),
            Job(3, JobKind.Deployment, excludesOn: 1, minutesOld: 1)
        ], Id(3), Now, maxConcurrency: 4);

        place.Wait.Should().Be(QueueWait.BlockedBySameTarget);
    }

    [Fact]
    public void An_older_queued_deployment_of_the_same_app_blocks_this_one_too()
    {
        // Nothing holds the app yet, but oldest-first means the older row is claimed first and this
        // one is excluded the moment it is. Saying "second in the queue" would be true for one pass
        // of the loop and wrong from then on. The sentence must also not say "to finish" here — the
        // blocker has not even started.
        var place = QueuePosition.For(
        [
            Job(1, JobKind.Deployment, excludesOn: 1, minutesOld: 4),
            Job(2, JobKind.Deployment, excludesOn: 1, minutesOld: 1)
        ], Id(2), Now, maxConcurrency: 4);

        place.Wait.Should().Be(QueueWait.BlockedBySameTarget);
        QueuePosition.Describe(place, persian: false)
            .Should().Be("Blocked by another deployment of this app; only one may run at a time.");
    }

    [Fact]
    public void A_running_job_of_another_kind_on_the_same_key_does_not_block_it()
    {
        // The pair is what excludes. A backup of something whose id happens to equal this app's is
        // different work.
        var place = QueuePosition.For(
        [
            Job(1, JobKind.Backup, excludesOn: 1, status: JobStatus.Running, minutesOld: 4),
            Job(2, JobKind.Deployment, excludesOn: 1, minutesOld: 1)
        ], Id(2), Now, maxConcurrency: 4);

        place.Wait.Should().Be(QueueWait.Next);
    }

    // ---- slots ----

    [Fact]
    public void Work_in_front_of_it_does_not_mean_waiting_when_there_are_slots_for_all_of_it()
    {
        var jobs = new[]
        {
            Job(1, JobKind.Backup, excludesOn: 7, minutesOld: 10),
            Job(2, JobKind.Deployment, excludesOn: 8, minutesOld: 5),
            Job(3, JobKind.Deployment, excludesOn: 1, minutesOld: 1)
        };

        QueuePosition.For(jobs, Id(3), Now, maxConcurrency: 4).Wait.Should().Be(QueueWait.Next);
        QueuePosition.For(jobs, Id(3), Now, maxConcurrency: 2).Wait.Should().Be(QueueWait.Behind);
    }

    [Fact]
    public void A_running_job_holds_a_slot_even_when_it_blocks_nothing()
    {
        var jobs = new[]
        {
            Job(1, JobKind.Backup, excludesOn: 7, status: JobStatus.Running, minutesOld: 10),
            Job(2, JobKind.CronRun, excludesOn: 8, status: JobStatus.Running, minutesOld: 9),
            Job(3, JobKind.Deployment, excludesOn: 1, minutesOld: 1)
        };

        QueuePosition.For(jobs, Id(3), Now, maxConcurrency: 2).Wait.Should().Be(QueueWait.Behind);
        QueuePosition.For(jobs, Id(3), Now, maxConcurrency: 3).Wait.Should().Be(QueueWait.Next);
    }

    [Fact]
    public void A_configured_concurrency_of_zero_is_read_as_one()
    {
        // What the worker itself does with it. Reporting from a different number than the queue runs
        // on is the whole class of bug this rule exists to avoid.
        QueuePosition.For([Job(1)], Id(1), Now, maxConcurrency: 0).Wait.Should().Be(QueueWait.Next);
    }

    // ---- not waiting at all ----

    [Fact]
    public void A_job_that_is_already_running_is_not_waiting()
    {
        QueuePosition.For([Job(1, status: JobStatus.Running)], Id(1), Now, 4)
            .Wait.Should().Be(QueueWait.NotQueued);
    }

    [Fact]
    public void A_settled_job_is_not_waiting()
    {
        QueuePosition.For([Job(1, status: JobStatus.Failed)], Id(1), Now, 4)
            .Wait.Should().Be(QueueWait.NotQueued);
    }

    [Fact]
    public void A_deployment_with_no_job_row_at_all_is_not_waiting()
    {
        // The reconciler settles rows; a deployment can outlive its job. Answering "first in the
        // queue" for one would be inventing a queue it is not in.
        QueuePosition.For([Job(1)], Id(99), Now, 4).Wait.Should().Be(QueueWait.NotQueued);
    }

    [Fact]
    public void A_job_serving_its_own_backoff_says_when_it_is_due()
    {
        var due = Now.AddMinutes(5);

        var place = QueuePosition.For([Job(1, nextAttemptAt: due)], Id(1), Now, 4);

        place.Wait.Should().Be(QueueWait.BackingOff);
        place.DueAt.Should().Be(due);
    }

    // ---- the sentence ----

    [Fact]
    public void The_sentence_names_how_many_jobs_are_in_front_and_what_they_are()
    {
        var place = QueuePosition.For(
        [
            Job(1, JobKind.Backup, excludesOn: 7, minutesOld: 10),
            Job(2, JobKind.Deployment, excludesOn: 8, minutesOld: 5),
            Job(3, JobKind.Deployment, excludesOn: 1, minutesOld: 1)
        ], Id(3), Now, maxConcurrency: 1);

        QueuePosition.Describe(place, persian: false)
            .Should().Be("3rd in the queue — waiting behind 2 jobs: a backup and another deployment.");
    }

    [Fact]
    public void The_sentence_counts_repeats_rather_than_listing_them()
    {
        var place = QueuePosition.For(
        [
            Job(1, JobKind.Backup, excludesOn: 7, minutesOld: 10),
            Job(2, JobKind.Backup, excludesOn: 8, minutesOld: 9),
            Job(3, JobKind.Deployment, excludesOn: 1, minutesOld: 1)
        ], Id(3), Now, maxConcurrency: 1);

        QueuePosition.Describe(place, persian: false)
            .Should().Be("3rd in the queue — waiting behind 2 jobs: 2 backups.");
    }

    [Fact]
    public void The_sentence_for_a_blocked_deployment_names_the_app_instead_of_a_number()
    {
        var place = QueuePosition.For(
        [
            Job(1, JobKind.Deployment, excludesOn: 1, status: JobStatus.Running, minutesOld: 4),
            Job(2, JobKind.Deployment, excludesOn: 1, minutesOld: 1)
        ], Id(2), Now, maxConcurrency: 4);

        QueuePosition.Describe(place, persian: false)
            .Should().Be("Blocked by another deployment of this app; only one may run at a time.");
    }

    [Fact]
    public void The_sentence_for_a_full_queue_names_the_running_jobs_rather_than_an_empty_list()
    {
        // The ordinary reason a deployment waits: DeploymentEngine.QueueDeploymentAsync coalesces a
        // redeploy onto the in-flight deployment rather than creating a second row, so
        // BlockedBySameTarget is reachable only through the narrow double-submit race. What an
        // ordinary busy install produces is this — nothing claimable ahead of it, but every slot
        // taken by running work that is not ahead of it at all. Ahead.Count == 0 must never render as
        // "waiting behind 0 jobs: nothing".
        var place = QueuePosition.For(
        [
            Job(1, JobKind.Backup, excludesOn: 7, status: JobStatus.Running, minutesOld: 10),
            Job(2, JobKind.CronRun, excludesOn: 8, status: JobStatus.Running, minutesOld: 9),
            Job(3, JobKind.Deployment, excludesOn: 1, minutesOld: 1)
        ], Id(3), Now, maxConcurrency: 2);

        place.Wait.Should().Be(QueueWait.Behind);
        place.Ahead.Should().BeEmpty();

        QueuePosition.Describe(place, persian: false)
            .Should().Be("1st in the queue — waiting for a worker to free up: 2 jobs running now.");
        QueuePosition.Describe(place, persian: true)
            .Should().Be("در صف، جایگاه 1 — منتظر آزاد شدن یک کارگر: 2 کار در حال اجراست.");
    }

    [Fact]
    public void The_sentence_for_a_full_queue_uses_the_singular_for_one_running_job()
    {
        var place = QueuePosition.For(
        [
            Job(1, JobKind.Backup, excludesOn: 7, status: JobStatus.Running, minutesOld: 10),
            Job(2, JobKind.Deployment, excludesOn: 1, minutesOld: 1)
        ], Id(2), Now, maxConcurrency: 1);

        place.Wait.Should().Be(QueueWait.Behind);
        QueuePosition.Describe(place, persian: false)
            .Should().Be("1st in the queue — waiting for a worker to free up: 1 job running now.");
    }

    [Fact]
    public void The_sentence_for_nothing_in_the_way_says_so()
    {
        QueuePosition.Describe(QueuePosition.For([Job(1)], Id(1), Now, 4), persian: false)
            .Should().Be("Next in the queue — it starts as soon as a worker picks it up.");
    }

    [Fact]
    public void A_job_that_is_not_waiting_has_nothing_to_say()
    {
        QueuePosition.Describe(QueuePosition.For([Job(1, status: JobStatus.Running)], Id(1), Now, 4),
            persian: false).Should().BeNull();
    }

    [Fact]
    public void Every_sentence_is_also_written_in_persian()
    {
        // The panel is bilingual and this text is the whole point of the feature; an English-only
        // explanation on a Persian page explains nothing.
        var places = new[]
        {
            QueuePosition.For([Job(1)], Id(1), Now, 4),
            QueuePosition.For(
            [
                Job(1, JobKind.Backup, excludesOn: 7, minutesOld: 10),
                Job(2, JobKind.Deployment, excludesOn: 1, minutesOld: 1)
            ], Id(2), Now, maxConcurrency: 1),
            QueuePosition.For(
            [
                Job(1, JobKind.Deployment, excludesOn: 1, status: JobStatus.Running, minutesOld: 4),
                Job(2, JobKind.Deployment, excludesOn: 1, minutesOld: 1)
            ], Id(2), Now, maxConcurrency: 4),
            QueuePosition.For([Job(1, nextAttemptAt: Now.AddMinutes(5))], Id(1), Now, 4)
        };

        foreach (var place in places)
        {
            var fa = QueuePosition.Describe(place, persian: true);
            fa.Should().NotBeNullOrWhiteSpace();
            fa.Should().NotBe(QueuePosition.Describe(place, persian: false));
        }
    }
}
