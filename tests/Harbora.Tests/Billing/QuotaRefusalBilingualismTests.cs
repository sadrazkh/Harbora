using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Identity;
using Harbora.Domain.Services;
using Harbora.Infrastructure.Nodes;
using Harbora.Tests.Fakes;
using Harbora.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Harbora.Tests.Billing;

/// <summary>
/// What actually reaches the screen when a customer presses Start or Restart on a workspace the
/// billing gate has refused — not what <see cref="BillingGate"/> computed, which
/// <c>BillingGateTests</c> already covers, but what the controller does with it once a request (and
/// so a culture) is in the room.
///
/// <para>
/// This is the layer <c>QuotaCheck.ReasonFa</c> was added for and the layer commit fa4f5e7 left
/// unfinished: the Fa string existed, travelled as far as an exception message, and was thrown away
/// the moment <c>Controller.Message</c> flattened it to one language. <see cref="QuotaRefusedException"/>
/// is what stops that flattening; these tests are what stop it happening again unnoticed.
/// </para>
/// </summary>
public class QuotaRefusalBilingualismTests
{
    private sealed class Caller(Guid workspaceId, Guid userId) : ICurrentUser
    {
        public Guid? UserId { get; } = userId;
        public string? Email => "me@example.com";
        public bool IsAuthenticated => true;
        public Guid? WorkspaceId { get; } = workspaceId;
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

    /// <summary>
    /// <see cref="ControllerBase.RedirectToAction(string,object)"/> — the shape every action under
    /// test returns to — asks <c>Url</c> for the target link, which asks <see cref="IUrlHelperFactory"/>
    /// off the request. The link itself is never read here; only that generating one does not throw.
    /// </summary>
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

    /// <summary>
    /// A <see cref="QuotaCheck"/> denial that reads unmistakably as itself in either language, so a
    /// test asserting "the Fa string" or "the English string" landed cannot be confused by the two
    /// happening to share a substring.
    /// </summary>
    private static readonly QuotaCheck Denial = QuotaCheck.Deny(
        "English refusal marker.", "نشانه‌ی رد فارسی.");

    /// <summary>Throws the same <see cref="QuotaRefusedException"/> the real services now throw.</summary>
    private sealed class RefusingOperations : IAppOperationsService
    {
        public Task RestartAsync(Guid appId, CancellationToken ct) => throw new QuotaRefusedException(Denial);
        public Task StopAsync(Guid appId, CancellationToken ct) => Task.CompletedTask;
        public Task StartAsync(Guid appId, CancellationToken ct) => throw new QuotaRefusedException(Denial);
        public Task DeleteAsync(Guid appId, bool removeVolumes, CancellationToken ct) => Task.CompletedTask;
        public Task<string> GetLogsAsync(Guid appId, int tail, CancellationToken ct) => Task.FromResult(string.Empty);
        public Task<LogSearchResult> SearchLogsAsync(
            IReadOnlyList<Guid> appIds, string? text, bool problemsOnly, TimeSpan? window, int maxLinesPerApp,
            CancellationToken ct) => Task.FromResult(new LogSearchResult([], []));
        public Task<MaintenanceToggleResult> SetMaintenanceModeAsync(
            Guid appId, bool enabled, string? messageEn, string? messageFa, CancellationToken ct) =>
            Task.FromResult(MaintenanceToggleResult.Ok);
    }

