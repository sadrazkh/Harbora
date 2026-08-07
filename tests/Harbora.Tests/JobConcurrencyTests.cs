using FluentAssertions;
using Harbora.Domain.Jobs;
using Harbora.Infrastructure.Jobs;
using Harbora.Tests.Fakes;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Jobs run in parallel, but never two on the same target (HARBORA-0001).
///
/// <para>
/// The queue used to await one job to completion before it looked at the table again, so one
/// tenant's twenty-minute build was twenty minutes in which nobody else on the install could deploy,
/// and a six-hour snapshot would have stopped every backup, cron run and provision on the platform
/// for six hours.
/// </para>
///
/// <para>
/// Nothing here decides concurrency by waiting and hoping. Overlap is proved by holding a handler
/// open — a held handler cannot return, so anything else that enters is provably running beside it —
/// and exclusion is proved by the peak counters the handler keeps for itself, which are facts about
/// the whole run rather than about the instant a test happened to look. Where a bound does appear it
/// is only ever the ceiling on waiting for something that must happen, never the evidence that
/// something did not.
/// </para>
/// </summary>
public class JobConcurrencyTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    // ---- running in parallel ----

    [Fact]
    public async Task Two_jobs_for_different_targets_run_at_the_same_time()
    {
        using var h = new JobHarness();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        h.Handler.Hold(a);
        h.Handler.Hold(b);

        var slowBuild = await h.Queue().EnqueueAsync(JobKind.Deployment, a);
        h.Clock.UtcNow = h.Clock.UtcNow.AddSeconds(1);
        var otherApp = await h.Queue().EnqueueAsync(JobKind.Deployment, b);

        var worker = h.Worker(maxConcurrency: 2);
        await worker.StartAsync(default);
        h.Gate.Open();

        // The whole point, stated as a fact rather than a duration: the second job's handler is
        // entered while the first one's is still inside its body and cannot possibly have returned.
        await h.Handler.StartedFor(a).WaitAsync(Patience);
        await h.Handler.StartedFor(b).WaitAsync(Patience);
        h.Handler.MaxConcurrent.Should().Be(2);

        h.Handler.Release(a);
        h.Handler.Release(b);
        (await h.SettledAsync(slowBuild)).Status.Should().Be(JobStatus.Succeeded);
        (await h.SettledAsync(otherApp)).Status.Should().Be(JobStatus.Succeeded);
        await worker.StopAsync(default).WaitAsync(Patience);
    }

    [Fact]
    public async Task A_backlog_whose_oldest_row_is_blocked_still_moves_on_to_the_next_one()
    {
        using var h = new JobHarness();
        var busy = Guid.NewGuid();
        var free = Guid.NewGuid();
        h.Handler.Hold(busy);

        var running = await h.Queue().EnqueueAsync(JobKind.Backup, busy);
        h.Clock.UtcNow = h.Clock.UtcNow.AddSeconds(1);
        var blocked = await h.Queue().EnqueueAsync(JobKind.Backup, busy);
        h.Clock.UtcNow = h.Clock.UtcNow.AddSeconds(1);
        var eligible = await h.Queue().EnqueueAsync(JobKind.Deployment, free);

        var worker = h.Worker(maxConcurrency: 2);
        await worker.StartAsync(default);
        h.Gate.Open();

        await h.Handler.StartedFor(busy).WaitAsync(Patience);

        // The oldest claimable row is now the second backup of a target this process is already
        // inside. Skipping it must mean moving on, not idling: the row behind it is free to run.
        await h.Handler.StartedFor(free).WaitAsync(Patience);
        h.JobById(blocked)!.Status.Should().Be(JobStatus.Pending,
            "the row is due and oldest, and is waiting only because its target is busy");

        h.Handler.Release(busy);
        (await h.SettledAsync(running)).Status.Should().Be(JobStatus.Succeeded);
        (await h.SettledAsync(blocked)).Status.Should().Be(JobStatus.Succeeded,
            "held back is not the same as dropped");
        (await h.SettledAsync(eligible)).Status.Should().Be(JobStatus.Succeeded);
        await worker.StopAsync(default).WaitAsync(Patience);

        h.Handler.MaxConcurrentFor(busy).Should().Be(1);
    }

    // ---- never two on one target ----

    [Fact]
    public async Task Two_jobs_for_one_target_never_overlap_and_the_second_still_runs()
    {
        // Two snapshots of one backup target, or a deployment and the redeploy that followed it.
        // Oldest-first over a single worker used to give this ordering away for free.
        using var h = new JobHarness();
        var target = Guid.NewGuid();
        h.Handler.Hold(target);

        var first = await h.Queue().EnqueueAsync(JobKind.Backup, target);
        h.Clock.UtcNow = h.Clock.UtcNow.AddSeconds(1);
        var second = await h.Queue().EnqueueAsync(JobKind.Backup, target);

        var worker = h.Worker(maxConcurrency: 4);
        var inFlight = worker.RunNextAsync(default);
        await h.Handler.StartedFor(target).WaitAsync(Patience);

        // Asked at a moment when the first job is provably inside its handler: the second row is
        // due, is the oldest thing left, and must still not be claimed.
        (await worker.RunNextAsync(default)).Should().BeFalse(
            "this process is already running that target");
        h.Handler.Executed.Should().HaveCount(1);
        h.JobById(second)!.Status.Should().Be(JobStatus.Pending);

        h.Handler.Release(target);
        (await inFlight.WaitAsync(Patience)).Should().BeTrue();

        // Free again, and the work was only held back, not lost.
        (await worker.RunNextAsync(default)).Should().BeTrue();
        h.Handler.Executed.Should().HaveCount(2);
        h.Handler.MaxConcurrentFor(target).Should().Be(1);
        h.JobById(first)!.Status.Should().Be(JobStatus.Succeeded);
        h.JobById(second)!.Status.Should().Be(JobStatus.Succeeded);
    }

    [Fact]
    public async Task A_target_is_free_for_the_next_job_the_moment_the_last_one_settles()
    {
        // The exclusion must be released by finishing, not by anything else. A leaked reservation
        // would look exactly like a queue that had quietly stopped serving one app.
        using var h = new JobHarness();
        var target = Guid.NewGuid();
        var first = await h.Queue().EnqueueAsync(JobKind.ServiceProvision, target);
        h.Clock.UtcNow = h.Clock.UtcNow.AddSeconds(1);
        var second = await h.Queue().EnqueueAsync(JobKind.ServiceProvision, target);

        var worker = h.Worker(maxConcurrency: 4);
        (await worker.RunNextAsync(default)).Should().BeTrue();
        (await worker.RunNextAsync(default)).Should().BeTrue();

        h.JobById(first)!.Status.Should().Be(JobStatus.Succeeded);
        h.JobById(second)!.Status.Should().Be(JobStatus.Succeeded);
    }

    [Fact]
    public async Task A_target_whose_job_failed_is_released_too()
    {
        using var h = new JobHarness();
        var target = Guid.NewGuid();
        h.Handler.FailWith(target, new InvalidOperationException("no such image"));
        var first = await h.Queue().EnqueueAsync(JobKind.Backup, target);
        h.Clock.UtcNow = h.Clock.UtcNow.AddSeconds(1);
        var second = await h.Queue().EnqueueAsync(JobKind.Backup, target);

        var worker = h.Worker(maxConcurrency: 4);
        await worker.RunNextAsync(default);

        // A failure is a finish. If it did not release the target, one bad backup would end that
        // target's backups until the panel was restarted.
        (await worker.RunNextAsync(default)).Should().BeTrue();
        h.JobById(first)!.Status.Should().Be(JobStatus.Failed);
        h.JobById(second)!.Status.Should().Be(JobStatus.Failed);
    }

    [Fact]
    public async Task A_full_queue_of_repeated_targets_drains_without_any_target_doubling_up()
    {
        // The staged cases above each prove one interleaving. This one runs a real backlog through
        // the loop at full width and asks the same question of whatever interleavings the scheduler
        // actually chose: every job ran, every job finished, and no target was ever inside its
        // handler twice at once.
        using var h = new JobHarness();
        var targets = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToArray();
        foreach (var target in targets) h.Handler.Hold(target);

        var queued = new List<Guid>();
        foreach (var _ in Enumerable.Range(0, 6))
        foreach (var target in targets)
        {
            queued.Add(await h.Queue().EnqueueAsync(JobKind.Backup, target));
            h.Clock.UtcNow = h.Clock.UtcNow.AddMilliseconds(1);
        }

        // Four slots and three targets, all three held. The fourth slot is free and there are
        // fifteen due rows left, every one of them for a target that is busy: the only thing that
        // may happen next is nothing.
        var worker = h.Worker(maxConcurrency: 4);
        await worker.StartAsync(default);
        h.Gate.Open();

        foreach (var target in targets) await h.Handler.StartedFor(target).WaitAsync(Patience);
        h.Handler.MaxConcurrent.Should().Be(3, "three targets were free to start, and did");
        foreach (var target in targets)
            h.Handler.MaxConcurrentFor(target).Should().Be(1, "and the spare slot had nothing it was allowed to take");

        foreach (var target in targets) h.Handler.Release(target);
        foreach (var jobId in queued)
            (await h.SettledAsync(jobId, TimeSpan.FromSeconds(30))).Status.Should().Be(JobStatus.Succeeded);

        await worker.StopAsync(default).WaitAsync(Patience);

        h.Handler.Executed.Should().HaveCount(queued.Count);
        foreach (var target in targets)
            h.Handler.MaxConcurrentFor(target).Should().Be(1, "one target is one queue of its own");
    }

    // ---- one job at a time, which is where the platform came from ----

    [Fact]
    public async Task One_at_a_time_is_still_available_and_is_exactly_the_old_worker()
    {
        // The rollback path. An operator who suspects the queue sets Jobs:MaxConcurrency to 1 and
        // restarts, and gets back the worker the platform ran for its whole life until now.
        using var h = new JobHarness();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        h.Handler.Hold(a);
        h.Handler.Hold(b);

        var firstJob = await h.Queue().EnqueueAsync(JobKind.Deployment, a);
        h.Clock.UtcNow = h.Clock.UtcNow.AddSeconds(1);
        var secondJob = await h.Queue().EnqueueAsync(JobKind.Deployment, b);

        var worker = h.Worker(maxConcurrency: 1);
        await worker.StartAsync(default);
        h.Gate.Open();

        await h.Handler.StartedFor(a).WaitAsync(Patience);
        h.JobById(secondJob)!.Status.Should().Be(JobStatus.Pending,
            "nothing else may even be claimed while the one slot is taken");

        h.Handler.Release(a);
        await h.Handler.StartedFor(b).WaitAsync(Patience);
        h.Handler.Release(b);

        (await h.SettledAsync(firstJob)).Status.Should().Be(JobStatus.Succeeded);
        (await h.SettledAsync(secondJob)).Status.Should().Be(JobStatus.Succeeded);
        await worker.StopAsync(default).WaitAsync(Patience);

        // Recorded by the work itself, over the whole run: at no instant were two jobs inside their
        // handlers together.
        h.Handler.MaxConcurrent.Should().Be(1);
    }

    // ---- one job's failure is its own ----

    [Fact]
    public async Task A_job_that_fails_leaves_the_one_running_beside_it_alone()
    {
        using var h = new JobHarness();
        var slow = Guid.NewGuid();
        var doomed = Guid.NewGuid();
        h.Handler.Hold(slow);
        h.Handler.FailWith(doomed, new InvalidOperationException("the base image is gone"));

        var slowJob = await h.Queue().EnqueueAsync(JobKind.Deployment, slow);
        h.Clock.UtcNow = h.Clock.UtcNow.AddSeconds(1);
        var doomedJob = await h.Queue().EnqueueAsync(JobKind.Deployment, doomed);

        var worker = h.Worker(maxConcurrency: 2);
        var running = worker.RunNextAsync(default);
        await h.Handler.StartedFor(slow).WaitAsync(Patience);

        // Fails while the other one is provably still in flight: its own scope, its own linked
        // token, its own settle.
        (await worker.RunNextAsync(default)).Should().BeTrue();
        h.JobById(doomedJob)!.Status.Should().Be(JobStatus.Failed);
        h.JobById(doomedJob)!.Error.Should().Contain("the base image is gone");
        h.JobById(slowJob)!.Status.Should().Be(JobStatus.Running,
            "a failure next door must not cancel work that is going fine");

        h.Handler.Release(slow);
        (await running.WaitAsync(Patience)).Should().BeTrue();
        h.JobById(slowJob)!.Status.Should().Be(JobStatus.Succeeded);
    }

    // ---- shutdown ----

    [Fact]
    public async Task Shutdown_returns_every_job_in_flight_to_pending()
    {
        using var h = new JobHarness();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        h.Handler.BlockUntilCancelled = true;

        var firstJob = await h.Queue().EnqueueAsync(JobKind.Deployment, a);
        h.Clock.UtcNow = h.Clock.UtcNow.AddSeconds(1);
        var secondJob = await h.Queue().EnqueueAsync(JobKind.Backup, b);

        var worker = h.Worker(maxConcurrency: 2);
        await worker.StartAsync(default);
        h.Gate.Open();
        await h.Handler.StartedFor(a).WaitAsync(Patience);
        await h.Handler.StartedFor(b).WaitAsync(Patience);

        await worker.StopAsync(default).WaitAsync(Patience);

        // Read with no further waiting at all. That is the assertion: the worker does not return
        // from its loop until every job it claimed has been settled, so a host that has finished
        // stopping has left nothing half-recorded behind it.
        foreach (var jobId in new[] { firstJob, secondJob })
        {
            var job = h.JobById(jobId)!;
            job.Status.Should().Be(JobStatus.Pending, "the work was owed, not failed");
            job.FinishedAt.Should().BeNull();
            job.ClaimedBy.Should().BeNull("a released claim must not look like it is still owned");
        }
    }

    // ---- how much at once ----

    [Fact]
    public void The_default_is_four_or_the_core_count_where_that_is_smaller()
    {
        new JobQueueOptions().MaxConcurrency
            .Should().Be(Math.Min(4, Environment.ProcessorCount))
            .And.BeGreaterThanOrEqualTo(1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_configured_zero_still_runs_one_job_rather_than_none(int configured)
    {
        // A queue that accepts work, records it and never runs any of it is a stopped platform that
        // looks like a working one.
        new JobQueueOptions { MaxConcurrency = configured }.EffectiveMaxConcurrency.Should().Be(1);
    }

    [Fact]
    public void A_configured_value_is_used_as_written()
    {
        new JobQueueOptions { MaxConcurrency = 9 }.EffectiveMaxConcurrency.Should().Be(9);
    }

    // ---- the set the claim consults ----

    [Fact]
    public void A_target_can_only_be_held_once()
    {
        var targets = new InFlightTargets();
        var id = Guid.NewGuid();

        targets.TryReserve(JobKind.Backup, id).Should().BeTrue();
        targets.TryReserve(JobKind.Backup, id).Should().BeFalse();

        // Same id, different work: a backup of a service and a deployment of an app are not each
        // other's target, whatever their identifiers happen to be.
        targets.TryReserve(JobKind.Deployment, id).Should().BeTrue();

        targets.Release(JobKind.Backup, id);
        targets.TryReserve(JobKind.Backup, id).Should().BeTrue();
        targets.Count.Should().Be(2);
    }

    [Fact]
    public void Releasing_something_never_held_changes_nothing()
    {
        var targets = new InFlightTargets();
        targets.Release(JobKind.Backup, Guid.NewGuid());
        targets.Count.Should().Be(0);
    }
}
