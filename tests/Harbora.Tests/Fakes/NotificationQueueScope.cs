using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Infrastructure.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Harbora.Tests.Fakes;

/// <summary>
/// The <see cref="IServiceScopeFactory"/> a hand-built <c>NotificationService</c> needs to actually
/// enqueue a delivery (N1) — every scope opens a fresh <see cref="HarboraDbContext"/> over the same
/// in-memory store plus a real <see cref="DatabaseJobQueue"/>, so "was a Job row written" exercises
/// the production enqueue path rather than a stub that only records a call.
/// </summary>
public sealed class NotificationQueueScope(string store, ISystemClock? clock = null) : IDisposable
{
    private readonly ServiceProvider _provider = new ServiceCollection()
        .AddDbContext<HarboraDbContext>(o => o.UseInMemoryDatabase(store))
        .AddSingleton<IWorkspaceScope>(SystemWorkspaceScope.Instance)
        .AddSingleton<ISystemClock>(clock ?? new FixedClock())
        .AddSingleton<JobSignal>()
        .AddSingleton<IJobCancellationRegistry, JobCancellationRegistry>()
        .AddScoped<IJobQueue, DatabaseJobQueue>()
        .BuildServiceProvider();

    public IServiceScopeFactory Factory => _provider.GetRequiredService<IServiceScopeFactory>();

    /// <summary>A fresh context over the same store — for reading back what a scope wrote.</summary>
    public HarboraDbContext NewDb() => new(
        new DbContextOptionsBuilder<HarboraDbContext>().UseInMemoryDatabase(store).Options);

    public void Dispose() => _provider.Dispose();
}
