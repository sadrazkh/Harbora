using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Authorization;
using Harbora.Domain.Common;
using Harbora.Domain.Identity;
using Harbora.Domain.Servers;
using Harbora.Domain.Settings;
using Harbora.Domain.Tenancy;
using Harbora.Infrastructure.Apps;
using Harbora.Infrastructure.Billing;
using Harbora.Infrastructure.Networking;
using Harbora.Infrastructure.Projects;
using Harbora.Infrastructure.Security;
using Harbora.Infrastructure.Tenancy;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Environment = Harbora.Domain.Projects.Environment;
using Project = Harbora.Domain.Projects.Project;

namespace Harbora.Tests;

/// <summary>
/// One workspace, one project with one environment, a signed-in member, and the two app services wired
/// over an EF InMemory context that applies this workspace's tenant filter — the same scoping a panel
/// request has. Everything the services reach outside the database is a recording double, so a test can
/// say what was audited, scheduled and queued, and that nothing was when nothing should have been.
/// </summary>
internal sealed class AppServiceHarness : IDisposable
{
    public static readonly DateTimeOffset Now = new(2026, 10, 11, 9, 0, 0, TimeSpan.Zero);
    public const string RootDomain = "apps.example.com";
    public const string PanelDomain = "panel.example.com";

    private readonly string _store = "app-services-" + Guid.NewGuid();

    public HarboraDbContext Db { get; }
    public Guid WorkspaceId { get; } = Guid.CreateVersion7();
    public Guid UserId { get; } = Guid.CreateVersion7();
    public Plan Plan { get; } = new() { Name = "Team", IsEnabled = true };
    public Project Project { get; }
    public Environment? Environment { get; }
    public Server Server { get; } = new() { Name = "local", Hostname = "localhost", IsLocal = true, Status = ServerStatus.Online };

    public RecordingAudit Audit { get; } = new();
    public StubScheduler Scheduler { get; } = new();
    public RecordingDeployments Deployments { get; } = new();
    public FakeDockerEngine Docker { get; } = new();
    public BillingOptions Billing { get; } = new() { Enabled = false };
    public FixedClock Clock { get; } = new(Now);

    /// <param name="role">The member's workspace role.</param>
    /// <param name="scoped">Scoped to projects, with no grant at all unless the test adds one.</param>
    /// <param name="withEnvironment">False for a workspace that has no project or environment yet.</param>
    public AppServiceHarness(WorkspaceRole role = WorkspaceRole.Admin, bool scoped = false, bool withEnvironment = true)
    {
        Db = new HarboraDbContext(new DbContextOptionsBuilder<HarboraDbContext>()
            .UseInMemoryDatabase(_store).Options,
            new FixedWorkspaceScope(WorkspaceId));

        Project = new Project { WorkspaceId = WorkspaceId, Name = "Shop", Slug = "shop" };
        Db.Plans.Add(Plan);
        Db.Workspaces.Add(new Workspace { Id = WorkspaceId, Name = "Acme", Slug = "acme", PlanId = Plan.Id });
        Db.Users.Add(new User { Id = UserId, Email = "member@example.com", DisplayName = "Member" });
        Db.WorkspaceMembers.Add(new WorkspaceMember
        {
            WorkspaceId = WorkspaceId, UserId = UserId, Role = role, ScopedToProjects = scoped
        });
        Db.Servers.Add(Server);
        Db.Settings.Add(new Setting { Key = SettingKeys.PlatformRootDomain, Value = RootDomain });

        if (withEnvironment)
        {
            Environment = new Environment
            {
                WorkspaceId = WorkspaceId, ProjectId = Project.Id, Name = "Production", Slug = "production", IsDefault = true
            };
            Db.Projects.Add(Project);
            Db.Environments.Add(Environment);
        }

        Db.SaveChanges();
        Scheduler.Result = PlacementResult.Placed(Server.Id);
    }

    public void Dispose() => Db.Dispose();

    public AppActor Actor => new(WorkspaceId, "203.0.113.9");

