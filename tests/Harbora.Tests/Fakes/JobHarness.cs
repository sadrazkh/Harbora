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

    public TestableJobWorker Worker() => new(Scopes, Cancellations, Signal, Clock, Handler);

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
        if (BlockUntilCancelled) await Task.Delay(MaxBlock, ct);
    }
}

/// <summary><see cref="JobWorker"/> with only the dispatch target swapped for the stub.</summary>
public sealed class TestableJobWorker(
    IServiceScopeFactory scopes,
    IJobCancellationRegistry cancellations,
    JobSignal signal,
    ISystemClock clock,
    StubJobHandler handler)
    : JobWorker(scopes, cancellations, signal, clock, NullLogger<JobWorker>.Instance)
{
    protected override Task DispatchAsync(Job job, IServiceProvider scope, CancellationToken ct)
        => handler.ExecuteAsync(job, ct);
}
