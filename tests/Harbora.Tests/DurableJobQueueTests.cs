using FluentAssertions;
using Harbora.Domain.Jobs;
using Harbora.Infrastructure.Jobs;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
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

    /// <summary>
    /// P5's whole scoping story rests on this column actually being populated by the caller who
    /// enqueues, rather than left null — <c>/activity</c> filters on it by hand (Job carries no query
    /// filter, see <c>Job.WorkspaceId</c>'s own doc comment), so a queue that silently dropped it
    /// would make every job in the platform invisible to the page meant to show it.
    /// </summary>
    [Fact]
    public async Task Enqueuing_with_a_workspace_id_stamps_it_on_the_row()
    {
        using var h = new JobHarness();
        var target = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();

        await h.Queue().EnqueueAsync(JobKind.Deployment, target, workspaceId);

        h.JobFor(target)!.WorkspaceId.Should().Be(workspaceId);
    }

    /// <summary>The platform-level case — a billing tick, or an enqueue nobody attached a workspace
    /// to — must not silently invent one; the row stays unowned rather than getting a wrong owner.</summary>
    [Fact]
    public async Task Enqueuing_with_no_workspace_id_leaves_the_row_unowned()
    {
        using var h = new JobHarness();
        var target = Guid.NewGuid();

        await h.Queue().EnqueueAsync(JobKind.Deployment, target);

        h.JobFor(target)!.WorkspaceId.Should().BeNull();
    }

    [Fact]
    public async Task Enqueuing_the_exclusive_way_stamps_the_workspace_id_too()
    {
        using var h = new JobHarness();
        var target = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();

        await h.Queue().EnqueueExclusiveAsync(JobKind.Deployment, target, Guid.NewGuid(), workspaceId);

        h.JobFor(target)!.WorkspaceId.Should().Be(workspaceId);
    }

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
    public async Task An_enormous_error_message_is_cut_down_to_what_the_row_can_hold()
    {
        // Job.Error is a bounded column. A message longer than it throws on save in Postgres, inside
        // a SettleAsync whose catch swallows the failure — so the row would stay Running until the
        // next restart reconciled it, and the message it was trying to record would be lost
        // entirely. A build that echoes a whole log into its exception is not a rare shape.
        using var h = new JobHarness();
        var target = Guid.NewGuid();
        h.Handler.Failure = new InvalidOperationException(new string('x', 5000));
        await h.Queue().EnqueueAsync(JobKind.Deployment, target);

        await h.Worker().RunNextAsync(default);

        // Read the cap from the model rather than repeating it: this must follow the schema.
        using var db = h.NewDb();
        var cap = db.Model.FindEntityType(typeof(Job))!.FindProperty(nameof(Job.Error))!.GetMaxLength()!.Value;

        var job = h.JobFor(target)!;
        job.Status.Should().Be(JobStatus.Failed);
        job.Error!.Length.Should().BeLessThanOrEqualTo(cap,
            "the in-memory provider does not enforce the length, and Postgres does");
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
    public async Task A_job_whose_work_swallows_the_deadline_is_still_failed()
    {
        // The worker may not take the callee's word for it. Every real dispatch target catches
        // Exception at its top level and writes the failure into its own domain row —
        // DeploymentPipeline into the deployment, CronJobRunner into the run, ManagedServiceEngine
        // into the service — so a killed job can return to the worker looking exactly like a
        // finished one. Recording Succeeded there would be the platform lying about work it just
        // killed, which is the one thing this phase exists to stop.
        using var h = new JobHarness();
        var target = Guid.NewGuid();
        h.Handler.BlockUntilCancelled = true;
        h.Handler.OnCancellation = StubCancellation.Swallow;
        await h.Queue().EnqueueAsync(JobKind.Deployment, target);

        await h.Worker(TimeSpan.FromMilliseconds(200)).RunNextAsync(default);

        var job = h.JobFor(target)!;
        job.Status.Should().Be(JobStatus.Failed, "the deadline fired, whatever the work said afterwards");
        job.Error.Should().Contain("given up on");
        job.FinishedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task A_deadline_that_arrives_as_a_broken_connection_is_still_a_deadline()
    {
        // A cancelled token usually reaches the worker wearing another exception's clothes: a socket
        // torn down mid-transfer surfaces as IOException, which the policy calls retryable. Judged on
        // the exception alone, a snapshot killed at its seven-hour deadline would be queued to spend
        // another seven hours. The token is the worker's own fact and outranks the account of it.
        using var h = new JobHarness();
        var target = Guid.NewGuid();
        h.Handler.BlockUntilCancelled = true;
        h.Handler.OnCancellation = StubCancellation.SurfaceAsBrokenConnection;
        await h.Queue().EnqueueAsync(JobKind.ServiceProvision, target);

        await h.Worker(TimeSpan.FromMilliseconds(200)).RunNextAsync(default);

        var job = h.JobFor(target)!;
        job.Status.Should().Be(JobStatus.Failed, "a job that spent its whole allowance is not a transient fault");
        job.NextAttemptAt.Should().BeNull();
        job.Error.Should().Contain("given up on");
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

    // ---- the startup gate ----

    [Fact]
    public async Task The_worker_claims_nothing_until_startup_reconciliation_has_finished()
    {
        // The race this closes: the reconcilers are hosted services whose StartAsync runs to
        // completion, but the worker is a BackgroundService whose StartAsync returns at its first
        // await — so its claim loop used to run alongside DeploymentReconciler, and could re-dispatch
        // a deployment the reconciler was in the middle of marking Failed.
        using var h = new JobHarness();
        var target = Guid.NewGuid();
        await h.Queue().EnqueueAsync(JobKind.Deployment, target);

        var worker = h.Worker();
        await worker.StartAsync(default);

        // Wait for the positive fact that the worker is parked at the gate, rather than racing a
        // timer against "it did not reach the queue yet" — a bound long enough to be safe on a loaded
        // machine would also be long enough to hide a regression on one that is fast. WaitStarted
        // completes the instant JobStartupGate.WaitAsync is entered, which in ExecuteAsync's single,
        // un-forked call stack strictly precedes the claim loop — so once it is observed, the loop
        // provably has not run yet.
        await h.Gate.WaitStarted.WaitAsync(TimeSpan.FromSeconds(10));
        h.ClaimAttempted.IsCompleted.Should().BeFalse(
            "the reconcilers have not finished deciding what this work means");
        h.Handler.Executed.Should().BeEmpty();
        h.JobFor(target)!.Status.Should().Be(JobStatus.Pending);

        h.Gate.Open();

        await h.Handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await worker.StopAsync(default);

        h.Handler.Executed.Should().ContainSingle().Which.TargetId.Should().Be(target,
            "once startup is over the queue drains exactly as before");
        // The ordering itself, recorded as it happened rather than sampled: whatever the scheduler
        // did with the two threads, no claim may have started before the gate opened.
        h.ScopesTakenBeforeTheGateOpened.Should().Be(0);
    }

    [Fact]
    public async Task Waiting_at_the_gate_ends_when_the_host_stops_even_if_it_never_opens()
    {
        // The property the whole design turns on. A gate that could only be released by Open() would
        // turn a startup that fails part-way — or a host stopped while it was still starting — into a
        // shutdown that never finishes, because stopping a BackgroundService means waiting for
        // ExecuteAsync to return.
        var gate = new JobStartupGate();
        using var stopping = new CancellationTokenSource();
        var waiting = gate.WaitAsync(stopping.Token);

        await stopping.CancelAsync();

        var ended = await Record.ExceptionAsync(() => waiting.WaitAsync(TimeSpan.FromSeconds(5)));
        ended.Should().BeAssignableTo<OperationCanceledException>(
            "the wait has to end when the host stops, not only when the gate opens");
        gate.IsOpen.Should().BeFalse("nothing opened it — the waiter left of its own accord");
    }

    [Fact]
    public async Task A_worker_stopped_before_the_gate_opens_leaves_instead_of_waiting()
    {
        using var h = new JobHarness();
        var worker = h.Worker();
        await worker.StartAsync(default);
        await worker.LoopEntered.WaitAsync(TimeSpan.FromSeconds(10));

        await worker.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(10));

        // Not just "StopAsync returned": the loop itself must be finished, or the host is holding a
        // thread at a gate nobody is ever going to open. This is what makes the worker pass its own
        // stopping token to the gate rather than waiting unconditionally.
        worker.ExecuteTask!.IsCompleted.Should().BeTrue("the worker has to leave when it is told to");
        h.Gate.IsOpen.Should().BeFalse();
    }

    [Fact]
    public async Task Nothing_is_left_waiting_at_a_gate_the_host_will_never_open()
    {
        // The opener is a hosted service registered after the reconcilers. A host that fails part-way
        // through startup never reaches its StartAsync — but every hosted service is still stopped,
        // so opening here is what guarantees no waiter outlives the host.
        var gate = new JobStartupGate();
        var opener = new JobStartupGateOpener(gate, NullLogger<JobStartupGateOpener>.Instance);

        await opener.StopAsync(default);

        gate.IsOpen.Should().BeTrue();
    }

    [Fact]
    public async Task The_gate_opens_when_the_startup_services_before_it_have_run()
    {
        var gate = new JobStartupGate();
        var opener = new JobStartupGateOpener(gate, NullLogger<JobStartupGateOpener>.Instance);
        gate.IsOpen.Should().BeFalse("a gate that starts open is not a gate");

        await opener.StartAsync(default);

        gate.IsOpen.Should().BeTrue();
        await gate.WaitAsync(default);
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
