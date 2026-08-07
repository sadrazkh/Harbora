using FluentAssertions;
using Harbora.Domain.Jobs;
using Harbora.Tests.Fakes;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Durable job queue (doc 15, Phase D — completes P3). The point of persisting jobs is that work
/// survives a restart and can be stopped once started; these tests assert exactly that, plus the
/// bookkeeping that makes it safe (claim once, settle correctly, never re-run blindly).
/// </summary>
public class DurableJobQueueTests
{
    // ---- durability ----

    [Fact]
    public async Task Enqueuing_persists_the_work_before_it_runs()
    {
        using var h = new JobHarness();
        var target = Guid.NewGuid();

        await h.Queue().EnqueueAsync(JobKind.Deployment, target);

        // This is the whole point: the work exists in the database, not only in memory.
        var job = h.JobFor(target);
        job.Should().NotBeNull();
        job!.Status.Should().Be(JobStatus.Pending);
        job.Kind.Should().Be(JobKind.Deployment);
        h.Handler.Executed.Should().BeEmpty("enqueuing must not execute anything inline");
    }

    [Fact]
    public async Task A_pending_job_is_picked_up_and_settled()
    {
        using var h = new JobHarness();
        var target = Guid.NewGuid();
        await h.Queue().EnqueueAsync(JobKind.Backup, target);

        var ran = await h.Worker().RunNextAsync(default);

        ran.Should().BeTrue();
        h.Handler.Executed.Should().ContainSingle().Which.Should().Be((JobKind.Backup, target));

        var job = h.JobFor(target)!;
        job.Status.Should().Be(JobStatus.Succeeded);
        job.StartedAt.Should().NotBeNull();
        job.FinishedAt.Should().NotBeNull();
        job.Attempts.Should().Be(1);
    }

    [Fact]
    public async Task A_job_enqueued_before_a_restart_still_runs_afterwards()
    {
        using var h = new JobHarness();
        var target = Guid.NewGuid();
        await h.Queue().EnqueueAsync(JobKind.Deployment, target);

        // Simulate the restart: everything in memory is gone, only the database survives. A brand
        // new worker must find the work on its own — with the old channel queue it never would.
        var afterRestart = h.Worker();
        await afterRestart.RunNextAsync(default);

        h.Handler.Executed.Should().ContainSingle().Which.TargetId.Should().Be(target);
    }

    [Fact]
    public async Task Jobs_run_oldest_first()
    {
        using var h = new JobHarness();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        await h.Queue().EnqueueAsync(JobKind.Deployment, first);
        h.Clock.UtcNow = h.Clock.UtcNow.AddSeconds(1);
        await h.Queue().EnqueueAsync(JobKind.Deployment, second);

        var worker = h.Worker();
        await worker.RunNextAsync(default);
        await worker.RunNextAsync(default);

        h.Handler.Executed.Select(e => e.TargetId).Should().Equal(first, second);
    }

    [Fact]
    public async Task An_empty_queue_reports_no_work()
    {
        using var h = new JobHarness();

        (await h.Worker().RunNextAsync(default)).Should().BeFalse();
    }

    [Fact]
    public async Task A_claimed_job_is_not_handed_out_again()
    {
        using var h = new JobHarness();
        await h.Queue().EnqueueAsync(JobKind.Deployment, Guid.NewGuid());

        var worker = h.Worker();
        await worker.RunNextAsync(default);
        var secondPass = await worker.RunNextAsync(default);

        secondPass.Should().BeFalse("a settled job must never be executed twice");
        h.Handler.Executed.Should().HaveCount(1);
    }

    // ---- failure ----

    [Fact]
    public async Task A_failing_job_is_recorded_with_its_error_and_not_retried()
    {
        using var h = new JobHarness();
        var target = Guid.NewGuid();
        h.Handler.Failure = new InvalidOperationException("build exploded");
        await h.Queue().EnqueueAsync(JobKind.Deployment, target);

        await h.Worker().RunNextAsync(default);

        var job = h.JobFor(target)!;
        job.Status.Should().Be(JobStatus.Failed);
        job.Error.Should().Contain("build exploded");

        // Deployments have side effects; a blind retry could compound them.
        (await h.Worker().RunNextAsync(default)).Should().BeFalse();
    }

