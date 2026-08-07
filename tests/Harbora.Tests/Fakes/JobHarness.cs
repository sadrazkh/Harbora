using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Jobs;
using Harbora.Infrastructure.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

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
    public JobSignal Signal { get; } = new();
    public JobCancellationRegistry Cancellations { get; } = new();
    public StubJobHandler Handler { get; } = new();

    /// <summary>
    /// Closed, exactly as it is when the host starts. A test that drives the worker's loop has to
    /// open it; one that drives <c>RunNextAsync</c> directly is past the gate already.
    /// </summary>
    public JobStartupGate Gate { get; } = new();

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
    /// deadline a test can actually reach — the real ones are quarter-hours and upwards.
    /// </summary>
    public TestableJobWorker Worker(TimeSpan? timeout = null) =>
        new(Scopes, Cancellations, Signal, Gate, Clock, Handler, timeout);

    public JobReconciler Reconciler() => new(Scopes, Clock, NullLogger<JobReconciler>.Instance);

    /// <summary>Reads a job row through a fresh context.</summary>
    public Job? JobFor(Guid targetId)
    {
        using var db = NewDb();
        return db.Jobs.AsNoTracking().FirstOrDefault(j => j.TargetId == targetId);
    }

    public IReadOnlyList<Job> AllJobs()
    {
        using var db = NewDb();
        return db.Jobs.AsNoTracking().OrderBy(j => j.CreatedAt).ToList();
    }

    public void Dispose() => _provider.Dispose();
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

    public IReadOnlyList<(JobKind Kind, Guid TargetId)> Executed { get { lock (_gate) return _executed.ToList(); } }

    /// <summary>Thrown from the handler to simulate a job that fails.</summary>
    public Exception? Failure { get; set; }

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
        lock (_gate) _executed.Add((job.Kind, job.TargetId));
        Started.TrySetResult();

        if (Failure is not null) throw Failure;
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
    TimeSpan? timeout = null)
    : JobWorker(scopes, cancellations, signal, startupGate, clock, NullLogger<JobWorker>.Instance)
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