    /// <summary>
    /// Runs once, the next time either service takes the workspace's creation lock, and is then
    /// cleared — standing in for another request that commits while this one waits for the lock. A
    /// check made before the lock cannot see what this writes; a check made inside it must.
    /// </summary>
    public Action<HarboraDbContext>? OnCreationLock { get; set; }

    /// <summary>A second context over the same rows and workspace: another request, as far as EF knows.</summary>
    public HarboraDbContext OtherRequest() => new(new DbContextOptionsBuilder<HarboraDbContext>()
        .UseInMemoryDatabase(_store).Options, new FixedWorkspaceScope(WorkspaceId));

    private IQuotaService Quota() => new LockHookQuota(new QuotaService(Db, Options.Create(Billing)), () =>
    {
        var hook = OnCreationLock;
        OnCreationLock = null;
        if (hook is null) return;
        using var other = OtherRequest();
        hook(other);
        other.SaveChanges();
    });

    private IConfiguration Config() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["PANEL_DOMAIN"] = PanelDomain })
        .Build();

    private ProjectAccessService Access() => new(Db, new Caller(WorkspaceId, UserId));

    private AppAddressAssigner Addresses() => new(Db, Config());

    public AppProvisioningService Provisioning() => new(
        Db, Quota(), Scheduler, new PassthroughProtector(), Audit, Deployments,
        new ProjectService(Db, Clock, new QuotaService(Db, Options.Create(Billing))), Access(), Addresses(),
        new ResourceCreationBilling(Db, Clock, Options.Create(Billing)));

    public AppConfigurationService Configuration() => new(
        Db, Quota(), new PassthroughProtector(), Audit, Access(), Addresses(),
        new FakeServerEngineFactory(Docker), Config(), NullLogger<AppConfigurationService>.Instance);

    /// <summary>A prebuilt-image app request that clears every check in this harness as it stands.</summary>
    public AppCreateRequest ImageApp(string name = "Shop API", string? slug = null) => new()
    {
        WorkspaceId = WorkspaceId,
        UserId = UserId,
        ClientIp = "203.0.113.9",
        Name = name,
        Slug = slug,
        SourceType = AppSourceType.PrebuiltImage,
        PrebuiltImage = "ghcr.io/example/api:1.0",
        EnvironmentId = Environment?.Id,
        DeployNow = false
    };

    /// <summary>
    /// Every table either service writes to, counted across all tenants, plus what the context is
    /// tracking as added, changed or deleted — "changes nothing" asserted as a whole, not table by table.
    /// </summary>
    public string Snapshot()
    {
        Db.ChangeTracker.DetectChanges();
        var pending = Db.ChangeTracker.Entries().Count(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted);
        return string.Join(", ",
            $"apps={Db.Apps.IgnoreQueryFilters().Count()}",
            $"domains={Db.Domains.IgnoreQueryFilters().Count()}",
            $"volumes={Db.Volumes.IgnoreQueryFilters().Count()}",
            $"env={Db.EnvironmentVariables.IgnoreQueryFilters().Count()}",
            $"gitProviders={Db.GitProviders.IgnoreQueryFilters().Count()}",
            $"gitRepositories={Db.GitRepositories.IgnoreQueryFilters().Count()}",
            $"projects={Db.Projects.IgnoreQueryFilters().Count()}",
            $"environments={Db.Environments.IgnoreQueryFilters().Count()}",
            $"ledger={Db.BillingLedger.IgnoreQueryFilters().Count()}",
            $"deployments={Db.Deployments.IgnoreQueryFilters().Count()}",
            $"routes={Db.Routes.IgnoreQueryFilters().Count()}",
            $"audit={Db.AuditLogs.IgnoreQueryFilters().Count()}",
            $"pending={pending}");
    }

    /// <summary>Gives a scoped member a grant on the harness project.</summary>
    public void Grant(SystemRole role)
    {
        Db.ProjectGrants.Add(new ProjectGrant { WorkspaceId = WorkspaceId, UserId = UserId, ProjectId = Project.Id, Role = role });
        Db.SaveChanges();
    }

    public sealed class Caller(Guid workspaceId, Guid userId) : ICurrentUser
    {
        public Guid? UserId { get; } = userId;
        public string? Email => "member@example.com";
        public bool IsAuthenticated => true;
        public Guid? WorkspaceId { get; } = workspaceId;
    }

    public sealed class RecordingAudit : IAuditLogger
    {
        public sealed record Entry(string Action, string? TargetType, string? TargetId, string? IpAddress, string? MetadataJson, Guid? WorkspaceId);

        public List<Entry> Entries { get; } = [];

        public Task LogAsync(string action, string? targetType = null, string? targetId = null, string? ipAddress = null,
            string? actorEmailOverride = null, Guid? userIdOverride = null, string? metadataJson = null,
            Guid? workspaceId = null, CancellationToken ct = default)
        {
            Entries.Add(new Entry(action, targetType, targetId, ipAddress, metadataJson, workspaceId));
            return Task.CompletedTask;
        }
    }

    /// <summary>The real quota service, with a hook just after the creation lock is taken.</summary>
    public sealed class LockHookQuota(IQuotaService inner, Action afterLock) : IQuotaService
    {
        public async Task<IQuotaReservation> AcquireCreationLockAsync(Guid workspaceId, CancellationToken ct)
        {
            var reservation = await inner.AcquireCreationLockAsync(workspaceId, ct);
            afterLock();
            return reservation;
        }

        public Task<WorkspaceUsage> GetUsageAsync(Guid workspaceId, CancellationToken ct) => inner.GetUsageAsync(workspaceId, ct);
        public Task<QuotaCheck> CanAddAppAsync(Guid workspaceId, string? instanceSizeKey, Guid? excludeAppId, CancellationToken ct) =>
            inner.CanAddAppAsync(workspaceId, instanceSizeKey, excludeAppId, ct);
        public Task<QuotaCheck> CanAddServiceAsync(Guid workspaceId, string? instanceSizeKey, CancellationToken ct) =>
            inner.CanAddServiceAsync(workspaceId, instanceSizeKey, ct);
        public Task<QuotaCheck> CanAddGovernedResourcesAsync(Guid workspaceId, GovernanceQuotaDelta delta, CancellationToken ct) =>
            inner.CanAddGovernedResourcesAsync(workspaceId, delta, ct);
        public Task<QuotaCheck> CanAddWorkloadsAsync(Guid workspaceId, WorkloadQuotaDelta delta, CancellationToken ct) =>
            inner.CanAddWorkloadsAsync(workspaceId, delta, ct);
        public Task<QuotaCheck> CanQueueDeploymentAsync(Guid workspaceId, CancellationToken ct) =>
            inner.CanQueueDeploymentAsync(workspaceId, ct);
        public Task<QuotaCheck> CanUseBackupRetentionAsync(Guid workspaceId, int retentionCount, CancellationToken ct) =>
            inner.CanUseBackupRetentionAsync(workspaceId, retentionCount, ct);
    }

    public sealed class StubScheduler : ISchedulerService
    {
        public PlacementResult Result { get; set; } = PlacementResult.Fail("unset");

        public Task<PlacementResult> PlaceAsync(long memoryBytes, double cpu, string? requiredPool, CancellationToken ct) =>
            Task.FromResult(Result);

        public Task<PlacementResult> CheckAsync(Guid serverId, long memoryBytes, double cpu, CancellationToken ct) =>
            Task.FromResult(Result);
    }

    public sealed class RecordingDeployments : IDeploymentEngine
    {
        public List<DeploymentRequest> Queued { get; } = [];
        public Exception? Refuse { get; set; }
        public Guid NextId { get; } = Guid.CreateVersion7();

        public Task<Guid> QueueDeploymentAsync(DeploymentRequest request, CancellationToken ct)
        {
            if (Refuse is not null) throw Refuse;
            Queued.Add(request);
            return Task.FromResult(NextId);
        }

        public Task CancelAsync(Guid deploymentId, CancellationToken ct) => throw new NotSupportedException();
        public Task ApproveAsync(Guid deploymentId, Guid approverUserId, CancellationToken ct) => throw new NotSupportedException();
        public Task RejectAsync(Guid deploymentId, Guid approverUserId, string reason, CancellationToken ct) => throw new NotSupportedException();
    }
}
