using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Jobs;
using Harbora.Infrastructure.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Harbora.Tests.Fakes;

/// <summary>
/// Wires a real <see cref="DatabaseJobQueue"/>, <see cref="JobWorker"/> and
/// <see cref="JobReconciler"/> over an in-memory database. Only the dispatch target is stubbed, so
/// claiming, the concurrency stamp, cancellation and settling are all the production code path.
/// </summary>
public sealed class JobHarness : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly string _dbName = "jobs-" + Guid.NewGuid();

    public FixedClock Clock { get; } = new();
    public ObservableJobSignal Signal { get; } = new();
    public JobCancellationRegistry Cancellations { get; } = new();
    public StubJobHandler Handler { get; } = new();

    /// <summary>
    /// Closed, exactly as it is when the host starts. A test that drives the worker's loop has to
    /// open it; one that drives <c>RunNextAsync</c> directly is past the gate already.
    /// </summary>
    public ObservableJobStartupGate Gate { get; } = new();

    private readonly CountingScopeFactory _scopes;

    public JobHarness()
    {
        var services = new ServiceCollection();
        services.AddDbContext<HarboraDbContext>(o => o.UseInMemoryDatabase(_dbName));
        _provider = services.BuildServiceProvider();
        _scopes = new CountingScopeFactory(
            _provider.GetRequiredService<IServiceScopeFactory>(), () => Gate.IsOpen);
    }

    public IServiceScopeFactory Scopes => _scopes;

    /// <summary>
    /// Completes the first time anything reaches for a database scope. Claiming takes one as its
    /// very first act, so a test can wait for the worker's attempt to reach the queue instead of
    /// guessing how long to give it.
    /// </summary>
    public Task ClaimAttempted => _scopes.FirstScope;

    /// <summary>
    /// How many scopes were taken while <see cref="Gate"/> was still closed. Zero is the guarantee:
    /// nothing may go near the queue until startup reconciliation has finished.
    /// </summary>
    public int ScopesTakenBeforeTheGateOpened => _scopes.CreatedBeforeTheGateOpened;

    /// <summary>A fresh context — the worker writes through its own scopes, so never share one.</summary>
    public HarboraDbContext NewDb() => new(
        new DbContextOptionsBuilder<HarboraDbContext>().UseInMemoryDatabase(_dbName).Options);

    public DatabaseJobQueue Queue() => new(NewDb(), Clock, Cancellations, Signal);

    /// <summary>
    /// A worker over this harness. Pass <paramref name="timeout"/> to give dispatched jobs a
    /// deadline a test can actually reach — the real ones are quarter-hours and upwards — and
    /// <paramref name="maxConcurrency"/> to say how much of the queue it may run at once. The
    /// default is one, which is the worker the platform had before jobs ran in parallel, so every
    /// test written against that behaviour still describes it.
    /// </summary>
    public TestableJobWorker Worker(TimeSpan? timeout = null, int maxConcurrency = 1) =>
        new(Scopes, Cancellations, Signal, Gate, Clock, Handler, timeout, maxConcurrency);

    public JobReconciler Reconciler() => new(Scopes, Clock, NullLogger<JobReconciler>.Instance);

    /// <summary>Reads a job row through a fresh context.</summary>
    public Job? JobFor(Guid targetId)
    {
        using var db = NewDb();
        return db.Jobs.AsNoTracking().FirstOrDefault(j => j.TargetId == targetId);
    }

    /// <summary>By id, because two jobs may legitimately share a target.</summary>
    public Job? JobById(Guid jobId)
    {
        using var db = NewDb();
        return db.Jobs.AsNoTracking().FirstOrDefault(j => j.Id == jobId);
    }

    public IReadOnlyList<Job> AllJobs()
    {
        using var db = NewDb();
        return db.Jobs.AsNoTracking().OrderBy(j => j.CreatedAt).ToList();
    }

    /// <summary>
    /// Waits for a job row to reach a terminal status and returns it.
    ///
    /// Used for outcomes only — never to decide whether two jobs overlapped, which the handler's own
    /// gates and peak counters answer without reference to time. Settling is a write made on another
    /// thread after the work returns and there is no in-process signal for it, so this waits for the
    /// durable fact itself: a worker that never settles a row cannot pass, however long it is given.
    /// </summary>
    public async Task<Job> SettledAsync(Guid jobId, TimeSpan? within = null)
    {
        var deadline = DateTime.UtcNow + (within ?? TimeSpan.FromSeconds(10));
        while (true)
        {
            var job = JobById(jobId);
            if (job is { IsTerminal: true }) return job;
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException(
                    $"Job {jobId} was still {job?.Status.ToString() ?? "missing"} when the wait ran out.");
            await Task.Delay(5);
        }
    }

    public void Dispose() => _provider.Dispose();
}

