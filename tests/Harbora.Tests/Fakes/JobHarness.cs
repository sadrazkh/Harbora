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

    public JobHarness()
    {
        var services = new ServiceCollection();
        services.AddDbContext<HarboraDbContext>(o => o.UseInMemoryDatabase(_dbName));
        _provider = services.BuildServiceProvider();
    }

    public IServiceScopeFactory Scopes => _provider.GetRequiredService<IServiceScopeFactory>();

    /// <summary>A fresh context — the worker writes through its own scopes, so never share one.</summary>
    public HarboraDbContext NewDb() => new(
        new DbContextOptionsBuilder<HarboraDbContext>().UseInMemoryDatabase(_dbName).Options);

    public DatabaseJobQueue Queue() => new(NewDb(), Clock, Cancellations, Signal);

    /// <summary>
    /// A worker over this harness. Pass <paramref name="timeout"/> to give dispatched jobs a
    /// deadline a test can actually reach — the real ones are quarter-hours and upwards.
    /// </summary>
    public TestableJobWorker Worker(TimeSpan? timeout = null) =>
        new(Scopes, Cancellations, Signal, Clock, Handler, timeout);

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
    ISystemClock clock,
    StubJobHandler handler,
    TimeSpan? timeout = null)
    : JobWorker(scopes, cancellations, signal, clock, NullLogger<JobWorker>.Instance)
{
    protected override Task DispatchAsync(Job job, IServiceProvider scope, CancellationToken ct)
        => handler.ExecuteAsync(job, ct);

    /// <summary>
    /// The real deadlines run from five minutes to seven hours, which no test can wait out. Only the
    /// length is replaced — the worker's own enforcement of it is the thing under test.
    /// </summary>
    protected override TimeSpan TimeoutFor(Job job) => timeout ?? base.TimeoutFor(job);
}
