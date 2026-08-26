using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Common;
using Harbora.Domain.Identity;
using Harbora.Domain.Services;
using Harbora.Infrastructure.Deployments;
using Harbora.Infrastructure.Nodes;
using Harbora.Infrastructure.Services;
using Harbora.Tests.Fakes;
using Harbora.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Harbora.Tests.Billing;

/// <summary>
/// Starting and stopping a customer's managed database from a session that is not the customer's.
///
/// <para>
/// The caller these tests stand for is the provider console's credit button. It runs inside an HTTP
/// request, so <c>HttpWorkspaceScope.IsUnscoped</c> is false and the ambient workspace is the
/// <b>provider's</b> — never the tenant whose database is being brought back. Read through the tenant
/// filter, <c>db.ManagedServices.FirstAsync</c> then matches nothing and throws "Sequence contains no
/// elements" before a node is reached: every managed database stays down, the workspace stays
/// suspended, and the failure the customer is shown blames the node for not coming back. Crediting a
/// tenant is the only way money reaches an account, so this is the ordinary path, not an edge of one.
/// </para>
///
/// <para>
/// <b>The real engine is driven here, not a stand-in.</b> <c>FakeDatabaseOperations</c> in
/// <c>BillingSuspensionTests</c> reads with <c>IgnoreQueryFilters()</c>, which made every "a top-up
/// brings the database back" test green about something production could not do — a fake kinder than
/// production is the same defect as a check that reports success for work it never did, wearing the
/// other hat. No daemon is needed for this: the engine resolves an <see cref="IServerEngineFactory"/>
/// and lists containers, and <see cref="FakeDockerEngine"/> answers both.
/// </para>
///
/// <para>
/// <b>Widening a filter on a shared write path is how cross-tenant holes are made</b>
/// (<c>docs/product-audit/19-do-not-change-list.md</c> item 16), so the last three tests below hold
/// the other side: the two request-bound callers of this engine re-check ownership themselves, with
/// an explicit predicate that does not depend on the ambient filter at all.
/// </para>
/// </summary>
public class ManagedServiceEngineTenancyTests
{
    private const string ContainerName = "harbora-svc-orders";

    /// <summary>The workspace a provider administrator's own session belongs to. Never the customer's.</summary>
    private static readonly Guid ProviderWorkspace = new("d0c0ffee-0000-0000-0000-0000000000c0");

    // --- the engine, under somebody else's session --------------------------------------------

    [Fact]
    public async Task Starting_a_customers_database_from_the_provider_console_restarts_its_container()
    {
        await using var db = GateHarness.SystemContext();
        var workspaceId = GateHarness.SeedWorkspace(db, balanceMinor: 100_000);
        var serviceId = SeedDatabase(db, workspaceId, ServiceStatus.Stopped);
        await db.SaveChangesAsync();

        var docker = await DaemonHoldingTheCustomersContainer();
        await using var console = ProviderConsoleContext(db);

        await Engine(console, docker).StartAsync(serviceId, default);

        docker.Calls.Should().Contain(
            c => c.Operation == "RestartContainerAsync" && c.Target == ContainerName,
            "a filtered read matches nothing under the provider's own scope and throws before a node " +
            "is ever reached, so the container the customer paid to bring back is never touched");

        (await db.ManagedServices.IgnoreQueryFilters().AsNoTracking().SingleAsync())
            .Status.Should().Be(ServiceStatus.Running);
    }

