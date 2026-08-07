using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Backups;
using Harbora.Domain.Common;
using Harbora.Domain.Deployments;
using Harbora.Domain.Identity;
using Harbora.Domain.Networking;
using Harbora.Domain.Services;
using Harbora.Infrastructure.Deployments;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Global query filters (completes P13). Controllers already scope their queries by hand; these
/// filters make isolation a property of the model, so a query that forgets returns nothing rather
/// than another tenant's data — the failure mode becomes "missing", not "leaked".
///
/// Note the InMemory provider evaluates query filters, so these are real assertions about the
/// model rather than a stand-in.
/// </summary>
public class WorkspaceQueryFilterTests
{
    private static readonly Guid TenantA = Guid.NewGuid();
    private static readonly Guid TenantB = Guid.NewGuid();

    private readonly string _dbName = "filters-" + Guid.NewGuid();

    /// <summary>A context that sees one tenant, exactly as a web request does.</summary>
    private HarboraDbContext AsTenant(Guid workspaceId) => new(
        new DbContextOptionsBuilder<HarboraDbContext>().UseInMemoryDatabase(_dbName).Options,
        new FixedWorkspaceScope(workspaceId));

    /// <summary>A context that spans tenants, exactly as a background job does.</summary>
    private HarboraDbContext AsSystem() => new(
        new DbContextOptionsBuilder<HarboraDbContext>().UseInMemoryDatabase(_dbName).Options,
        SystemWorkspaceScope.Instance);

    private async Task<(Guid AppA, Guid AppB, Guid DeploymentB)> SeedAsync()
    {
        using var db = AsSystem();
        var appA = new App { Id = Guid.NewGuid(), WorkspaceId = TenantA, Name = "A", Slug = "a" };
        var appB = new App { Id = Guid.NewGuid(), WorkspaceId = TenantB, Name = "B", Slug = "b" };
        var deploymentB = new Deployment
        {
            Id = Guid.NewGuid(), AppId = appB.Id, WorkspaceId = TenantB,
            Number = 1, Status = DeploymentStatus.Succeeded
        };

        db.Apps.AddRange(appA, appB);
        db.Deployments.Add(deploymentB);
        db.Routes.Add(new Route { Id = Guid.NewGuid(), WorkspaceId = TenantB, AppId = appB.Id, Host = "b.example.com" });
        db.Domains.Add(new DomainName { Id = Guid.NewGuid(), AppId = appB.Id, Host = "b.example.com" });
        db.EnvironmentVariables.Add(new EnvironmentVariable { Id = Guid.NewGuid(), AppId = appB.Id, Key = "SECRET", Value = "x" });
        db.ManagedServices.Add(new ManagedService { Id = Guid.NewGuid(), WorkspaceId = TenantB, Name = "pg" });
        db.Backups.Add(new Backup
        { Id = Guid.NewGuid(), WorkspaceId = TenantB, Type = BackupType.Volume, Status = BackupStatus.Completed });
        await db.SaveChangesAsync();

        return (appA.Id, appB.Id, deploymentB.Id);
    }

    // ---- the point of the exercise: an unscoped query is now safe ----

    [Fact]
    public async Task A_query_that_forgets_to_scope_cannot_see_another_tenants_app()
    {
        var (_, appB, _) = await SeedAsync();
        using var db = AsTenant(TenantA);

        // Deliberately written the "wrong" way — no WorkspaceId predicate anywhere.
        var found = await db.Apps.FirstOrDefaultAsync(a => a.Id == appB);

        found.Should().BeNull("the model must protect a query the developer forgot to scope");
    }

    [Fact]
    public async Task A_deployment_cannot_be_reached_by_id_from_another_tenant()
    {
        var (_, _, deploymentB) = await SeedAsync();
        using var db = AsTenant(TenantA);

        // Deployment ids appear in URLs, so this is the natural id-guessing target.
        var found = await db.Deployments.FirstOrDefaultAsync(d => d.Id == deploymentB);

        found.Should().BeNull();
    }

    [Theory]
    [InlineData("routes")]
    [InlineData("services")]
    [InlineData("backups")]
    public async Task Tenant_owned_collections_are_empty_for_another_tenant(string entity)
    {
        await SeedAsync();
        using var db = AsTenant(TenantA);

        var count = entity switch
        {
            "routes" => await db.Routes.CountAsync(),
            "services" => await db.ManagedServices.CountAsync(),
            "backups" => await db.Backups.CountAsync(),
            _ => throw new ArgumentOutOfRangeException(nameof(entity))
        };

        count.Should().Be(0);
    }