/// <summary>
/// A <see cref="JobStartupGate"/> that reports the instant a waiter parks on it, so a test can await
/// a positive fact ("the worker is now waiting at the gate") instead of racing a timer against "the
/// worker did not reach the queue yet". <see cref="WaitStarted"/> completes as soon as
/// <see cref="WaitAsync"/> is entered — before it can possibly have returned — so a caller that has
/// observed it knows the worker's claim loop has not run, because in <c>JobWorker.ExecuteAsync</c>
/// the gate wait strictly precedes the loop on the same, single, un-forked call stack.
/// </summary>
public sealed class ObservableJobStartupGate : JobStartupGate
{
    private readonly TaskCompletionSource _waitStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task WaitStarted => _waitStarted.Task;

    public override Task WaitAsync(CancellationToken ct)
    {
        _waitStarted.TrySetResult();
        return base.WaitAsync(ct);
    }
}

/// <summary>
/// A <see cref="JobSignal"/> that reports when the worker is asleep on it, so a test about what
/// shutdown does to a parked loop can put the loop there first instead of hoping it got there.
///
/// <para>
/// <see cref="Parked"/> is the current wait, re-armed each time the last waiter comes back out: read
/// it while a waiter is inside and it is already complete; read it between waits and it completes on
/// the next entry. Once inside, only a <c>Notify</c>, the five-second backstop poll or cancellation
/// can get the loop out again — and a test that has just enqueued nothing causes none of the first
/// two.
/// </para>
/// </summary>
public sealed class ObservableJobSignal : JobSignal
{
    private readonly object _gate = new();
    private TaskCompletionSource _parked = Fresh();
    private int _inside;

    /// <summary>Completes while a waiter is inside <see cref="WaitAsync"/>.</summary>
    public Task Parked { get { lock (_gate) return _parked.Task; } }

    public override async Task WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        TaskCompletionSource parked;
        lock (_gate) { _inside++; parked = _parked; }
        parked.TrySetResult();

        try { await base.WaitAsync(timeout, ct); }
        finally { lock (_gate) if (--_inside == 0) _parked = Fresh(); }
    }

    private static TaskCompletionSource Fresh() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>
/// Watches for scopes being taken; otherwise the real factory, unchanged. Taking a scope is the
/// first observable step of a claim, which makes "the worker went to the queue" — and, crucially,
/// *when* it went — something a test can assert on rather than infer from timing.
/// </summary>
internal sealed class CountingScopeFactory(IServiceScopeFactory inner, Func<bool> gateIsOpen)
    : IServiceScopeFactory
{
    private int _beforeTheGateOpened;
    private readonly TaskCompletionSource _first = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int CreatedBeforeTheGateOpened => Volatile.Read(ref _beforeTheGateOpened);

    public Task FirstScope => _first.Task;

    public IServiceScope CreateScope()
    {
        if (!gateIsOpen()) Interlocked.Increment(ref _beforeTheGateOpened);
        _first.TrySetResult();
        return inner.CreateScope();
    }
}

