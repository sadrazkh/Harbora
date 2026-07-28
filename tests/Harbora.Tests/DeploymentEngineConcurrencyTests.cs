using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Deployments;
using Harbora.Infrastructure.Deployments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// At-most-one-active-deployment-per-app (H3): concurrent triggers must coalesce onto the existing
/// in-flight deployment instead of racing a second build.
/// </summary>
public class DeploymentEngineConcurrencyTests
{
    private sealed class NoopQueue : IBackgroundJobQueue
    {
        public int Enqueued;
        public ValueTask EnqueueAsync(Func<IServiceProvider, CancellationToken, Task> job, CancellationToken ct = default)
        { Enqueued++; return ValueTask.CompletedTask; }
        public ValueTask<Func<IServiceProvider, CancellationToken, Task>> DequeueAsync(CancellationToken ct)
            => throw new NotSupportedException();
    }

    private sealed class Clock : ISystemClock { public DateTimeOffset UtcNow => DateTimeOffset.UtcNow; }

    private static HarboraDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<HarboraDbContext>()
            .UseInMemoryDatabase("engine-" + Guid.NewGuid()).Options;
        return new HarboraDbContext(options);
    }

    [Fact]
    public async Task Second_trigger_returns_the_existing_in_flight_deployment()
    {
        using var db = NewDb();
        var appId = Guid.NewGuid();
        db.Apps.Add(new App { Id = appId, Name = "a", Slug = "a" });
        await db.SaveChangesAsync();

        var engine = new DeploymentEngine(db, new NoopQueue(), new Clock());

        var first = await engine.QueueDeploymentAsync(
            new DeploymentRequest(appId, DeploymentTrigger.Manual, Guid.NewGuid()), default);
        var second = await engine.QueueDeploymentAsync(
            new DeploymentRequest(appId, DeploymentTrigger.GitPush, Guid.NewGuid()), default);

        second.Should().Be(first, "a second trigger while one is in flight must coalesce");
        db.Deployments.Count(d => d.AppId == appId).Should().Be(1);
    }

    [Fact]
    public async Task A_new_deployment_is_created_once_the_previous_is_terminal()
    {
        using var db = NewDb();
        var appId = Guid.NewGuid();
        db.Apps.Add(new App { Id = appId, Name = "a", Slug = "a" });
        await db.SaveChangesAsync();
        var engine = new DeploymentEngine(db, new NoopQueue(), new Clock());

        var first = await engine.QueueDeploymentAsync(
            new DeploymentRequest(appId, DeploymentTrigger.Manual, Guid.NewGuid()), default);

        // Mark the first terminal, then a new trigger should create a distinct deployment.
        var d1 = db.Deployments.Single(d => d.Id == first);
        d1.Status = DeploymentStatus.Succeeded;
        await db.SaveChangesAsync();

        var second = await engine.QueueDeploymentAsync(
            new DeploymentRequest(appId, DeploymentTrigger.Manual, Guid.NewGuid()), default);

        second.Should().NotBe(first);
        db.Deployments.Count(d => d.AppId == appId).Should().Be(2);
    }
}