    [Fact]
    public async Task Child_tables_are_unfiltered_but_only_reachable_through_a_filtered_parent()
    {
        // EnvironmentVariable/Volume/DomainName are deliberately NOT filtered: they are only ever
        // loaded via their app, and a navigation filter would add a join to every read plus the
        // inner-join hazard that cost us the Deployment filter. This test states that trade-off
        // explicitly rather than leaving it implied.
        await SeedAsync();
        using var db = AsTenant(TenantA);

        db.EnvironmentVariables.Count().Should().Be(1, "the table itself carries no tenant filter");

        // The path the application actually uses yields nothing, because the app is filtered.
        var viaApp = await db.Apps.Include(a => a.EnvironmentVariables)
            .SelectMany(a => a.EnvironmentVariables).CountAsync();
        viaApp.Should().Be(0);
    }

    [Fact]
    public async Task A_tenant_still_sees_its_own_data()
    {
        var (appA, _, _) = await SeedAsync();
        using var db = AsTenant(TenantA);

        (await db.Apps.FirstOrDefaultAsync(a => a.Id == appA)).Should().NotBeNull(
            "the filter must not be so aggressive that it hides the caller's own data");
    }

    // ---- system work must still span tenants ----

    [Fact]
    public async Task Background_work_sees_every_tenant()
    {
        await SeedAsync();
        using var db = AsSystem();

        // The deploy pipeline, reconcilers and schedulers all run this way. If the filter applied
        // here, deployments would simply stop working.
        (await db.Apps.CountAsync()).Should().Be(2);
        (await db.Deployments.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task A_newly_queued_deployment_is_visible_to_the_tenant_that_triggered_it()
    {
        // The denormalised column only works if it is actually stamped. Miss it and the deployment
        // still builds and releases — background work is unscoped — but never appears in the UI of
        // the tenant who asked for it, which looks like the deploy silently vanished.
        var (appA, _, _) = await SeedAsync();

        Guid deploymentId;
        using (var system = AsSystem())
        {
            var engine = new DeploymentEngine(system, new RecordingQueue(), new FixedClock());
            deploymentId = await engine.QueueDeploymentAsync(
                new DeploymentRequest(appA, DeploymentTrigger.Manual, Guid.NewGuid()), default);
        }

        using var asTenant = AsTenant(TenantA);
        (await asTenant.Deployments.FirstOrDefaultAsync(d => d.Id == deploymentId))
            .Should().NotBeNull("the tenant must be able to watch the deployment they just triggered");
    }

    private sealed class RecordingQueue : IJobQueue
    {
        public Task<Guid> EnqueueAsync(Harbora.Domain.Jobs.JobKind kind, Guid targetId, CancellationToken ct = default)
            => Task.FromResult(Guid.NewGuid());
        public Task<Guid> EnqueueExclusiveAsync(
            Harbora.Domain.Jobs.JobKind kind, Guid targetId, Guid exclusiveWith, CancellationToken ct = default)
            => Task.FromResult(Guid.NewGuid());
        public Task<bool> RequestCancellationAsync(Harbora.Domain.Jobs.JobKind kind, Guid targetId, CancellationToken ct = default)
            => Task.FromResult(false);
    }

    [Fact]
    public async Task Background_work_still_sees_a_deployment_whose_app_row_is_gone()
    {
        // Regression guard. Filtering Deployment through its App navigation made EF emit an INNER
        // JOIN (AppId is non-nullable), which silently hid orphaned deployments from EVERY query —
        // including the crash reconciler, whose whole job is to find stranded deployments. The
        // denormalised WorkspaceId has no such failure mode.
        using (var seed = AsSystem())
        {
            seed.Deployments.Add(new Deployment
            {
                Id = Guid.NewGuid(), AppId = Guid.NewGuid(), WorkspaceId = TenantA,
                Number = 1, Status = DeploymentStatus.Queued
            });
            await seed.SaveChangesAsync();
        }

        using var db = AsSystem();
        (await db.Deployments.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task The_default_constructor_is_system_scoped()
    {
        await SeedAsync();
        using var db = new HarboraDbContext(
            new DbContextOptionsBuilder<HarboraDbContext>().UseInMemoryDatabase(_dbName).Options);

        (await db.Apps.CountAsync()).Should().Be(2, "existing background call sites construct it this way");
    }

    [Fact]
    public async Task An_admin_page_can_opt_out_explicitly()
    {
        await SeedAsync();
        using var db = AsTenant(TenantA);

        // The tenants page and the server-in-use check genuinely need every workspace.
        var all = await db.Apps.IgnoreQueryFilters().CountAsync();

        all.Should().Be(2);
    }

    // ---- bootstrap queries: the ones that ESTABLISH scope ----

    [Fact]
    public async Task Sign_in_can_resolve_the_users_workspace_before_they_have_one()
    {
        // The bug this pins cost a production outage. Login reads WorkspaceMembers to decide which
        // workspace the caller belongs to — but at that moment the request has no workspace claim,
        // so the scope is Guid.Empty. With the filter applied, that query returns nothing, every
        // user signs in scoped to an empty workspace, their dashboard is blank, and any app they
        // create is stamped Guid.Empty and can never be deployed.
        var userId = Guid.NewGuid();
        using (var seed = AsSystem())
        {
            seed.WorkspaceMembers.Add(new WorkspaceMember
            { Id = Guid.NewGuid(), WorkspaceId = TenantA, UserId = userId });
            await seed.SaveChangesAsync();
        }

        // Exactly the state of a login request: an HttpContext exists, no workspace claim yet.
        using var duringLogin = AsTenant(Guid.Empty);

        var resolved = await duringLogin.WorkspaceMembers.IgnoreQueryFilters()
            .Where(m => m.UserId == userId)
            .Select(m => m.WorkspaceId)
            .FirstOrDefaultAsync();

        resolved.Should().Be(TenantA,
            "the query that decides the caller's workspace cannot itself be scoped by that workspace");
    }

    [Fact]
    public async Task Without_the_bypass_the_sign_in_lookup_finds_nothing()
    {
        // States the trap plainly: the same query without IgnoreQueryFilters is silently empty —
        // no exception, no error, just a user with no workspace.
        var userId = Guid.NewGuid();
        using (var seed = AsSystem())
        {
            seed.WorkspaceMembers.Add(new WorkspaceMember
            { Id = Guid.NewGuid(), WorkspaceId = TenantA, UserId = userId });
            await seed.SaveChangesAsync();
        }

        using var duringLogin = AsTenant(Guid.Empty);

        var resolved = await duringLogin.WorkspaceMembers
            .Where(m => m.UserId == userId)
            .Select(m => m.WorkspaceId)
            .FirstOrDefaultAsync();

        resolved.Should().Be(Guid.Empty, "this is the failure mode the bypass exists to prevent");
    }

    // ---- deny by default ----

    [Fact]
    public async Task A_request_with_no_workspace_sees_nothing()
    {
        await SeedAsync();
        using var db = AsTenant(Guid.Empty);

        // An unauthenticated or not-yet-onboarded caller must not fall back to seeing everything.
        (await db.Apps.CountAsync()).Should().Be(0);
        (await db.Deployments.CountAsync()).Should().Be(0);
    }

    // ---- platform-level data stays reachable ----

    [Fact]
    public async Task Platform_entities_are_not_filtered()
    {
        using (var seed = AsSystem())
        {
            seed.Servers.Add(new Harbora.Domain.Servers.Server { Id = Guid.NewGuid(), Name = "node" });
            seed.AuditLogs.Add(new Harbora.Domain.Auditing.AuditLog { Id = Guid.NewGuid(), Action = "user.login" });
            await seed.SaveChangesAsync();
        }
        using var db = AsTenant(TenantA);

        // Login, setup and the audit trail all need to work before or across any workspace.
        (await db.Servers.CountAsync()).Should().Be(1);
        (await db.AuditLogs.CountAsync()).Should().Be(1);
    }

    // ---- writes ----

    [Fact]
    public async Task A_tenant_cannot_load_and_therefore_cannot_modify_another_tenants_app()
    {
        var (_, appB, _) = await SeedAsync();
        using var db = AsTenant(TenantA);

        var target = await db.Apps.FirstOrDefaultAsync(a => a.Id == appB);
        target.Should().BeNull("an update path that loads by id first is protected by the same filter");

        using var verify = AsSystem();
        (await verify.Apps.FirstAsync(a => a.Id == appB)).Name.Should().Be("B");
    }
}