/// <summary>Stands in for the real engines so the queue can be exercised on its own.</summary>
public sealed class StubJobHandler
{
    private readonly List<(JobKind Kind, Guid TargetId)> _executed = [];
    private readonly object _gate = new();

    // Concurrency bookkeeping, all under _gate. Peaks rather than instantaneous counts, because a
    // test that samples "how many are running now" can only ever assert about the moment it looked;
    // a peak recorded by the handler itself is a fact about the whole run.
    private int _running;
    private int _peak;
    private readonly Dictionary<Guid, int> _runningPerTarget = [];
    private readonly Dictionary<Guid, int> _peakPerTarget = [];

    private readonly Dictionary<Guid, TaskCompletionSource> _startedPerTarget = [];
    private readonly Dictionary<Guid, TaskCompletionSource> _heldPerTarget = [];
    private readonly Dictionary<Guid, Exception> _failurePerTarget = [];

    public IReadOnlyList<(JobKind Kind, Guid TargetId)> Executed { get { lock (_gate) return _executed.ToList(); } }

    /// <summary>Thrown from the handler to simulate a job that fails.</summary>
    public Exception? Failure { get; set; }

    /// <summary>
    /// Makes this target's handler block until <see cref="Release"/> is called for it. This is what
    /// turns "were two jobs running at once?" into something a test can decide rather than time: a
    /// held handler cannot return, so anything else that starts is provably overlapping it.
    /// </summary>
    public void Hold(Guid targetId) { lock (_gate) Gate(_heldPerTarget, targetId); }

    /// <summary>Lets a held target's handler return. Safe to call before it has started.</summary>
    public void Release(Guid targetId)
    {
        TaskCompletionSource hold;
        lock (_gate) hold = Gate(_heldPerTarget, targetId);
        hold.TrySetResult();
    }

    /// <summary>Completes the moment this target's handler is entered.</summary>
    public Task StartedFor(Guid targetId) { lock (_gate) return Gate(_startedPerTarget, targetId).Task; }

    /// <summary>Fails only this target, so one job's failure can be watched next to another's success.</summary>
    public void FailWith(Guid targetId, Exception failure) { lock (_gate) _failurePerTarget[targetId] = failure; }

    /// <summary>The most handlers that were ever inside their bodies at the same time.</summary>
    public int MaxConcurrent { get { lock (_gate) return _peak; } }

    /// <summary>The same, for one target. Anything above one is two jobs on one target overlapping.</summary>
    public int MaxConcurrentFor(Guid targetId) { lock (_gate) return _peakPerTarget.GetValueOrDefault(targetId); }

    /// <summary>How many handlers are inside their bodies right now.</summary>
    public int Running { get { lock (_gate) return _running; } }

    /// <summary>Call under <see cref="_gate"/>.</summary>
    private static TaskCompletionSource Gate(Dictionary<Guid, TaskCompletionSource> gates, Guid targetId)
    {
        if (!gates.TryGetValue(targetId, out var gate))
            gates[targetId] = gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return gate;
    }

    /// <summary>When set, the handler blocks until its token is cancelled — simulates long work.</summary>
    public bool BlockUntilCancelled { get; set; }

    /// <summary>
    /// What the handler does when that block is cancelled. Defaults to rethrowing, which is what a
    /// bare <c>await Task.Delay(ct)</c> does and what no real dispatch target does.
    /// </summary>
    public StubCancellation OnCancellation { get; set; } = StubCancellation.Rethrow;

    /// <summary>
    /// Upper bound on that block. Deliberately finite: if cancellation never arrives the test should
    /// fail on its assertion within seconds, not hang the suite forever.
    /// </summary>
    public static readonly TimeSpan MaxBlock = TimeSpan.FromSeconds(10);

