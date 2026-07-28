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

    [Fact]
    public async Task A_rollback_is_never_coalesced_onto_an_in_flight_deploy()
    {
        using var db = NewDb();
        var appId = Guid.NewGuid();
        db.Apps.Add(new App { Id = appId, Name = "a", Slug = "a" });
        await db.SaveChangesAsync();
        var engine = new DeploymentEngine(db, new NoopQueue(), new Clock());

        var running = await engine.QueueDeploymentAsync(
            new DeploymentRequest(appId, DeploymentTrigger.Manual, Guid.NewGuid()), default);

        // Handing back `running` here would look like the rollback succeeded while it was never
        // queued — precisely when the user needs it (a bad deploy is live).
        var rollback = async () => await engine.QueueDeploymentAsync(
            new DeploymentRequest(appId, DeploymentTrigger.Rollback, Guid.NewGuid(),
                RollbackToDeploymentId: running), default);

        await rollback.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*still running*");
        db.Deployments.Count(d => d.AppId == appId).Should().Be(1);
    }

    [Fact]
    public async Task A_deploy_is_never_coalesced_onto_an_in_flight_rollback()
    {
        using var db = NewDb();
        var appId = Guid.NewGuid();
        db.Apps.Add(new App { Id = appId, Name = "a", Slug = "a" });
        db.Deployments.Add(new Deployment
        {
            Id = Guid.NewGuid(), AppId = appId, Number = 1, Status = DeploymentStatus.Succeeded
        });
        await db.SaveChangesAsync();
        var engine = new DeploymentEngine(db, new NoopQueue(), new Clock());

        var target = db.Deployments.Single(d => d.AppId == appId).Id;
        await engine.QueueDeploymentAsync(
            new DeploymentRequest(appId, DeploymentTrigger.Rollback, Guid.NewGuid(),
                RollbackToDeploymentId: target), default);

        var deploy = async () => await engine.QueueDeploymentAsync(
            new DeploymentRequest(appId, DeploymentTrigger.Manual, Guid.NewGuid()), default);

        await deploy.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*rollback*");
    }

    [Fact]
    public async Task Two_rollbacks_still_coalesce()
    {
        using var db = NewDb();
        var appId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        db.Apps.Add(new App { Id = appId, Name = "a", Slug = "a" });
        db.Deployments.Add(new Deployment
        {
            Id = targetId, AppId = appId, Number = 1, Status = DeploymentStatus.Succeeded
        });
        await db.SaveChangesAsync();
        var engine = new DeploymentEngine(db, new NoopQueue(), new Clock());

        var first = await engine.QueueDeploymentAsync(
            new DeploymentRequest(appId, DeploymentTrigger.Rollback, Guid.NewGuid(),
                RollbackToDeploymentId: targetId), default);
        var second = await engine.QueueDeploymentAsync(
            new DeploymentRequest(appId, DeploymentTrigger.Rollback, Guid.NewGuid(),
                RollbackToDeploymentId: targetId), default);

        second.Should().Be(first, "a double-clicked rollback is still the same intent");
    }

    [Theory]
    [InlineData(DeploymentStatus.Queued)]
    [InlineData(DeploymentStatus.Building)]
    [InlineData(DeploymentStatus.Deploying)]
    [InlineData(DeploymentStatus.HealthChecking)]
    public async Task Cancel_moves_an_in_flight_deployment_to_cancelled(DeploymentStatus from)
    {
        using var db = NewDb();
        var id = Guid.NewGuid();
        db.Deployments.Add(new Deployment { Id = id, AppId = Guid.NewGuid(), Number = 1, Status = from });
        await db.SaveChangesAsync();
        var engine = new DeploymentEngine(db, new NoopQueue(), new Clock());

        await engine.CancelAsync(id, default);

        var d = db.Deployments.Single(x => x.Id == id);
        d.Status.Should().Be(DeploymentStatus.Cancelled);
        d.FinishedAt.Should().NotBeNull("cancelling is terminal and must stamp a finish time");
    }

    [Theory]
    [InlineData(DeploymentStatus.Succeeded)]
    [InlineData(DeploymentStatus.Failed)]
    [InlineData(DeploymentStatus.RolledBack)]
    public async Task Cancel_is_a_no_op_for_a_terminal_deployment(DeploymentStatus terminal)
    {
        using var db = NewDb();
        var id = Guid.NewGuid();
        db.Deployments.Add(new Deployment { Id = id, AppId = Guid.NewGuid(), Number = 1, Status = terminal });
        await db.SaveChangesAsync();
        var engine = new DeploymentEngine(db, new NoopQueue(), new Clock());

        // Must not throw an illegal-transition exception, and must not rewrite history.
        await engine.CancelAsync(id, default);

        db.Deployments.Single(x => x.Id == id).Status.Should().Be(terminal);
    }

    [Fact]
    public async Task Cancel_ignores_an_unknown_deployment()
    {
        using var db = NewDb();
        var engine = new DeploymentEngine(db, new NoopQueue(), new Clock());

        var act = async () => await engine.CancelAsync(Guid.NewGuid(), default);

        await act.Should().NotThrowAsync();
    }
}
