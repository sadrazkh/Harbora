using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Common;
using Harbora.Domain.Services;
using Harbora.Infrastructure.Deployments;
using Harbora.Infrastructure.Nodes;
using Harbora.Infrastructure.Services;
using Harbora.Tests.Fakes;
using Harbora.Web.Controllers;
using Harbora.Web.ViewModels;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The external-access page, as a caller reaches it.
///
/// The properties worth holding here are not about grants in general — those live in
/// <see cref="DatabaseAccessLifecycleTests"/> — but about what one URL may reach. A grant id is a
/// guessable-shaped value arriving from outside, and the route it arrives on names a database that
/// may not be the one it belongs to.
/// </summary>
public class DatabaseAccessPageTests
{
    private sealed class Clock : ISystemClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 8, 4, 9, 0, 0, TimeSpan.Zero);
    }

    private sealed class Caller(Guid workspaceId) : ICurrentUser
    {
        public Guid? UserId { get; } = Guid.CreateVersion7();
        public string? Email => "me@example.com";
        public bool IsAuthenticated => true;
        public Guid? WorkspaceId { get; } = workspaceId;
    }

    private sealed record Fixture(
        HarboraDbContext Db, DatabasesController Controller, ManagedService Database, Guid WorkspaceId);

    /// <summary>Harbora's own database, able to refuse one save on demand.</summary>
    private sealed class BrittleContext(DbContextOptions<HarboraDbContext> options) : HarboraDbContext(options)
    {
        public Exception? FailTheNextSaveWith { get; set; }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            if (FailTheNextSaveWith is not { } failure) return base.SaveChangesAsync(cancellationToken);

            FailTheNextSaveWith = null;
            throw failure;
        }
    }

    private sealed record LocalFixture(
        BrittleContext Db, DatabasesController Controller, ManagedService Database, FakeDockerEngine Docker);

    /// <summary>
    /// The single-server install, reached through the controller rather than the service.
    ///
    /// <para>
    /// <see cref="Build"/> above is the install with no local reach, where
    /// <see cref="ExternalAccessAvailability"/> refuses every access action before it starts. That
    /// makes it the wrong fixture for anything about what a rotation puts on the screen, because no
    /// rotation ever runs on it.
    /// </para>
    /// </summary>
    private static LocalFixture BuildLocal()
    {
        var db = new BrittleContext(new DbContextOptionsBuilder<HarboraDbContext>()
            .UseInMemoryDatabase("dbaccess-page-local-" + Guid.NewGuid()).Options);

        var workspaceId = Guid.CreateVersion7();
        db.Add(new Harbora.Domain.Identity.Workspace { Id = workspaceId, Name = "Acme", Slug = "acme" });

        var protector = new PassthroughProtector();
        var database = new ManagedService
        {
            Id = Guid.CreateVersion7(), WorkspaceId = workspaceId,

            // Guid.Empty is the panel's own machine, which is what the gateway insists on before it
            // will publish a port.
            ServerId = Guid.Empty,
            Name = "Shop DB", ContainerName = "harbora-svc-shop", DatabaseName = "shop",
            Username = "postgres", EncryptedPassword = protector.Protect("admin_secret"),
            InternalPort = 5432, Type = ManagedServiceType.PostgreSql, Status = ServiceStatus.Running
        };
        db.Add(database);

        var currentUser = new Caller(workspaceId);
        db.Add(new Harbora.Domain.Identity.User
        {
            Id = currentUser.UserId!.Value,
            Email = currentUser.Email!,
            DisplayName = "Tester",
            Role = SystemRole.Owner,
            ScopedToProjects = false
        });
        db.SaveChanges();

        var docker = new FakeDockerEngine();
        var engines = new FakeServerEngineFactory(docker);
        var clock = new Clock();
        var node = new FakeNodeAgentClient(NullLogger<FakeNodeAgentClient>.Instance);

        var access = new DatabaseAccessService(
            db, node, clock, NullLogger<DatabaseAccessService>.Instance,
            new DockerTcpGateway(db, engines, NullLogger<DockerTcpGateway>.Instance),
            new DatabaseGrantExecutor(docker, protector, NullLogger<DatabaseGrantExecutor>.Instance),
            new ManagedServiceEngine(
                db, engines, protector, new NoopJobQueue(),
                Microsoft.Extensions.Options.Options.Create(new HarboraRuntimeOptions()), clock,
                NullLogger<ManagedServiceEngine>.Instance),
            protector);

        var controller = new DatabasesController(
            db,
            new FakeManagedServiceEngine(),
            new AlwaysAllowedQuota(),
            new PlacesOnTheFirstServer(db),
            protector,
            new Harbora.Infrastructure.Projects.ProjectService(db, clock),
            new ServiceUsageService(db, protector),
            new Harbora.Infrastructure.Security.ProjectAccessService(db, currentUser),
            access,
            adminer: null!,
            audit: new SilentAudit(),
            node,
            currentUser)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        return new LocalFixture(db, controller, database, docker);
    }

    private static Fixture Build(ServiceStatus status = ServiceStatus.Running)
    {
        var db = new HarboraDbContext(new DbContextOptionsBuilder<HarboraDbContext>()
            .UseInMemoryDatabase("dbaccess-page-" + Guid.NewGuid()).Options);

        var workspaceId = Guid.CreateVersion7();
        var database = new ManagedService
        {
            Id = Guid.CreateVersion7(), WorkspaceId = workspaceId, ServerId = Guid.CreateVersion7(),
            Name = "Shop DB", ContainerName = "harbora-svc-shop", DatabaseName = "shop",
            InternalPort = 5432, Type = ManagedServiceType.PostgreSql, Status = status
        };
        db.Add(database);

        var currentUser = new Caller(workspaceId);

        // A real user row, because the access rule denies anyone it cannot find rather than falling
        // back to a default role — which is how a deleted account would keep working.
        db.Add(new Harbora.Domain.Identity.User
        {
            Id = currentUser.UserId!.Value,
            Email = currentUser.Email!,
            DisplayName = "Tester",
            Role = SystemRole.Owner,
            ScopedToProjects = false
        });
        db.SaveChanges();
        var protector = new PassthroughProtector();
        var node = new FakeNodeAgentClient(NullLogger<FakeNodeAgentClient>.Instance);

        var controller = new DatabasesController(
            db,
            new FakeManagedServiceEngine(),
            new AlwaysAllowedQuota(),
            new PlacesOnTheFirstServer(db),
            protector,
            new Harbora.Infrastructure.Projects.ProjectService(db, new Clock()),
            new ServiceUsageService(db, protector),
            new Harbora.Infrastructure.Security.ProjectAccessService(db, currentUser),
            new DatabaseAccessService(db, node, new Clock(), NullLogger<DatabaseAccessService>.Instance),
            // The admin tool is not exercised by these tests; it is here because the controller
            // takes it. Its own rules are covered by AdminerSessionTests.
            adminer: null!,
            audit: new SilentAudit(),
            node,
            currentUser)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        return new Fixture(db, controller, database, workspaceId);
    }

    /// <summary>
    /// Places on whatever server the fixture created. The real scheduler needs capacity reports
    /// from a node, which this test has no business standing up — it is about the access page.
    /// </summary>
    private sealed class PlacesOnTheFirstServer(HarboraDbContext db) : ISchedulerService
    {
        public Task<PlacementResult> PlaceAsync(long memoryBytes, double cpu, string? pool, CancellationToken ct) =>
            Task.FromResult(db.Servers.Select(s => s.Id).FirstOrDefault() is { } id && id != Guid.Empty
                ? PlacementResult.Placed(id)
                : PlacementResult.Fail("No server."));

        public Task<PlacementResult> CheckAsync(Guid serverId, long memoryBytes, double cpu, CancellationToken ct) =>
            Task.FromResult(PlacementResult.Placed(serverId));
    }

    private sealed class AlwaysAllowedQuota : IQuotaService
    {
        public Task<WorkspaceUsage> GetUsageAsync(Guid w, CancellationToken ct) => throw new NotSupportedException();
        public Task<QuotaCheck> CanAddAppAsync(Guid w, string? s, Guid? e, CancellationToken ct) => Task.FromResult(QuotaCheck.Ok);
        public Task<QuotaCheck> CanAddServiceAsync(Guid w, string? size, CancellationToken ct) => Task.FromResult(QuotaCheck.Ok);
    }

    private static DatabaseAccessGrant Grant(Fixture f, Guid? serviceId = null, Guid? workspaceId = null) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            WorkspaceId = workspaceId ?? f.WorkspaceId,
            ManagedServiceId = serviceId ?? f.Database.Id,
            Username = "hb_reader",
            PasswordHash = "hash",
            Kind = DatabaseAccessKind.Temporary,
            Status = DatabaseAccessStatus.Active,
            ExpiresAt = new DateTimeOffset(2026, 8, 4, 18, 0, 0, TimeSpan.Zero)
        };

    private static DatabaseAccessPageViewModel PageOf(IActionResult result) =>
        (DatabaseAccessPageViewModel)((ViewResult)result).Model!;

    [Fact]
    public async Task The_page_explains_why_access_cannot_be_offered_rather_than_hiding_the_form()
    {
        // A missing form reads as a feature that was removed, and somebody waits for it to come
        // back. The stated reason is what stops that.
        var f = Build();

        var page = PageOf(await f.Controller.Access(f.Database.Id, CancellationToken.None));

        page.Unavailable.Should().NotBeNull();
        page.Unavailable!.Reason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Nothing_is_issued_while_the_node_agent_is_only_simulated()
    {
        // The point of the guard. A grant here would hand somebody a working-looking connection
        // string for a gateway that does not exist.
        var f = Build();

        var page = PageOf(await f.Controller.IssueAccess(
            f.Database.Id, DatabaseAccessKind.Temporary, 60, null, CancellationToken.None));

        page.Error.Should().NotBeNullOrWhiteSpace();
        page.Issued.Should().BeNull();
        (await f.Db.DatabaseAccessGrants.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_grant_belonging_to_another_database_cannot_be_revoked_through_this_one()
    {
        // Both the workspace and the database in the route are checked. Without the second, the
        // action would succeed and the audit trail would record it against the wrong service.
        var f = Build();
        var elsewhere = Grant(f, serviceId: Guid.CreateVersion7());
        f.Db.Add(elsewhere);
        await f.Db.SaveChangesAsync();

        var result = await f.Controller.RevokeAccess(f.Database.Id, elsewhere.Id, CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
        (await f.Db.DatabaseAccessGrants.SingleAsync()).Status.Should().Be(DatabaseAccessStatus.Active);
    }

    [Fact]
    public async Task A_grant_belonging_to_another_workspace_is_invisible()
    {
        var f = Build();
        var theirs = Grant(f, workspaceId: Guid.CreateVersion7());
        f.Db.Add(theirs);
        await f.Db.SaveChangesAsync();

        var result = await f.Controller.RevokeAccess(f.Database.Id, theirs.Id, CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task A_grant_can_always_be_closed_even_when_the_agent_is_simulated()
    {
        // Refusing here would leave a row nobody can shut. A grant that cannot be revoked is worse
        // than one that was never issued.
        var f = Build();
        var grant = Grant(f);
        f.Db.Add(grant);
        await f.Db.SaveChangesAsync();

        var page = PageOf(await f.Controller.RevokeAccess(f.Database.Id, grant.Id, CancellationToken.None));

        page.Error.Should().BeNull();
        (await f.Db.DatabaseAccessGrants.SingleAsync()).Status.Should().Be(DatabaseAccessStatus.Revoked);
    }

    [Fact]
    public async Task Closed_grants_stay_on_the_page()
    {
        // "Who opened this database in March, and for how long" is a question that gets asked, and a
        // list showing only what is open cannot answer it.
        var f = Build();
        var grant = Grant(f);
        grant.Status = DatabaseAccessStatus.Expired;
        f.Db.Add(grant);
        await f.Db.SaveChangesAsync();

        var page = PageOf(await f.Controller.Access(f.Database.Id, CancellationToken.None));

        page.Grants.Should().ContainSingle().Which.Status.Should().Be(DatabaseAccessStatus.Expired);
    }

    [Fact]
    public async Task The_page_never_carries_a_password_it_did_not_just_create()
    {
        // Only the action that generated one may fill Issued. Loading the page again must not show
        // it, and cannot: only the hash was kept.
        var f = Build();
        f.Db.Add(Grant(f));
        await f.Db.SaveChangesAsync();

        var page = PageOf(await f.Controller.Access(f.Database.Id, CancellationToken.None));

        page.Issued.Should().BeNull();
    }

    /// <summary>
    /// A rotation Harbora could not record still puts the password on the page.
    ///
    /// <para>
    /// This is the only screen the new password ever appears on. Before, the failed save threw
    /// straight past the action and the operator landed on the generic error page — which reads as
    /// "nothing happened", while the database was holding a password no human had seen and the old
    /// one no longer worked. So the banner and the credential panel are shown together: the banner
    /// because the old password is dead, the panel because this is the last chance at the new one.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_rotation_the_panel_could_not_record_still_shows_the_password_on_the_page()
    {
        var f = BuildLocal();

        var issuedPage = PageOf(await f.Controller.IssueAccess(
            f.Database.Id, DatabaseAccessKind.Persistent, 0, null, CancellationToken.None));
        issuedPage.Issued.Should().NotBeNull("the fixture has local reach, so a grant can be made");

        var grant = await f.Db.DatabaseAccessGrants.SingleAsync();
        f.Db.FailTheNextSaveWith = new DbUpdateException("Harbora's own database went away.");

        var page = PageOf(await f.Controller.RotateAccess(
            f.Database.Id, grant.Id, CancellationToken.None));

        page.Error.Should().NotBeNullOrWhiteSpace("the operator has to be told the old password is gone");
        page.Issued.Should().NotBeNull("this page is the only place the new password will ever exist");
        page.Issued!.Password.Should().NotBe(issuedPage.Issued!.Password);
        page.Issued.Rotated.Should().BeTrue();

        f.Docker.OneOffCommands
            .Should().ContainSingle(c => c.Contains("ALTER USER", StringComparison.Ordinal))
            .Which.Should().Contain(page.Issued.Password,
                "the password shown must be the one the database was actually given");
    }

    [Fact]
    public void Every_action_reaches_the_database_and_its_grants_through_the_scoped_helpers()
    {
        // The workspace and service filters live in two helpers. An action that queries the sets
        // directly would compile, work in every test written against one workspace, and be a tenant
        // leak — so the rule is asserted on the shape of the file rather than on behaviour, which is
        // the only place a not-yet-written action can be caught.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Harbora.slnx")))
            dir = dir.Parent;
        dir.Should().NotBeNull();

        var source = File.ReadAllText(Path.Combine(
            dir!.FullName, "src", "Harbora.Web", "Controllers", "DatabaseAccessActions.cs"));

        // Split at the helpers: the two legitimate queries are below that line, the actions above.
        var helpers = source.IndexOf("// ---- helpers ----", StringComparison.Ordinal);
        helpers.Should().BeGreaterThan(0);

        var actions = source[..helpers];
        actions.Should().NotContain("db.ManagedServices",
            "an action must go through FindDatabaseAsync, which filters by workspace");
        actions.Should().NotContain("db.DatabaseAccessGrants",
            "an action must go through FindGrantAsync, which filters by workspace and by database");
    }

    [Fact]
    public async Task Another_workspaces_database_is_not_found()
    {
        var f = Build();
        var theirs = new ManagedService
        {
            Id = Guid.CreateVersion7(), WorkspaceId = Guid.CreateVersion7(), ServerId = Guid.CreateVersion7(),
            Name = "Theirs", ContainerName = "harbora-svc-theirs", InternalPort = 5432,
            Type = ManagedServiceType.PostgreSql, Status = ServiceStatus.Running
        };
        f.Db.Add(theirs);
        await f.Db.SaveChangesAsync();

        var result = await f.Controller.Access(theirs.Id, CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    private sealed class SilentAudit : IAuditLogger
    {
        public Task LogAsync(string action, string? targetType = null, string? targetId = null,
            string? ipAddress = null, string? actorEmailOverride = null, Guid? userIdOverride = null,
            string? metadataJson = null, CancellationToken ct = default) => Task.CompletedTask;
    }
}