    /// <summary>Completes once the handler has started, so a test can cancel mid-flight.</summary>
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task ExecuteAsync(Job job, CancellationToken ct)
    {
        var target = job.TargetId;
        TaskCompletionSource started;
        TaskCompletionSource? hold;
        Exception? failure;
        lock (_gate)
        {
            _executed.Add((job.Kind, target));

            _peak = Math.Max(_peak, ++_running);
            var here = _runningPerTarget.GetValueOrDefault(target) + 1;
            _runningPerTarget[target] = here;
            _peakPerTarget[target] = Math.Max(_peakPerTarget.GetValueOrDefault(target), here);

            started = Gate(_startedPerTarget, target);
            // Looked up, never created: a target nobody held must not gain a gate that nobody will
            // ever open.
            _heldPerTarget.TryGetValue(target, out hold);
            failure = _failurePerTarget.GetValueOrDefault(target) ?? Failure;
        }

        Started.TrySetResult();
        started.TrySetResult();

        try
        {
            if (failure is not null) throw failure;

            // A target nobody held has no gate to wait on: the handler returns immediately, exactly
            // as it did before any of this existed.
            if (hold is not null) await hold.Task.WaitAsync(MaxBlock, ct);
            if (!BlockUntilCancelled) return;

            try
            {
                await Task.Delay(MaxBlock, ct);
            }
            catch (OperationCanceledException) when (OnCancellation != StubCancellation.Rethrow)
            {
                if (OnCancellation == StubCancellation.SurfaceAsBrokenConnection)
                    throw new IOException("the connection was reset while the stream was being read");

                // Swallowed: the handler returns as if the work had finished.
            }
        }
        finally
        {
            lock (_gate)
            {
                _running--;
                _runningPerTarget[target] = _runningPerTarget.GetValueOrDefault(target) - 1;
            }
        }
    }
}

/// <summary>
/// How a dispatch target reports a cancellation the worker caused. Only the first of these is a
/// clean rethrow, and it is the one no production target does: <c>DeploymentPipeline</c>,
/// <c>CronJobRunner</c> and <c>ManagedServiceEngine</c> all catch <c>Exception</c> at the top level
/// and write the failure into their own domain row, and a killed transfer usually reports itself as
/// a torn-down socket rather than as a cancellation at all.
/// </summary>
public enum StubCancellation
{
    /// <summary>A bare <c>await Task.Delay(ct)</c>: the OperationCanceledException propagates.</summary>
    Rethrow,

    /// <summary>Caught, recorded elsewhere, and the handler returns normally.</summary>
    Swallow,

    /// <summary>Caught and re-reported as an IOException, which the policy calls retryable.</summary>
    SurfaceAsBrokenConnection
}

/// <summary><see cref="JobWorker"/> with only the dispatch target swapped for the stub.</summary>
public sealed class TestableJobWorker(
    IServiceScopeFactory scopes,
    IJobCancellationRegistry cancellations,
    JobSignal signal,
    JobStartupGate startupGate,
    ISystemClock clock,
    StubJobHandler handler,
    TimeSpan? timeout = null,
    int maxConcurrency = 1)
    : JobWorker(scopes, cancellations, signal, startupGate, clock,
        Options.Create(new JobQueueOptions { MaxConcurrency = maxConcurrency }),
        NullLogger<JobWorker>.Instance)
{
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Completes once the loop has actually begun. The host starts a <c>BackgroundService</c>'s
    /// <c>ExecuteAsync</c> on the thread pool under the stopping token, so a worker stopped straight
    /// after <c>StartAsync</c> frequently never enters it at all — and a test about what the loop
    /// does when it is stopped has to know that it is running first.
    /// </summary>
    public Task LoopEntered => _entered.Task;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _entered.TrySetResult();
        return base.ExecuteAsync(stoppingToken);
    }

    protected override Task DispatchAsync(Job job, IServiceProvider scope, CancellationToken ct)
        => handler.ExecuteAsync(job, ct);

    /// <summary>
    /// The real deadlines run from five minutes to seven hours, which no test can wait out. Only the
    /// length is replaced — the worker's own enforcement of it is the thing under test.
    /// </summary>
    protected override TimeSpan TimeoutFor(Job job) => timeout ?? base.TimeoutFor(job);
}
