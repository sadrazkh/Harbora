using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Deployments;
using Harbora.Infrastructure.Deployments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Crash-recovery tests (ADR-005 / C2): after a restart, no deployment may remain stuck in a
/// non-terminal state. Queued work is re-queued; interrupted in-progress work is failed cleanly.
/// </summary>
public class DeploymentReconcilerTests
{
    private sealed class CapturingQueue : IBackgroundJobQueue
    {
        public int Enqueued;
        public ValueTask EnqueueAsync(Func<IServiceProvider, CancellationToken, Task> job, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Enqueued);
            return ValueTask.CompletedTask;
        }
        public ValueTask<Func<IServiceProvider, CancellationToken, Task>> DequeueAsync(CancellationToken ct)
            => throw new NotSupportedException();
    }

    private sealed class FixedClock : ISystemClock
    {
        public DateTimeOffset UtcNow { get; } = DateTimeOffset.UtcNow;
    }

    private static ServiceProvider BuildProvider(string dbName)
    {
        var services = new ServiceCollection();
        services.AddDbContext<HarboraDbContext>(o => o.UseInMemoryDatabase(dbName));
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Reconciles_stranded_deployments_after_restart()
    {
        var sp = BuildProvider("recon-" + Guid.NewGuid());
        var apps = new
        {
            NoActive = Guid.NewGuid(),   // building, never had a live version
            HasActive = Guid.NewGuid(),  // deploying, already had a live version
            Queued = Guid.NewGuid(),
        };
        var priorLive = Guid.NewGuid();

        using (var scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();
            db.Apps.AddRange(
                new App { Id = apps.NoActive, Name = "a1", Slug = "a1", Status = AppStatus.Deploying, ActiveDeploymentId = null },
                new App { Id = apps.HasActive, Name = "a2", Slug = "a2", Status = AppStatus.Deploying, ActiveDeploymentId = priorLive },
                new App { Id = apps.Queued, Name = "a3", Slug = "a3", Status = AppStatus.Created });
            db.Deployments.AddRange(
                new Deployment { AppId = apps.NoActive, Number = 1, Status = DeploymentStatus.Building },
                new Deployment { AppId = apps.HasActive, Number = 2, Status = DeploymentStatus.Deploying },
                new Deployment { AppId = apps.Queued, Number = 1, Status = DeploymentStatus.Queued },
                new Deployment { AppId = apps.NoActive, Number = 0, Status = DeploymentStatus.Succeeded }); // terminal, untouched
            await db.SaveChangesAsync();
        }

        var queue = new CapturingQueue();
        var reconciler = new DeploymentReconciler(
            sp.GetRequiredService<IServiceScopeFactory>(), queue, new FixedClock(),
            NullLogger<DeploymentReconciler>.Instance);

        await reconciler.ReconcileAsync(default);

        using (var scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();

            // The one Queued deployment was re-queued exactly once.
            queue.Enqueued.Should().Be(1);
            var queued = db.Deployments.Single(d => d.AppId == apps.Queued);
            queued.Status.Should().Be(DeploymentStatus.Queued, "re-queued rows keep their status until picked up");

            // In-progress deployments were failed with a reason.
            var building = db.Deployments.Single(d => d.AppId == apps.NoActive && d.Number == 1);
            building.Status.Should().Be(DeploymentStatus.Failed);
            building.ErrorMessage.Should().Contain("restart");
            building.FinishedAt.Should().NotBeNull();

            var deploying = db.Deployments.Single(d => d.AppId == apps.HasActive);
            deploying.Status.Should().Be(DeploymentStatus.Failed);

            // App with no prior live version → Failed; app that had one → stays Running.
            db.Apps.Single(a => a.Id == apps.NoActive).Status.Should().Be(AppStatus.Failed);
            db.Apps.Single(a => a.Id == apps.HasActive).Status.Should().Be(AppStatus.Running);

            // Terminal deployment untouched.
            db.Deployments.Single(d => d.AppId == apps.NoActive && d.Number == 0)
                .Status.Should().Be(DeploymentStatus.Succeeded);
        }
    }

    [Fact]
    public async Task Is_noop_when_nothing_in_flight()
    {
        var sp = BuildProvider("recon-empty-" + Guid.NewGuid());
        using (var scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();
            db.Apps.Add(new App { Id = Guid.NewGuid(), Name = "x", Slug = "x", Status = AppStatus.Running });
            db.Deployments.Add(new Deployment { AppId = Guid.NewGuid(), Number = 1, Status = DeploymentStatus.Succeeded });
            await db.SaveChangesAsync();
        }
        var queue = new CapturingQueue();
        var reconciler = new DeploymentReconciler(
            sp.GetRequiredService<IServiceScopeFactory>(), queue, new FixedClock(),
            NullLogger<DeploymentReconciler>.Instance);

        await reconciler.ReconcileAsync(default);

        queue.Enqueued.Should().Be(0);
    }
}
