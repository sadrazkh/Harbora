using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Functions;
using Harbora.Domain.Networking;
using Harbora.Domain.Services;
using Harbora.Infrastructure.Projects;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Project = Harbora.Domain.Projects.Project;
using ProjectEnvironment = Harbora.Domain.Projects.Environment;

namespace Harbora.Tests;

/// <summary>
/// Deleting a project and everything inside it, against a real database.
///
/// The seam this whole feature turns on: <see cref="ProjectDeletionService.PlanAsync"/> (what the
/// confirm screen shows) and <see cref="ProjectDeletionService.DeleteAsync"/> (what actually gets
/// destroyed) have to name the exact same apps and databases, or the confirm screen is a lie. Both
/// read through the same private <c>BuildPlanAsync</c>, and
/// <see cref="Plan_and_delete_agree_on_the_same_apps_and_databases"/> is what pins that rather than
/// trusting the two call sites never drift apart.
///
/// Every app/database delete here goes through a hand-written fake of
/// <see cref="IAppOperationsService"/>/<see cref="IManagedServiceEngine"/> rather than the real Docker
/// path — the real paths already have their own tests (AppOperationsService, ManagedServiceEngine).
/// What is under test in this file is what <c>ProjectDeletionService</c> does with what those two
/// report back: whether it reads the database afterwards rather than trusting a call that returned
/// without throwing, and whether a caught failure is named rather than swallowed into "Deleted".
/// </summary>
public class ProjectDeletionServiceTests
{
    private static HarboraDbContext NewDb() => new(
        new DbContextOptionsBuilder<HarboraDbContext>()
            .UseInMemoryDatabase("projdel-" + Guid.NewGuid()).Options);

    /// <summary>
    /// Deletes the row for real, the way the production engines eventually do — unless the id is in
    /// <paramref name="refuses"/>, in which case it throws and leaves the row exactly where it was,
    /// standing in for a container that will not stop.
    /// </summary>
    private sealed class FakeAppOps(HarboraDbContext db, HashSet<Guid> refuses) : IAppOperationsService
    {
        public List<Guid> Deleted { get; } = [];

        public async Task DeleteAsync(Guid appId, bool removeVolumes, CancellationToken ct)
        {
            if (refuses.Contains(appId))
                throw new InvalidOperationException($"container for app {appId} would not stop");

            var app = await db.Apps.IgnoreQueryFilters().FirstAsync(a => a.Id == appId, ct);
            db.Apps.Remove(app);
            await db.SaveChangesAsync(ct);
            Deleted.Add(appId);
        }