    [Fact]
    public async Task A_failing_job_does_not_stop_the_next_one()
    {
        using var h = new JobHarness();
        h.Handler.Failure = new InvalidOperationException("nope");
        await h.Queue().EnqueueAsync(JobKind.Deployment, Guid.NewGuid());
        h.Clock.UtcNow = h.Clock.UtcNow.AddSeconds(1);
        var second = Guid.NewGuid();
        await h.Queue().EnqueueAsync(JobKind.Backup, second);

        var worker = h.Worker();
        await worker.RunNextAsync(default);
        h.Handler.Failure = null;
        await worker.RunNextAsync(default);

        h.JobFor(second)!.Status.Should().Be(JobStatus.Succeeded);
    }

    // ---- deadlines ----

    [Fact]
    public async Task A_job_that_never_finishes_is_failed_at_its_deadline()
    {
        // The defect this replaces: nothing bounded a dispatched job. A docker build hanging against
        // a live daemon ran until someone killed the process, and the worker runs one job at a time,
        // so it held every other tenant's work behind it.
        using var h = new JobHarness();
        var target = Guid.NewGuid();
        h.Handler.BlockUntilCancelled = true;
        await h.Queue().EnqueueAsync(JobKind.Deployment, target);

        await h.Worker(TimeSpan.FromMilliseconds(200)).RunNextAsync(default);

        var job = h.JobFor(target)!;
        job.Status.Should().Be(JobStatus.Failed, "the work genuinely did not finish, and saying so is the point");
        job.Error.Should().Contain("given up on");
        job.Error.Should().Contain("second", "the message has to name the limit that was spent");
        job.FinishedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task The_queue_carries_on_after_a_job_hits_its_deadline()
    {
        using var h = new JobHarness();
        var first = Guid.NewGuid();
        h.Handler.BlockUntilCancelled = true;
        await h.Queue().EnqueueAsync(JobKind.Deployment, first);
        h.Clock.UtcNow = h.Clock.UtcNow.AddSeconds(1);
        var second = Guid.NewGuid();
        await h.Queue().EnqueueAsync(JobKind.Backup, second);

        var worker = h.Worker(TimeSpan.FromMilliseconds(200));
        await worker.RunNextAsync(default);
        h.Handler.BlockUntilCancelled = false;
        await worker.RunNextAsync(default);

        h.JobFor(first)!.Status.Should().Be(JobStatus.Failed);
        h.JobFor(second)!.Status.Should().Be(JobStatus.Succeeded,
            "one hung job must cost the platform that job, not the queue");
    }

    [Fact]
    public async Task A_job_killed_at_its_deadline_is_not_tried_again()
    {
        // Provisioning is retryable work, but a deadline is not a transient fault: the job spent its
        // whole allowance and finished nothing, and a second run would spend it again.
        using var h = new JobHarness();
        var target = Guid.NewGuid();
        h.Handler.BlockUntilCancelled = true;
        await h.Queue().EnqueueAsync(JobKind.ServiceProvision, target);

        await h.Worker(TimeSpan.FromMilliseconds(200)).RunNextAsync(default);

        var job = h.JobFor(target)!;
        job.Status.Should().Be(JobStatus.Failed);
        job.NextAttemptAt.Should().BeNull();
    }

    // ---- retry ----

    [Fact]
    public async Task A_transient_failure_on_repeatable_work_is_scheduled_to_run_again()
    {
        using var h = new JobHarness();
        var target = Guid.NewGuid();
        h.Handler.Failure = new HttpRequestException("the daemon refused the connection");
        await h.Queue().EnqueueAsync(JobKind.ServiceProvision, target);

        await h.Worker().RunNextAsync(default);

        var job = h.JobFor(target)!;
        job.Status.Should().Be(JobStatus.Pending, "the work never happened, so it is still owed");
        job.NextAttemptAt.Should().Be(h.Clock.UtcNow.AddMinutes(1));
        job.Error.Should().Contain("refused", "the row still has to explain the attempt that failed");
        job.FinishedAt.Should().BeNull();
        job.ClaimedBy.Should().BeNull("a released claim must not look like it is still owned");
    }

    [Fact]
    public async Task Work_waiting_for_its_backoff_is_not_claimed_until_the_wait_has_passed()
    {
        using var h = new JobHarness();
        var target = Guid.NewGuid();
        h.Handler.Failure = new HttpRequestException("the daemon refused the connection");
        await h.Queue().EnqueueAsync(JobKind.ServiceProvision, target);

        var worker = h.Worker();
        await worker.RunNextAsync(default);

        (await worker.RunNextAsync(default)).Should().BeFalse(
            "claiming it straight back would be a retry loop, not a backoff");
        h.Handler.Executed.Should().HaveCount(1);

        // The worker polls every few seconds whatever happens, so nothing has to wake it for this.
        h.Clock.UtcNow = h.Clock.UtcNow.AddMinutes(1);
        h.Handler.Failure = null;

        (await worker.RunNextAsync(default)).Should().BeTrue();
        var job = h.JobFor(target)!;
        job.Status.Should().Be(JobStatus.Succeeded);
        job.Attempts.Should().Be(2);
    }

    [Fact]
    public async Task A_failure_that_would_happen_again_is_not_retried()
    {
        // A bad image reference fails identically the second time. Retrying it wastes the queue and
        // buries the message the operator needs under three copies of itself.
        using var h = new JobHarness();
        var target = Guid.NewGuid();
        h.Handler.Failure = new InvalidOperationException("no such image");
        await h.Queue().EnqueueAsync(JobKind.ServiceProvision, target);

        await h.Worker().RunNextAsync(default);

        var job = h.JobFor(target)!;
        job.Status.Should().Be(JobStatus.Failed);
        job.NextAttemptAt.Should().BeNull();
    }

    [Fact]
    public async Task Work_that_has_used_its_attempts_is_failed_rather_than_queued_again()
    {
        using var h = new JobHarness();
        var target = Guid.NewGuid();
        using (var db = h.NewDb())
        {
            // Two attempts already spent; provisioning is allowed three.
            db.Jobs.Add(new Job
            {
                Kind = JobKind.ServiceProvision, TargetId = target,
                Status = JobStatus.Pending, Attempts = 2
            });
            await db.SaveChangesAsync();
        }
        h.Handler.Failure = new HttpRequestException("still refused");

        await h.Worker().RunNextAsync(default);

        var job = h.JobFor(target)!;
        job.Status.Should().Be(JobStatus.Failed, "a budget that never runs out is not a budget");
        job.Attempts.Should().Be(3);
        job.NextAttemptAt.Should().BeNull();
    }

    [Fact]
    public async Task A_deployment_is_never_queued_again_however_it_failed()
    {
        // Even a textbook transient fault. Half a deployment may already have happened — an image
        // pushed, a container started, the proxy repointed — and an unattended repeat compounds it.
        using var h = new JobHarness();
        var target = Guid.NewGuid();
        h.Handler.Failure = new HttpRequestException("the daemon refused the connection");
        await h.Queue().EnqueueAsync(JobKind.Deployment, target);

        var worker = h.Worker();
        await worker.RunNextAsync(default);

        var job = h.JobFor(target)!;
        job.Status.Should().Be(JobStatus.Failed);
        job.NextAttemptAt.Should().BeNull();
        (await worker.RunNextAsync(default)).Should().BeFalse();
    }

    // ---- cancellation ----

    [Fact]
    public async Task Cancelling_a_pending_job_stops_it_ever_running()
    {
        using var h = new JobHarness();
        var target = Guid.NewGuid();
        await h.Queue().EnqueueAsync(JobKind.Deployment, target);

        var cancelled = await h.Queue().RequestCancellationAsync(JobKind.Deployment, target);

        cancelled.Should().BeTrue();
        h.JobFor(target)!.Status.Should().Be(JobStatus.Cancelled);

        (await h.Worker().RunNextAsync(default)).Should().BeFalse();
        h.Handler.Executed.Should().BeEmpty("cancelled work must never be dispatched");
    }

    [Fact]
    public async Task Cancelling_a_running_job_interrupts_the_work()
    {
        using var h = new JobHarness();
        var target = Guid.NewGuid();
        h.Handler.BlockUntilCancelled = true;
        await h.Queue().EnqueueAsync(JobKind.Deployment, target);

        // Start the job, wait until the handler is genuinely inside its work, then cancel it. With
        // the old queue this was impossible — nothing held a reference to the running work.
        var running = h.Worker().RunNextAsync(default);
        await h.Handler.Started.Task;

        await h.Queue().RequestCancellationAsync(JobKind.Deployment, target);
        await running;

        var job = h.JobFor(target)!;
        job.Status.Should().Be(JobStatus.Cancelled);
        job.CancelRequested.Should().BeTrue();
        job.Error.Should().Contain("Cancelled");
    }

    [Fact]
    public async Task A_cancelled_job_that_a_restart_returned_to_pending_is_still_not_run()
    {
        // Cancel arrives while the job runs, then the host stops before it settles: the claim is
        // released back to Pending but CancelRequested stays set. On restart the worker must honour
        // it, otherwise a shutdown would quietly resurrect work the user already stopped.
        using var h = new JobHarness();
        var target = Guid.NewGuid();
        using (var db = h.NewDb())
        {
            db.Jobs.Add(new Job
            {
                Kind = JobKind.Deployment, TargetId = target,
                Status = JobStatus.Pending, CancelRequested = true, Attempts = 1
            });
            await db.SaveChangesAsync();
        }

        var ran = await h.Worker().RunNextAsync(default);

        ran.Should().BeFalse();
        h.Handler.Executed.Should().BeEmpty();
        h.JobFor(target)!.Status.Should().Be(JobStatus.Cancelled);
    }

    [Fact]
    public async Task Cancelling_when_nothing_is_live_reports_false()
    {
        using var h = new JobHarness();

        (await h.Queue().RequestCancellationAsync(JobKind.Deployment, Guid.NewGuid()))
            .Should().BeFalse();
    }

    [Fact]
    public async Task A_settled_job_cannot_be_cancelled()
    {
        using var h = new JobHarness();
        var target = Guid.NewGuid();
        await h.Queue().EnqueueAsync(JobKind.Deployment, target);
        await h.Worker().RunNextAsync(default);

        var cancelled = await h.Queue().RequestCancellationAsync(JobKind.Deployment, target);

        cancelled.Should().BeFalse();
        h.JobFor(target)!.Status.Should().Be(JobStatus.Succeeded, "a finished job must not be rewritten");
    }

    [Fact]
    public async Task Shutdown_returns_the_job_to_pending_rather_than_failing_it()
    {
        using var h = new JobHarness();
        var target = Guid.NewGuid();
        h.Handler.BlockUntilCancelled = true;
        await h.Queue().EnqueueAsync(JobKind.Deployment, target);

        using var shutdown = new CancellationTokenSource();
        var running = h.Worker().RunNextAsync(shutdown.Token);
        await h.Handler.Started.Task;
        await shutdown.CancelAsync();     // host is stopping, not a user cancelling
        await running;

        var job = h.JobFor(target)!;
        job.Status.Should().Be(JobStatus.Pending, "the work was never done, so it must be resumed after restart");
        job.FinishedAt.Should().BeNull();
        job.ClaimedBy.Should().BeNull("a released claim must not look like it is still owned");
    }

    // ---- reconciliation ----

    [Fact]
    public async Task Jobs_left_running_by_a_crash_are_settled_on_startup()
    {
        using var h = new JobHarness();
        var target = Guid.NewGuid();
        using (var db = h.NewDb())
        {
            db.Jobs.Add(new Job
            {
                Kind = JobKind.Deployment, TargetId = target,
                Status = JobStatus.Running, ClaimedBy = "dead-worker", Attempts = 1
            });
            await db.SaveChangesAsync();
        }

        var settled = await h.Reconciler().ReconcileAsync(default);

        settled.Should().Be(1);
        var job = h.JobFor(target)!;
        job.Status.Should().Be(JobStatus.Failed);
        job.Error.Should().Contain("restart");
        job.FinishedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Reconciliation_leaves_pending_work_alone()
    {
        using var h = new JobHarness();
        var target = Guid.NewGuid();
        await h.Queue().EnqueueAsync(JobKind.Deployment, target);

        await h.Reconciler().ReconcileAsync(default);

        h.JobFor(target)!.Status.Should().Be(JobStatus.Pending,
            "queued work is exactly what should survive a restart");
    }

    [Fact]
    public async Task Reconciliation_is_idempotent()
    {
        using var h = new JobHarness();
        using (var db = h.NewDb())
        {
            db.Jobs.Add(new Job { Kind = JobKind.Backup, TargetId = Guid.NewGuid(), Status = JobStatus.Running });
            await db.SaveChangesAsync();
        }

        (await h.Reconciler().ReconcileAsync(default)).Should().Be(1);
        (await h.Reconciler().ReconcileAsync(default)).Should().Be(0, "running it again must change nothing");
    }
}
