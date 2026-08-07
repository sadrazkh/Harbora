using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Deployments;
using Harbora.Domain.Jobs;
using Harbora.Infrastructure.Deployments;
using Harbora.Tests.Fakes;
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
    /// <summary>Ceiling on waiting for something that must happen, never a way of asserting it did not.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

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

        var reconciler = new DeploymentReconciler(
            sp.GetRequiredService<IServiceScopeFactory>(), new FixedClock(),
            NullLogger<DeploymentReconciler>.Instance);

        await reconciler.ReconcileAsync(default);

        using (var scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();

            // The one Queued deployment got exactly one durable job (it had none).
            db.Jobs.Count(j => j.Kind == JobKind.Deployment && j.Status == JobStatus.Pending).Should().Be(1);
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
        var reconciler = new DeploymentReconciler(
            sp.GetRequiredService<IServiceScopeFactory>(), new FixedClock(),
            NullLogger<DeploymentReconciler>.Instance);

        await reconciler.ReconcileAsync(default);

        using var verify = sp.CreateScope();
        verify.ServiceProvider.GetRequiredService<HarboraDbContext>().Jobs.Should().BeEmpty();
    }

    [Fact]
    public async Task A_queued_deployment_that_still_has_its_job_is_not_queued_twice()
    {
        // Since the job table is durable, the job normally survives the restart alongside the
        // deployment row. Re-queueing unconditionally here would deploy the same thing twice.
        var sp = BuildProvider("recon-dup-" + Guid.NewGuid());
        var appId = Guid.NewGuid();
        var deploymentId = Guid.NewGuid();
        using (var scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();
            db.Apps.Add(new App { Id = appId, Name = "x", Slug = "x", Status = AppStatus.Created });
            db.Deployments.Add(new Deployment { Id = deploymentId, AppId = appId, Number = 1, Status = DeploymentStatus.Queued });
            db.Jobs.Add(new Job { Kind = JobKind.Deployment, TargetId = deploymentId, Status = JobStatus.Pending });
            await db.SaveChangesAsync();
        }

        var reconciler = new DeploymentReconciler(
            sp.GetRequiredService<IServiceScopeFactory>(), new FixedClock(),
            NullLogger<DeploymentReconciler>.Instance);
        await reconciler.ReconcileAsync(default);

        using var verify = sp.CreateScope();
        verify.ServiceProvider.GetRequiredService<HarboraDbContext>()
            .Jobs.Count(j => j.TargetId == deploymentId).Should().Be(1);
    }

    [Fact]
    public async Task The_queued_job_of_a_deployment_the_restart_failed_is_settled_too()
    {
        // A graceful shutdown returns a running job to Pending so the next start resumes it. When
        // the deployment it points at has meanwhile been failed by this reconciler, resuming it
        // means dispatching work whose target is already over — so the job has to be settled here,
        // where the decision to end the deployment is made.
        var sp = BuildProvider("recon-jobs-" + Guid.NewGuid());
        var appId = Guid.NewGuid();
        var failedId = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        using (var scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();
            db.Apps.Add(new App { Id = appId, Name = "x", Slug = "x", Status = AppStatus.Deploying });
            db.Deployments.Add(new Deployment { Id = failedId, AppId = appId, Number = 1, Status = DeploymentStatus.Building });
            db.Jobs.AddRange(
                new Job { Kind = JobKind.Deployment, TargetId = failedId, Status = JobStatus.Pending, Attempts = 1 },
                // Someone else's work, and a finished attempt at this one: neither may be rewritten.
                new Job { Kind = JobKind.Deployment, TargetId = otherId, Status = JobStatus.Pending },
                new Job { Kind = JobKind.Backup, TargetId = failedId, Status = JobStatus.Pending });
            await db.SaveChangesAsync();
        }

        var reconciler = new DeploymentReconciler(
            sp.GetRequiredService<IServiceScopeFactory>(), new FixedClock(),
            NullLogger<DeploymentReconciler>.Instance);
        await reconciler.ReconcileAsync(default);

        using var verify = sp.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<HarboraDbContext>();

        var settled = verifyDb.Jobs.Single(j => j.Kind == JobKind.Deployment && j.TargetId == failedId);
        settled.Status.Should().Be(JobStatus.Cancelled, "there is nothing left for it to deploy");
        settled.Error.Should().Contain("restart");
        settled.FinishedAt.Should().NotBeNull();

        verifyDb.Jobs.Single(j => j.TargetId == otherId).Status.Should().Be(JobStatus.Pending,
            "another deployment's job is none of this deployment's business");
        verifyDb.Jobs.Single(j => j.Kind == JobKind.Backup).Status.Should().Be(JobStatus.Pending,
            "a backup of the same id is a different kind of work entirely");
    }

    [Fact]
    public async Task A_deployment_the_restart_failed_is_never_dispatched_again()
    {
        // The whole incident in one test: the panel is restarted mid-deployment, the reconcilers
        // run, and the worker then finds nothing left to claim — so the deployment is failed once,
        // by the restart, with the message the restart wrote.
        using var h = new JobHarness();
        var appId = Guid.NewGuid();
        var deploymentId = Guid.NewGuid();
        using (var db = h.NewDb())
        {
            db.Apps.Add(new App { Id = appId, Name = "x", Slug = "x", Status = AppStatus.Deploying });
            db.Deployments.Add(new Deployment { Id = deploymentId, AppId = appId, Number = 3, Status = DeploymentStatus.Building });
            db.Jobs.Add(new Job { Kind = JobKind.Deployment, TargetId = deploymentId, Status = JobStatus.Pending, Attempts = 1 });
            await db.SaveChangesAsync();
        }

        await new DeploymentReconciler(h.Scopes, h.Clock, NullLogger<DeploymentReconciler>.Instance)
            .ReconcileAsync(default);

        (await h.Worker().RunNextAsync(default)).Should().BeFalse("the queue has nothing claimable left");
        h.Handler.Executed.Should().BeEmpty();

        using var verify = h.NewDb();
        var deployment = verify.Deployments.Single(d => d.Id == deploymentId);
        deployment.Status.Should().Be(DeploymentStatus.Failed);
        deployment.ErrorMessage.Should().Contain("restart",
            "one terminal transition, with the reason the restart gave — not a second, confusing one");
    }

    [Fact]
    public async Task A_deployment_job_the_previous_build_queued_is_stamped_with_its_app()
    {
        // The upgrade that ships per-app exclusion arrives at a queue the OLD build filled, and those
        // rows carry no ExclusiveWith at all — so each one excludes on its own deployment id, which
        // is a fresh guid per redeploy. Two of them for one app therefore exclude on nothing in
        // common, and the parallel worker this phase ships is free to run both: two docker builds,
        // two containers under one name, two host-port reservations, two proxy applies. Under the
        // serial worker they merely queued behind each other, which is why the column alone was not
        // enough. Two rows for one app is the read-then-insert race in DeploymentEngine, which is
        // still open by design — and every row it left behind before the upgrade is one of these.
        using var h = new JobHarness();
        var app = Guid.NewGuid();
        var legacy = Guid.NewGuid();  // queued by the old build: no key on its job
        var fresh = Guid.NewGuid();   // queued by this one: the app is on the row

        using (var db = h.NewDb())
        {
            db.Apps.Add(new App { Id = app, Name = "x", Slug = "x", Status = AppStatus.Created });
            db.Deployments.AddRange(
                new Deployment { Id = legacy, AppId = app, Number = 1, Status = DeploymentStatus.Queued },
                new Deployment { Id = fresh, AppId = app, Number = 2, Status = DeploymentStatus.Queued });
            db.Jobs.AddRange(
                new Job
                {
                    Kind = JobKind.Deployment, TargetId = legacy, Status = JobStatus.Pending,
                    ExclusiveWith = null, CreatedAt = h.Clock.UtcNow
                },
                new Job
                {
                    Kind = JobKind.Deployment, TargetId = fresh, Status = JobStatus.Pending,
                    ExclusiveWith = app, CreatedAt = h.Clock.UtcNow.AddSeconds(1)
                });
            await db.SaveChangesAsync();
        }

        await new DeploymentReconciler(h.Scopes, h.Clock, NullLogger<DeploymentReconciler>.Instance)
            .ReconcileAsync(default);

        using var verify = h.NewDb();
        verify.Jobs.Single(j => j.TargetId == legacy).ExclusiveWith.Should().Be(app,
            "the reconciler knows the app of every deployment it is looking at");

        // And what that is for: the two must not run together. Driven by hand so the refusal is a
        // returned value observed while the first job is provably inside its handler.
        h.Handler.Hold(legacy);
        using var worker = h.Worker();
        var inFlight = worker.RunNextAsync(default);
        await h.Handler.StartedFor(legacy).WaitAsync(Patience);

        (await worker.RunNextAsync(default)).Should().BeFalse(
            "the row the old build left behind names the same app as the one queued since");
        h.Handler.Executed.Should().HaveCount(1);

        // Held back, not dropped: it runs the moment the one before it is done.
        h.Handler.Release(legacy);
        (await inFlight.WaitAsync(Patience)).Should().BeTrue();
        (await worker.RunNextAsync(default)).Should().BeTrue();
        h.Handler.MaxConcurrent.Should().Be(1, "one app, one deployment at a time, over the whole run");
    }

    [Fact]
    public async Task Reconciliation_that_fails_still_lets_startup_carry_on()
    {
        // JobStartupGate is opened by a hosted service registered after this one, so the worker only
        // ever starts if StartAsync returns. A reconciler that let an exception escape would leave
        // the platform running and deploying nothing at all — worse than the race the gate closes.
        var sp = BuildProvider("recon-throws-" + Guid.NewGuid());
        var reconciler = new DeploymentReconciler(
            sp.GetRequiredService<IServiceScopeFactory>(), new FixedClock(),
            NullLogger<DeploymentReconciler>.Instance);
        await sp.DisposeAsync();

        var start = () => reconciler.StartAsync(default);

        await start.Should().NotThrowAsync("startup must survive a reconciler that cannot read the database");
    }
}