        public Task RestartAsync(Guid appId, CancellationToken ct) => throw new NotSupportedException();
        public Task StopAsync(Guid appId, CancellationToken ct) => throw new NotSupportedException();
        public Task StartAsync(Guid appId, CancellationToken ct) => throw new NotSupportedException();
        public Task<string> GetLogsAsync(Guid appId, int tail, CancellationToken ct) => throw new NotSupportedException();
        public Task<LogSearchResult> SearchLogsAsync(
            IReadOnlyList<Guid> appIds, string? text, bool problemsOnly, TimeSpan? window, int maxLinesPerApp,
            CancellationToken ct) => throw new NotSupportedException();
        public Task<MaintenanceToggleResult> SetMaintenanceModeAsync(
            Guid appId, bool enabled, string? messageEn, string? messageFa, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<RateLimitToggleResult> SetRateLimitAsync(
            Guid appId, bool enabled, int average, int burst, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class FakeServiceEngine(HarboraDbContext db, HashSet<Guid> refuses) : IManagedServiceEngine
    {
        public List<Guid> Deleted { get; } = [];

        public IReadOnlyList<ServiceCatalogEntry> Catalog => [];

        public async Task RemoveAsync(Guid serviceId, bool deleteData, CancellationToken ct)
        {
            if (refuses.Contains(serviceId))
                throw new InvalidOperationException($"container for service {serviceId} would not stop");

            var svc = await db.ManagedServices.IgnoreQueryFilters().FirstAsync(s => s.Id == serviceId, ct);
            db.ManagedServices.Remove(svc);
            await db.SaveChangesAsync(ct);
            Deleted.Add(serviceId);
        }

        public Task QueueProvisionAsync(Guid serviceId, CancellationToken ct) => throw new NotSupportedException();
        public Task StartAsync(Guid serviceId, CancellationToken ct) => throw new NotSupportedException();
        public Task StopAsync(Guid serviceId, CancellationToken ct) => throw new NotSupportedException();
        public Task<long?> MeasureStorageAsync(Guid serviceId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<RotatedApp>> RotatePasswordAsync(Guid serviceId, CancellationToken ct) => throw new NotSupportedException();
        public Task<string?> TestConnectionAsync(Guid serviceId, CancellationToken ct) => throw new NotSupportedException();
        public Task<ServiceConnectionInfo> GetConnectionInfoAsync(Guid serviceId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<string, string>> BuildAttachEnvAsync(Guid serviceId, CancellationToken ct) => throw new NotSupportedException();
        public Task<RedisMemoryPolicyOutcome> UpdateRedisMemoryPolicyAsync(Guid serviceId, string? policy, long maxMemoryBytes, CancellationToken ct) => throw new NotSupportedException();
    }

    private static (Project Project, ProjectEnvironment Production, ProjectEnvironment Staging) SeedProjectWithTwoEnvironments(
        HarboraDbContext db, Guid workspaceId, string slug)
    {
        var project = new Project { WorkspaceId = workspaceId, Name = slug, Slug = slug };
        var production = new ProjectEnvironment
        {
            WorkspaceId = workspaceId, ProjectId = project.Id, Name = "Production", Slug = "production", IsDefault = true
        };
        var staging = new ProjectEnvironment
        {
            WorkspaceId = workspaceId, ProjectId = project.Id, Name = "Staging", Slug = "staging"
        };
        db.Projects.Add(project);
        db.Environments.AddRange(production, staging);
        return (project, production, staging);
    }

    private static App SeedApp(HarboraDbContext db, Guid workspaceId, Guid environmentId, string name)
    {
        var app = new App
        {
            WorkspaceId = workspaceId, EnvironmentId = environmentId, ServerId = Guid.NewGuid(),
            Name = name, Slug = name + "-" + Guid.NewGuid().ToString("N")[..8],
            SourceType = AppSourceType.PrebuiltImage, PrebuiltImage = "ghcr.io/example/" + name
        };
        db.Apps.Add(app);
        return app;
    }

    private static ManagedService SeedDatabase(HarboraDbContext db, Guid workspaceId, Guid environmentId, string name)
    {
        var svc = new ManagedService
        {
            WorkspaceId = workspaceId, EnvironmentId = environmentId, ServerId = Guid.NewGuid(),
            Name = name, ContainerName = "harbora-svc-" + name + "-" + Guid.NewGuid().ToString("N")[..8],
            Type = ManagedServiceType.PostgreSql, DatabaseName = name, VolumeName = name + "-data"
        };
        db.ManagedServices.Add(svc);
        return svc;
    }

    [Fact]
    public async Task Plan_and_delete_agree_on_the_same_apps_and_databases()
    {
        await using var db = NewDb();
        var workspaceId = Guid.NewGuid();
        var (project, production, staging) = SeedProjectWithTwoEnvironments(db, workspaceId, "seam-check");
        var api = SeedApp(db, workspaceId, production.Id, "api");
        var worker = SeedApp(db, workspaceId, staging.Id, "worker"); // a different environment — the
        // plan has to look across all of a project's environments, not just one.
        var orders = SeedDatabase(db, workspaceId, production.Id, "orders-db");
        await db.SaveChangesAsync();

        var appOps = new FakeAppOps(db, refuses: []);
        var serviceEngine = new FakeServiceEngine(db, refuses: []);
        var service = new ProjectDeletionService(
            db, appOps, serviceEngine, NullLogger<ProjectDeletionService>.Instance);

        var plan = await service.PlanAsync(workspaceId, project.Id, CancellationToken.None);

        plan.Should().NotBeNull();
        plan!.Value.Apps.Select(a => a.Id).Should().BeEquivalentTo([api.Id, worker.Id]);
        plan.Value.Databases.Select(d => d.Id).Should().BeEquivalentTo([orders.Id]);
        plan.Value.Apps.First(a => a.Id == worker.Id).EnvironmentName.Should().Be("Staging",
            "the plan has to say which environment holds each app, not just its own name");

        var outcome = await service.DeleteAsync(workspaceId, project.Id, CancellationToken.None);

        outcome.FullyDeleted.Should().BeTrue();
        appOps.Deleted.Should().BeEquivalentTo([api.Id, worker.Id],
            "DeleteAsync must destroy exactly the apps PlanAsync named — not a second, independently computed set");
        serviceEngine.Deleted.Should().BeEquivalentTo([orders.Id]);

        (await db.Apps.IgnoreQueryFilters().AnyAsync(a => a.Id == api.Id || a.Id == worker.Id)).Should().BeFalse();
        (await db.ManagedServices.IgnoreQueryFilters().AnyAsync(s => s.Id == orders.Id)).Should().BeFalse();
        (await db.Projects.IgnoreQueryFilters().AnyAsync(p => p.Id == project.Id)).Should().BeFalse(
            "nothing was left behind, so the project itself goes too");
        (await db.Environments.IgnoreQueryFilters().AnyAsync(e => e.ProjectId == project.Id)).Should().BeFalse(
            "the project's own environments cascade with it once nothing points at them any more");
    }

    [Fact]
    public async Task A_project_with_nothing_in_any_environment_has_an_empty_plan_that_confirms_itself()
    {
        await using var db = NewDb();
        var workspaceId = Guid.NewGuid();
        var (project, _, _) = SeedProjectWithTwoEnvironments(db, workspaceId, "empty-project");
        await db.SaveChangesAsync();

        var service = new ProjectDeletionService(
            db, new FakeAppOps(db, []), new FakeServiceEngine(db, []), NullLogger<ProjectDeletionService>.Instance);

        var plan = await service.PlanAsync(workspaceId, project.Id, CancellationToken.None);

        plan!.Value.IsEmpty.Should().BeTrue();
        plan.Value.IsConfirmed(null).Should().BeTrue("nothing would be destroyed, so no typed name is needed");
        plan.Value.IsConfirmed("garbage").Should().BeTrue();
    }

    [Fact]
    public async Task A_non_empty_plan_is_confirmed_only_by_the_exact_project_name()
    {
        await using var db = NewDb();
        var workspaceId = Guid.NewGuid();
        var (project, production, _) = SeedProjectWithTwoEnvironments(db, workspaceId, "typed-confirm");
        SeedApp(db, workspaceId, production.Id, "api");
        await db.SaveChangesAsync();

        var service = new ProjectDeletionService(
            db, new FakeAppOps(db, []), new FakeServiceEngine(db, []), NullLogger<ProjectDeletionService>.Instance);
        var plan = await service.PlanAsync(workspaceId, project.Id, CancellationToken.None);

        plan!.Value.IsConfirmed(null).Should().BeFalse("something would be destroyed — silence must not confirm it");
        plan.Value.IsConfirmed("").Should().BeFalse();
        plan.Value.IsConfirmed("typed-confirm-but-wrong").Should().BeFalse();
        plan.Value.IsConfirmed(project.Name.ToUpperInvariant()).Should().BeFalse(
            "a case-insensitive match would let a careless paste through — the same ordinal rule ServiceRemovalPlan uses");
        plan.Value.IsConfirmed(project.Name).Should().BeTrue();
        plan.Value.IsConfirmed("  " + project.Name + "  ").Should().BeTrue("surrounding whitespace from a copy-paste is not a different name");
    }

    [Fact]
    public async Task An_app_whose_container_will_not_stop_is_named_and_left_behind_rather_than_the_delete_claiming_success()
    {
        await using var db = NewDb();
        var workspaceId = Guid.NewGuid();
        var (project, production, _) = SeedProjectWithTwoEnvironments(db, workspaceId, "partial-failure");
        var stuck = SeedApp(db, workspaceId, production.Id, "stuck-api");
        var fine = SeedApp(db, workspaceId, production.Id, "fine-worker");
        var orders = SeedDatabase(db, workspaceId, production.Id, "orders-db");
        await db.SaveChangesAsync();

        var appOps = new FakeAppOps(db, refuses: [stuck.Id]);
        var serviceEngine = new FakeServiceEngine(db, refuses: []);
        var service = new ProjectDeletionService(
            db, appOps, serviceEngine, NullLogger<ProjectDeletionService>.Instance);

        var outcome = await service.DeleteAsync(workspaceId, project.Id, CancellationToken.None);

        outcome.FullyDeleted.Should().BeFalse("the project must never be reported deleted while an app row is still there");
        outcome.RemainingApps.Should().BeEquivalentTo(["stuck-api"],
            "the result has to say which app is still there, not just that something went wrong");
        outcome.RemainingDatabases.Should().BeEmpty();

        (await db.Apps.IgnoreQueryFilters().AnyAsync(a => a.Id == stuck.Id)).Should().BeTrue(
            "the container that would not stop must still have its row — deleting it anyway would orphan the container");
        (await db.Apps.IgnoreQueryFilters().AnyAsync(a => a.Id == fine.Id)).Should().BeFalse(
            "one stuck item must not stop the rest of the project's best-effort teardown");
        (await db.ManagedServices.IgnoreQueryFilters().AnyAsync(s => s.Id == orders.Id)).Should().BeFalse();
        (await db.Projects.IgnoreQueryFilters().AnyAsync(p => p.Id == project.Id)).Should().BeTrue(
            "the project itself must survive so long as anything inside it does");
    }

    [Fact]
    public async Task The_plan_names_the_domains_and_scheduled_functions_that_cascade_with_their_app()
    {
        await using var db = NewDb();
        var workspaceId = Guid.NewGuid();
        var (project, production, _) = SeedProjectWithTwoEnvironments(db, workspaceId, "cascades-named");
        var api = SeedApp(db, workspaceId, production.Id, "api");
        db.Domains.Add(new DomainName { AppId = api.Id, Host = "api.example.com" });
        db.FunctionDefinitions.Add(new FunctionDefinition
        {
            AppId = api.Id, WorkspaceId = workspaceId, Name = "nightly-report", Slug = "nightly-report",
            Trigger = FunctionTrigger.Cron, CronExpression = "0 3 * * *", Code = "// ..."
        });
        db.FunctionDefinitions.Add(new FunctionDefinition
        {
            AppId = api.Id, WorkspaceId = workspaceId, Name = "webhook", Slug = "webhook",
            Trigger = FunctionTrigger.Http, Code = "// ..."
        });
        await db.SaveChangesAsync();

        var service = new ProjectDeletionService(
            db, new FakeAppOps(db, []), new FakeServiceEngine(db, []), NullLogger<ProjectDeletionService>.Instance);

        var plan = await service.PlanAsync(workspaceId, project.Id, CancellationToken.None);

        plan!.Value.DomainHosts.Should().BeEquivalentTo(["api.example.com"]);
        plan.Value.ScheduledFunctionCount.Should().Be(1,
            "only the Cron-triggered function is a scheduled job — the Http-triggered one is not");
    }
}