    /// <summary>The one method these tests exercise; everything else is <see cref="FakeManagedServiceEngine"/>'s own refusal.</summary>
    private sealed class RefusingManagedServiceEngine : IManagedServiceEngine
    {
        public IReadOnlyList<ServiceCatalogEntry> Catalog { get; } = [];
        public Task QueueProvisionAsync(Guid serviceId, CancellationToken ct) => throw new NotSupportedException();
        public Task StartAsync(Guid serviceId, CancellationToken ct) => throw new QuotaRefusedException(Denial);
        public Task StopAsync(Guid serviceId, CancellationToken ct) => throw new NotSupportedException();
        public Task RemoveAsync(Guid serviceId, bool deleteData, CancellationToken ct) => throw new NotSupportedException();
        public Task<long?> MeasureStorageAsync(Guid serviceId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<RotatedApp>> RotatePasswordAsync(Guid serviceId, CancellationToken ct) => throw new NotSupportedException();
        public Task<string?> TestConnectionAsync(Guid serviceId, CancellationToken ct) => throw new NotSupportedException();
        public Task<ServiceConnectionInfo> GetConnectionInfoAsync(Guid serviceId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<string, string>> BuildAttachEnvAsync(Guid serviceId, CancellationToken ct) => throw new NotSupportedException();
    }

    /// <summary>Every method a fixture must supply but Start/Restart never reach; each throws if it is.</summary>
    private sealed class Unused :
        IDeploymentEngine, IRollbackPlanner, IDomainInspector, IQuotaService, ISchedulerService, IProxyEngine,
        Harbora.Application.Abstractions.IConfigOverrideResolver
    {
        private static NotSupportedException NotNeeded() =>
            new("This dependency is not reached by Start/Restart and should not have been called.");

        public Task<Guid> QueueDeploymentAsync(DeploymentRequest request, CancellationToken ct) => throw NotNeeded();
        public Task CancelAsync(Guid deploymentId, CancellationToken ct) => throw NotNeeded();
        public Task<RollbackPlan> PrepareAsync(Guid appId, Guid targetDeploymentId, CancellationToken ct) => throw NotNeeded();
        public Task<DomainStatus> InspectAsync(string host, CancellationToken ct) => throw NotNeeded();
        public Task<WorkspaceUsage> GetUsageAsync(Guid workspaceId, CancellationToken ct) => throw NotNeeded();
        public Task<QuotaCheck> CanAddAppAsync(Guid workspaceId, string? instanceSizeKey, Guid? excludeAppId, CancellationToken ct) => throw NotNeeded();
        public Task<QuotaCheck> CanAddServiceAsync(Guid workspaceId, string? instanceSizeKey, CancellationToken ct) => throw NotNeeded();
        public Task<PlacementResult> PlaceAsync(long memoryBytes, double cpu, string? requiredPool, CancellationToken ct) => throw NotNeeded();
        public Task<PlacementResult> CheckAsync(Guid serverId, long memoryBytes, double cpu, CancellationToken ct) => throw NotNeeded();
        public ProxyConfigPreview Preview(IReadOnlyList<Harbora.Domain.Networking.Route> routes) => throw NotNeeded();
        public ProxyValidationResult Validate(IReadOnlyList<Harbora.Domain.Networking.Route> routes) => throw NotNeeded();
        public Task<ProxyApplyResult> ApplyAllAsync(Guid? callerWorkspaceId, CancellationToken ct) => throw NotNeeded();
        public Task ApplyAllAsync(Harbora.Domain.Apps.App app, string containerNameOrId, CancellationToken ct) => throw NotNeeded();
        public Task<Harbora.Application.Abstractions.ConfigOverridePreview> PreviewAsync(
            Harbora.Domain.Apps.App app, Harbora.Domain.Configuration.ConfigOverrideRule rule, string containerNameOrId, CancellationToken ct) =>
            throw NotNeeded();
    }

    /// <summary>
    /// Sub-project E, Task 2: AppsController now takes a <c>BackupSnapshotService</c> for its
    /// "Back up now" action, which Start/Restart never reach — a real service still has to be
    /// constructed (it is a concrete class, not something a controller test can leave null), so its
    /// own sub-dependencies are every one of these, throwing if anything here ever calls them.
    /// </summary>
    private sealed class UnusedBackupDependency :
        Harbora.Modules.Backup.Contracts.IBackupEngineResolver,
        Harbora.Modules.Backup.Infrastructure.IRepositoryCredentialReader,
        Harbora.Modules.Backup.Infrastructure.IBackupTargetResolver,
        Harbora.Modules.Backup.Contracts.IBackupNotificationService
    {
        private static NotSupportedException NotNeeded() =>
            new("This dependency is not reached by Start/Restart and should not have been called.");

        public Harbora.Modules.Backup.Contracts.IBackupEngine Resolve(
            Harbora.Modules.Backup.Contracts.BackupEngineKind kind) => throw NotNeeded();
        public IReadOnlyCollection<Harbora.Modules.Backup.Contracts.BackupEngineKind> Available => throw NotNeeded();

        public Task<string?> GetPasswordAsync(Guid repositoryId, CancellationToken ct) => throw NotNeeded();
        public Task<Harbora.Modules.Backup.Contracts.RepositoryCredentials?> GetCredentialsAsync(
            Guid repositoryId, CancellationToken ct) => throw NotNeeded();

        public Harbora.Modules.Backup.Infrastructure.ResolvedTarget Validate(
            Harbora.Modules.Backup.Contracts.BackupTargetType targetType, string targetRef) => throw NotNeeded();
        public Task<Harbora.Modules.Backup.Infrastructure.TargetLease> AcquireAsync(
            Harbora.Modules.Backup.Contracts.BackupTargetType targetType, string targetRef, Guid snapshotId,
            CancellationToken ct) => throw NotNeeded();

        public Task SendAsync(
            Harbora.Modules.Backup.Contracts.BackupNotification notification, CancellationToken ct) =>
            throw NotNeeded();
    }

    private static (HarboraDbContext Db, AppsController Controller, Guid AppId) BuildAppsFixture()
    {
        var workspaceId = Guid.CreateVersion7();
        var userId = Guid.CreateVersion7();

        var db = new HarboraDbContext(new DbContextOptionsBuilder<HarboraDbContext>()
            .UseInMemoryDatabase("bilingual-refusal-apps-" + Guid.NewGuid()).Options,
            new FixedWorkspaceScope(workspaceId));

        var project = new Harbora.Domain.Projects.Project
        { Id = Guid.CreateVersion7(), WorkspaceId = workspaceId, Name = "Shop", Slug = "shop" };
        var environment = new Harbora.Domain.Projects.Environment
        {
            Id = Guid.CreateVersion7(), WorkspaceId = workspaceId, ProjectId = project.Id,
            Name = "Production", Slug = "production", IsDefault = true
        };
        db.Projects.Add(project);
        db.Environments.Add(environment);

        var app = new App
        {
            Id = Guid.CreateVersion7(), WorkspaceId = workspaceId, EnvironmentId = environment.Id,
            ServerId = Guid.CreateVersion7(), Name = "Shop", Slug = "shop", Status = AppStatus.Stopped
        };
        db.Apps.Add(app);
        db.Users.Add(new User
        {
            Id = userId, Email = "me@example.com", DisplayName = "Tester",
            Role = SystemRole.Owner, ScopedToProjects = false
        });
        db.WorkspaceMembers.Add(new WorkspaceMember
        {
            WorkspaceId = workspaceId, UserId = userId, Role = WorkspaceRole.Admin
        });
        db.SaveChanges();

        var currentUser = new Caller(workspaceId, userId);
        var protector = new PassthroughProtector();
        var clock = new FixedClock();
        var unused = new Unused();

        var controller = new AppsController(
            db,
            deployEngine: unused,
            ops: new RefusingOperations(),
            quota: unused,
            scheduler: unused,
            protector: protector,
            audit: new SilentAudit(),
            rollbackPlanner: unused,
            domains: unused,
            projects: new Harbora.Infrastructure.Projects.ProjectService(db, clock),
            access: new Harbora.Infrastructure.Security.ProjectAccessService(db, currentUser),
            serviceUsage: new Harbora.Infrastructure.Services.ServiceUsageService(db, protector),
            engines: new FakeServerEngineFactory(new FakeDockerEngine()),
            proxy: unused,
            logger: NullLogger<AppsController>.Instance,
            jobs: new NoopJobQueue(),
            config: new ConfigurationBuilder().Build(),
            currentUser: currentUser,
            creationBilling: new Harbora.Infrastructure.Billing.ResourceCreationBilling(
                db, clock, Microsoft.Extensions.Options.Options.Create(
                    new Harbora.Infrastructure.Billing.BillingOptions { Enabled = false })),
            runtimeOptions: Microsoft.Extensions.Options.Options.Create(
                new Harbora.Infrastructure.Deployments.HarboraRuntimeOptions()),
            storageOptions: Microsoft.Extensions.Options.Options.Create(
                new Harbora.Infrastructure.Storage.ObjectStorageOptions()),
            addresses: new Harbora.Infrastructure.Networking.AppAddressAssigner(db, new ConfigurationBuilder().Build()),
            backupSnapshots: new Harbora.Modules.Backup.Infrastructure.BackupSnapshotService(
                db, new UnusedBackupDependency(), new UnusedBackupDependency(), new UnusedBackupDependency(),
                new NoopJobQueue(), new UnusedBackupDependency(), currentUser, new SilentAudit(),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<
                    Harbora.Modules.Backup.Infrastructure.BackupSnapshotService>.Instance),
            lifecycle: new Harbora.Infrastructure.Monitoring.LifecycleHistory(db),
            configOverrides: new Unused())
        {
            ControllerContext = new ControllerContext { HttpContext = RequestWithServices() }
        };

        return (db, controller, app.Id);
    }

    private static (HarboraDbContext Db, DatabasesController Controller, Guid ServiceId) BuildDatabasesFixture()
    {
        var workspaceId = Guid.CreateVersion7();
        var userId = Guid.CreateVersion7();

        var db = new HarboraDbContext(new DbContextOptionsBuilder<HarboraDbContext>()
            .UseInMemoryDatabase("bilingual-refusal-db-" + Guid.NewGuid()).Options,
            new FixedWorkspaceScope(workspaceId));

        var project = new Harbora.Domain.Projects.Project
        { Id = Guid.CreateVersion7(), WorkspaceId = workspaceId, Name = "Shop", Slug = "shop" };
        var environment = new Harbora.Domain.Projects.Environment
        {
            Id = Guid.CreateVersion7(), WorkspaceId = workspaceId, ProjectId = project.Id,
            Name = "Production", Slug = "production", IsDefault = true
        };
        db.Projects.Add(project);
        db.Environments.Add(environment);

        var service = new ManagedService
        {
            Id = Guid.CreateVersion7(), WorkspaceId = workspaceId, EnvironmentId = environment.Id,
            ServerId = Guid.CreateVersion7(),
            Name = "orders", ContainerName = "harbora-svc-orders", DatabaseName = "orders",
            Username = "postgres", Type = ManagedServiceType.PostgreSql, Status = ServiceStatus.Stopped
        };
        db.ManagedServices.Add(service);
        db.Users.Add(new User
        {
            Id = userId, Email = "me@example.com", DisplayName = "Tester",
            Role = SystemRole.Owner, ScopedToProjects = false
        });
        db.WorkspaceMembers.Add(new WorkspaceMember
        {
            WorkspaceId = workspaceId, UserId = userId, Role = WorkspaceRole.Admin
        });
        db.SaveChanges();

        var currentUser = new Caller(workspaceId, userId);
        var protector = new PassthroughProtector();
        var clock = new FixedClock();
        var node = new FakeNodeAgentClient(NullLogger<FakeNodeAgentClient>.Instance);
        var unused = new Unused();

        var controller = new DatabasesController(
            db,
            engine: new RefusingManagedServiceEngine(),
            quota: new AlwaysAllowedQuota(),
            scheduler: new NeverAskedScheduler(),
            protector: protector,
            projects: new Harbora.Infrastructure.Projects.ProjectService(db, clock),
            usage: new Harbora.Infrastructure.Services.ServiceUsageService(db, protector),
            access: new Harbora.Infrastructure.Security.ProjectAccessService(db, currentUser),
            databaseAccess: new Harbora.Infrastructure.Services.DatabaseAccessService(
                db, node, clock, NullLogger<Harbora.Infrastructure.Services.DatabaseAccessService>.Instance),
            adminer: null!,
            audit: new SilentAudit(),
            node: node,
            currentUser: currentUser,
            creationBilling: new Harbora.Infrastructure.Billing.ResourceCreationBilling(
                db, clock, Microsoft.Extensions.Options.Options.Create(
                    new Harbora.Infrastructure.Billing.BillingOptions { Enabled = false })),
            deploymentEngine: unused,
            // Sub-project 10's export/import actions are not exercised by these quota-refusal tests.
            backupEngine: null!,
            downloadTokens: null!)
        {
            ControllerContext = new ControllerContext { HttpContext = RequestWithServices() }
        };

        return (db, controller, service.Id);
    }

    private sealed class NeverAskedScheduler : ISchedulerService
    {
        public Task<PlacementResult> PlaceAsync(long memoryBytes, double cpu, string? pool, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<PlacementResult> CheckAsync(Guid serverId, long memoryBytes, double cpu, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class AlwaysAllowedQuota : IQuotaService
    {
        public Task<WorkspaceUsage> GetUsageAsync(Guid w, CancellationToken ct) => throw new NotSupportedException();
        public Task<QuotaCheck> CanAddAppAsync(Guid w, string? s, Guid? e, CancellationToken ct) => Task.FromResult(QuotaCheck.Ok);
        public Task<QuotaCheck> CanAddServiceAsync(Guid w, string? size, CancellationToken ct) => Task.FromResult(QuotaCheck.Ok);
    }

    private sealed class SilentAudit : IAuditLogger
    {
        public Task LogAsync(string action, string? targetType = null, string? targetId = null,
            string? ipAddress = null, string? actorEmailOverride = null, Guid? userIdOverride = null,
            string? metadataJson = null, CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>
    /// Runs <paramref name="body"/> under a chosen UI culture and restores whatever was there before,
    /// so one test's Persian does not leak into the next test picked up by the same pooled thread.
    /// </summary>
    private static async Task UnderCultureAsync(string twoLetterIso, Func<Task> body)
    {
        var previous = System.Globalization.CultureInfo.CurrentUICulture;
        System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo(twoLetterIso);
        try { await body(); }
        finally { System.Globalization.CultureInfo.CurrentUICulture = previous; }
    }

    [Fact]
    public async Task Starting_a_refused_app_shows_the_Persian_reason_when_the_request_is_Persian()
    {
        var (_, controller, appId) = BuildAppsFixture();

        await UnderCultureAsync("fa", () => controller.Start(appId, default));

        controller.TempData["Error"].Should().Be(Denial.ReasonFa,
            "a customer reading Persian must not be shown the English half of a refusal that has one");
    }

    [Fact]
    public async Task Starting_a_refused_app_shows_the_English_reason_when_the_request_is_not_Persian()
    {
        var (_, controller, appId) = BuildAppsFixture();

        await UnderCultureAsync("en", () => controller.Start(appId, default));

        controller.TempData["Error"].Should().Be(Denial.Reason,
            "an English request must not be shown the Persian half instead");
    }

    [Fact]
    public async Task Restarting_a_refused_app_shows_the_Persian_reason_when_the_request_is_Persian()
    {
        var (_, controller, appId) = BuildAppsFixture();

        await UnderCultureAsync("fa", () => controller.Restart(appId, default));

        controller.TempData["Error"].Should().Be(Denial.ReasonFa,
            "Restart is the second of the two request-scoped buttons this refusal reaches — Start " +
            "alone being fixed would leave this one still English-only");
    }

    [Fact]
    public async Task Starting_a_refused_database_shows_the_Persian_reason_when_the_request_is_Persian()
    {
        var (_, controller, serviceId) = BuildDatabasesFixture();

        await UnderCultureAsync("fa", () => controller.Start(serviceId, default));

        controller.TempData["Error"].Should().Be(Denial.ReasonFa,
            "ManagedServiceEngine.StartAsync is the other request-scoped throw site — a customer " +
            "starting a database by hand gets the same treatment as one starting an app");
    }

    [Fact]
    public async Task Starting_a_refused_database_shows_the_English_reason_when_the_request_is_not_Persian()
    {
        var (_, controller, serviceId) = BuildDatabasesFixture();

        await UnderCultureAsync("en", () => controller.Start(serviceId, default));

        controller.TempData["Error"].Should().Be(Denial.Reason);
    }
}

/// <summary>
/// The structural backstop for the rule above: every place in <c>src/</c> that asks
/// <see cref="IBillingGate"/> has either wired the refusal through <see cref="QuotaRefusedException"/>
/// — so a request-scoped catch can still choose a language — or is named here with the reason, in
/// writing, that its refusal stays English.
///
/// <para>
/// The callers are found the same way <c>Billing.StartPathCensusTests</c> finds every container
/// starter: by reading the source rather than trusting a hand-kept list, so a fifth call site nobody
/// remembered to add here fails this test by naming itself, instead of shipping an English-only
/// refusal that nothing catches.
/// </para>
/// </summary>
public class QuotaRefusalReachabilityTests
{
    /// <summary>
    /// A file that reads a denied <see cref="QuotaCheck"/>'s English half and keeps it that way on
    /// purpose, with why. Each entry's reasoning lives at the call site too — this is the index, not
    /// the argument.
    /// </summary>
    private static readonly Dictionary<string, string> PersistedEnglishByDesign = new()
    {
        ["src/Harbora.Infrastructure/Deployments/DeploymentPipeline.cs"] =
            "deployment.ErrorMessage and the deploy log sit beside git checkout, Docker build and " +
            "health-check output that is never translated — a protected LTR island per " +
            "docs/product-audit/19-do-not-change-list.md item 21 — and the queue is durable, so " +
            "whoever reads it later need not be whoever the balance ran out on",
        ["src/Harbora.Infrastructure/Deployments/CronJobRunner.cs"] =
            "CronRun.Error sits beside Output and ExitCode, the job's own untranslated stdout/stderr, " +
            "and is reachable from a schedule with nobody's request behind it at all",
        ["src/Harbora.Infrastructure/Services/ManagedServiceEngine.cs"] =
            "also files a request-scoped throw (StartAsync) below, but ProvisionAsync's refusal has " +
            "nowhere bilingual to go: ManagedService stores no reason field, so mayStart.Reason there " +
            "reaches only the operator log, English by the same convention as every other log line",
    };

    [Fact]
    public void Every_caller_of_the_billing_gate_either_throws_QuotaRefusedException_or_has_filed_why_its_refusal_stays_English()
    {
        foreach (var path in BillingGateCallers())
        {
            var text = File.ReadAllText(Path.Combine(RepoRoot, path));

            // The actual throw, not just the type name anywhere in the file — a comment that only
            // mentions QuotaRefusedException (this one, for instance) must not be mistaken for code
            // that raises it. Proved by temporarily reverting AppOperationsService.cs's throw to
            // `new InvalidOperationException(mayStart.Reason)` while leaving its explanatory comment
            // untouched: matching on the bare type name left this test green; matching on the throw
            // itself, as below, failed it (and BillingGateEnforcementTests.RefusalFrom failed
            // independently, on the exception's runtime type).
            var carriesBothLanguagesForward = text.Contains("throw new QuotaRefusedException(");
            var filedAsEnglishOnPurpose = PersistedEnglishByDesign.ContainsKey(path);

            (carriesBothLanguagesForward || filedAsEnglishOnPurpose).Should().BeTrue(
                $"{path} reads IBillingGate's answer and does neither: it does not throw " +
                "QuotaRefusedException for a request-scoped caller to pick a language with, and it " +
                "is not filed above with a written reason its refusal stays English. A Persian " +
                "sentence QuotaCheck built and nobody reads is exactly the gap fa4f5e7 opened and " +
                "this suite exists to keep closed.");
        }
    }

    [Fact]
    public void Nothing_is_filed_as_English_by_design_for_a_file_that_no_longer_asks_the_gate()
    {
        var callers = BillingGateCallers();
        var stale = PersistedEnglishByDesign.Keys.Where(p => !callers.Contains(p)).ToList();

        stale.Should().BeEmpty(
            "these are written down as a deliberate English-only decision about code that no longer " +
            "calls CanStartAsync — a decision about nothing is not a decision, and a reader has no " +
            "way to tell it apart from one that still applies. Found: " + string.Join(", ", stale));
    }

    /// <summary>Every file under src/ that asks the one billing gate whether a workload may start.</summary>
    private static List<string> BillingGateCallers() =>
        Directory.EnumerateFiles(Path.Combine(RepoRoot, "src"), "*.cs", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(RepoRoot, f).Replace('\\', '/'))
            .Where(p => !p.Contains("/bin/") && !p.Contains("/obj/") && !p.Contains("/Migrations/"))
            .Where(p => File.ReadAllText(Path.Combine(RepoRoot, p)).Contains(".CanStartAsync("))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

    private static string RepoRoot { get; } = FindRoot();

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Harbora.slnx"))) return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate Harbora.slnx from the test output directory.");
    }
}