    [Fact]
    public async Task Stopping_a_customers_database_from_the_provider_console_stops_its_container()
    {
        await using var db = GateHarness.SystemContext();
        var workspaceId = GateHarness.SeedWorkspace(db, balanceMinor: 100_000);
        var serviceId = SeedDatabase(db, workspaceId, ServiceStatus.Running);
        await db.SaveChangesAsync();

        var docker = await DaemonHoldingTheCustomersContainer();
        await using var console = ProviderConsoleContext(db);

        await Engine(console, docker).StopAsync(serviceId, default);

        // Fixed with StartAsync and never on its own. The pair is one route in both directions: a
        // suspension reached from any scoped caller stops nothing while writing every marker that
        // says it did, and the resume that reads those markers then has nothing left to correct.
        docker.Calls.Should().Contain(
            c => c.Operation == "StopContainerAsync" && c.Target == ContainerName,
            "the stop half of the same route reads the same table the same way");

        (await db.ManagedServices.IgnoreQueryFilters().AsNoTracking().SingleAsync())
            .Status.Should().Be(ServiceStatus.Stopped);
    }

    // --- and what still refuses, now that the filter no longer does ---------------------------

    /// <param name="theFilterIsInert">
    /// Both halves of one claim, and the second is why the first is worth anything. Scoped to the
    /// intruder's own workspace, the tenant filter and the controller's guard would each refuse this
    /// on their own, so a passing test says nothing about which did. Asked over a context that can
    /// see every tenant, the filter is inert and only <c>ProjectAccessService</c>'s explicit
    /// <c>WorkspaceId ==</c> predicate is left — which is exactly the arrangement this branch has
    /// just stopped relying on one layer down, and the thing that has to survive a filter being
    /// widened underneath it.
    /// </param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Starting_a_database_in_another_workspace_is_refused_before_the_node_is_reached(
        bool theFilterIsInert)
    {
        var fixture = ForeignDatabase(
            scope: theFilterIsInert ? SystemWorkspaceScope.Instance : null);

        var start = () => fixture.Controller.Start(fixture.ServiceId, default);

        await start.Should().ThrowAsync<UnauthorizedAccessException>(
            "the engine's read is unfiltered now, so the button in front of it is what keeps one " +
            "tenant out of another's database");
        fixture.Docker.Calls.Should().BeEmpty("nothing may reach a node for a database the caller does not own");
        (await fixture.Store.ManagedServices.IgnoreQueryFilters().AsNoTracking().SingleAsync())
            .Status.Should().Be(ServiceStatus.Stopped);
    }

    [Fact]
    public async Task Stopping_a_database_in_another_workspace_is_refused_before_the_node_is_reached()
    {
        // The other verb, because the engine's two reads were widened together and a guard is only
        // ever missing from one of a pair.
        var fixture = ForeignDatabase(ServiceStatus.Running);

        var stop = () => fixture.Controller.Stop(fixture.ServiceId, default);

        await stop.Should().ThrowAsync<UnauthorizedAccessException>();
        fixture.Docker.Calls.Should().BeEmpty();
        (await fixture.Store.ManagedServices.IgnoreQueryFilters().AsNoTracking().SingleAsync())
            .Status.Should().Be(ServiceStatus.Running, "a stop nobody was allowed to ask for did not happen");
    }

    // --- fixture ------------------------------------------------------------------------------

    private static Guid SeedDatabase(GateContext db, Guid workspaceId, ServiceStatus status)
    {
        var service = new ManagedService
        {
            WorkspaceId = workspaceId,
            ServerId = Guid.CreateVersion7(),
            Name = "orders",
            Type = ManagedServiceType.PostgreSql,
            Version = "16",
            ContainerName = ContainerName,
            VolumeName = ContainerName + "-data",
            Status = status
        };
        db.ManagedServices.Add(service);
        return service.Id;
    }

    /// <summary>The same rows, read through the scope a provider administrator's request actually has.</summary>
    private static GateContext ProviderConsoleContext(GateContext db) =>
        new(db.Store, new FixedWorkspaceScope(ProviderWorkspace));

    private static ManagedServiceEngine Engine(GateContext db, FakeDockerEngine docker) =>
        new(db, new SingleEngineFactory(docker), new PassthroughProtector(), new NoopJobQueue(),
            GateHarness.Gate(db), Options.Create(new HarboraRuntimeOptions()),
            new FixedClock(DateTimeOffset.UnixEpoch),
            NullLogger<ManagedServiceEngine>.Instance);

    /// <summary>
    /// A daemon with the customer's database container on it, labelled the way
    /// <c>ManagedServiceEngine.ProvisionAsync</c> labels one — <c>FakeDockerEngine.SeedContainer</c>
    /// writes an app's label, which this engine's list filter would skip.
    /// </summary>
    private static async Task<FakeDockerEngine> DaemonHoldingTheCustomersContainer()
    {
        var docker = new FakeDockerEngine();
        await docker.RunContainerAsync(new DockerRunRequest(
            "postgres:16", ContainerName, "harbora",
            new Dictionary<string, string>(),
            new Dictionary<string, string> { ["harbora.managed"] = "true", ["harbora.service"] = "orders" },
            [], 5432, 0, 0, null), default);
        return docker;
    }

    private sealed record ForeignFixture(
        HarboraDbContext Store, DatabasesController Controller, Guid ServiceId, FakeDockerEngine Docker);

    /// <summary>
    /// A database owned by one workspace, and the databases screen as a member of a different one
    /// sees it. The real engine sits behind the controller, so a guard that let the request through
    /// would now genuinely start somebody else's container.
    /// </summary>
    private static ForeignFixture ForeignDatabase(
        ServiceStatus status = ServiceStatus.Stopped, IWorkspaceScope? scope = null)
    {
        var owner = Guid.CreateVersion7();
        var intruder = Guid.CreateVersion7();
        var userId = Guid.CreateVersion7();
        var store = "foreign-database-" + Guid.NewGuid();

        var db = new HarboraDbContext(
            new DbContextOptionsBuilder<HarboraDbContext>().UseInMemoryDatabase(store).Options,
            scope ?? new FixedWorkspaceScope(intruder));

        db.Workspaces.Add(new Workspace { Id = owner, Name = "Acme", Slug = "acme" });
        db.Workspaces.Add(new Workspace { Id = intruder, Name = "Other", Slug = "other" });
        db.ManagedServices.Add(new ManagedService
        {
            WorkspaceId = owner,
            ServerId = Guid.CreateVersion7(),
            Name = "orders",
            Type = ManagedServiceType.PostgreSql,
            Version = "16",
            ContainerName = ContainerName,
            VolumeName = ContainerName + "-data",
            Status = status
        });
        db.Users.Add(new User
        {
            Id = userId, Email = "me@example.com", DisplayName = "Tester",
            Role = SystemRole.Owner, ScopedToProjects = false
        });
        db.SaveChanges();

        var serviceId = db.ManagedServices.IgnoreQueryFilters().AsNoTracking().Single().Id;

        var currentUser = new Caller(intruder, userId);
        var protector = new PassthroughProtector();
        var clock = new FixedClock(DateTimeOffset.UnixEpoch);
        var node = new FakeNodeAgentClient(NullLogger<FakeNodeAgentClient>.Instance);
        var docker = new FakeDockerEngine();

        var engine = new ManagedServiceEngine(
            db, new SingleEngineFactory(docker), protector, new NoopJobQueue(),
            new Harbora.Infrastructure.Billing.BillingGate(
                db, Options.Create(new Harbora.Infrastructure.Billing.BillingOptions { Enabled = false })),
            Options.Create(new HarboraRuntimeOptions()), clock,
            NullLogger<ManagedServiceEngine>.Instance);

        var controller = new DatabasesController(
            db,
            engine: engine,
            quota: new AlwaysAllowedQuota(),
            scheduler: new NeverAskedScheduler(),
            protector: protector,
            projects: new Harbora.Infrastructure.Projects.ProjectService(db, clock),
            usage: new ServiceUsageService(db, protector),
            access: new Harbora.Infrastructure.Security.ProjectAccessService(db, currentUser),
            databaseAccess: new DatabaseAccessService(
                db, node, clock, NullLogger<DatabaseAccessService>.Instance),
            adminer: null!,
            audit: new SilentAudit(),
            node: node,
            currentUser: currentUser,
            creationBilling: new Harbora.Infrastructure.Billing.ResourceCreationBilling(
                db, clock, Options.Create(new Harbora.Infrastructure.Billing.BillingOptions { Enabled = false })),
            deploymentEngine: new NeverAskedDeploymentEngine(),
            // Sub-project 10's export/import actions are not exercised by these tenancy tests.
            backupEngine: null!,
            downloadTokens: null!,
            engines: new FakeServerEngineFactory(docker))
        {
            ControllerContext = new ControllerContext { HttpContext = RequestWithServices() }
        };

        return new ForeignFixture(db, controller, serviceId, docker);
    }

    private sealed class Caller(Guid workspaceId, Guid userId) : ICurrentUser
    {
        public Guid? UserId { get; } = userId;
        public string? Email => "me@example.com";
        public bool IsAuthenticated => true;
        public Guid? WorkspaceId { get; } = workspaceId;
    }

    private sealed class AlwaysAllowedQuota : IQuotaService
    {
        public Task<WorkspaceUsage> GetUsageAsync(Guid w, CancellationToken ct) => throw new NotSupportedException();
        public Task<QuotaCheck> CanAddAppAsync(Guid w, string? s, Guid? e, CancellationToken ct) => Task.FromResult(QuotaCheck.Ok);
        public Task<QuotaCheck> CanAddServiceAsync(Guid w, string? size, CancellationToken ct) => Task.FromResult(QuotaCheck.Ok);
    }

    private sealed class NeverAskedScheduler : ISchedulerService
    {
        public Task<PlacementResult> PlaceAsync(long memoryBytes, double cpu, string? pool, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<PlacementResult> CheckAsync(Guid serverId, long memoryBytes, double cpu, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    /// <summary>Every test in this file exercises Start/Stop, never Rotate — the controller still
    /// has to be constructed with something, and this throws if it is ever reached.</summary>
    private sealed class NeverAskedDeploymentEngine : IDeploymentEngine
    {
        public Task<Guid> QueueDeploymentAsync(DeploymentRequest request, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task CancelAsync(Guid deploymentId, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class SilentAudit : IAuditLogger
    {
        public Task LogAsync(string action, string? targetType = null, string? targetId = null,
            string? ipAddress = null, string? actorEmailOverride = null, Guid? userIdOverride = null,
            string? metadataJson = null, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class DiscardingTempData : ITempDataDictionaryFactory
    {
        public ITempDataDictionary GetTempData(HttpContext context) => new TempDataDictionary(context, new Nowhere());

        private sealed class Nowhere : ITempDataProvider
        {
            public IDictionary<string, object?> LoadTempData(HttpContext context) => new Dictionary<string, object?>();
            public void SaveTempData(HttpContext context, IDictionary<string, object?> values) { }
        }
    }

    private sealed class NullUrlHelper : IUrlHelper
    {
        public ActionContext ActionContext { get; } = new();
        public string? Action(UrlActionContext actionContext) => "/";
        public string? Content(string? contentPath) => contentPath;
        public bool IsLocalUrl(string? url) => true;
        public string? Link(string? routeName, object? values) => "/";
        public string? RouteUrl(UrlRouteContext routeContext) => "/";
    }

    private sealed class NullUrlHelperFactory : IUrlHelperFactory
    {
        public IUrlHelper GetUrlHelper(ActionContext context) => new NullUrlHelper();
    }

    private static DefaultHttpContext RequestWithServices() => new()
    {
        RequestServices = new ServiceCollection()
            .AddLogging()
            .AddSingleton<ITempDataDictionaryFactory, DiscardingTempData>()
            .AddSingleton<IUrlHelperFactory, NullUrlHelperFactory>()
            .BuildServiceProvider()
    };
}
