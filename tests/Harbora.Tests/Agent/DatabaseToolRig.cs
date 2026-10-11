using System.Text.Json;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Services;
using Harbora.Domain.Tenancy;
using Harbora.Infrastructure.Agent;
using Harbora.Infrastructure.Agent.Tools;
using Harbora.Infrastructure.Billing;
using Harbora.Infrastructure.Projects;
using Harbora.Infrastructure.Security;
using Harbora.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Project = Harbora.Domain.Projects.Project;
using ProjectEnvironment = Harbora.Domain.Projects.Environment;

namespace Harbora.Tests.Agent;

/// <summary>
/// The ten project and database tools (round six, wave B, agent G) over the <b>real</b> phase-0 services —
/// provisioning, attachment, <see cref="ProjectService"/>, <see cref="ProjectDeletionService"/>, the usage
/// reader — inside an <see cref="AgentHarness"/>, so the same rig serves a tool called directly and a tool
/// driven by the agent loop. Only what reaches a container is faked: the managed-service engine, the app
/// operations, the scheduler, the quota and the billing gate, each with answers a test sets.
/// </summary>
internal sealed class DatabaseToolRig : IDisposable
{
    public AgentHarness H { get; }
    public HarboraDbContext Db => H.Db;
    public ToolEngine Engine { get; }
    public ToolQuota Quota { get; } = new();
    public ToolScheduler Scheduler { get; } = new();
    public DeletingAppOps AppOps { get; }

    /// <summary>One options object every tool and service reads, so a test can switch billing on mid-way.</summary>
    public BillingOptions Billing { get; } = new() { Enabled = false };

    public ICurrentUser Caller { get; }

    public DatabaseToolRig(SystemRole role = SystemRole.Owner, bool scopedToProjects = false)
        : this(new AgentHarness(role: role, scopedToProjects: scopedToProjects))
    {
    }

    public DatabaseToolRig(AgentHarness harness)
    {
        H = harness;
        Caller = new Person(H.UserId, H.WorkspaceId);
        Engine = new ToolEngine(H.Db, H.Protector);
        AppOps = new DeletingAppOps(H.Db);
        H.Tools.AddRange(All());
    }

    public void Dispose() => H.Dispose();

    // ---- the real services, over this rig's fakes ----

    public ProjectAccessService Access => new(Db, Caller);
    public AgentResourceResolver Resolver => new(Db, Access);
    public ProjectService Projects => new(Db, H.Clock, Quota, H.Audit);
    public ServiceUsageService Usage => new(Db, H.Protector);

    public DatabaseProvisioningService Provisioning => new(
        Db, Engine, Quota, Scheduler, H.Protector, Projects, Access,
        new ResourceCreationBilling(Db, H.Clock, Options.Create(Billing)), H.Audit, Caller);

    public DatabaseAttachmentService Attachments => new(Db, Access, H.Audit, Caller);

    // ---- the tools ----

    public CreateProjectTool CreateProject() => new(Db, Projects, Access, Quota);
    public AddEnvironmentTool AddEnvironment() => new(Db, Projects, Access, Quota, Resolver);

    public CreateDatabaseTool CreateDatabase(IDatabaseProvisioningService? provisioning = null) =>
        new(Db, Engine, provisioning ?? Provisioning, Access, Resolver, Options.Create(Billing));

    public AttachDatabaseTool Attach() => new(Db, Attachments, Access, Resolver);
    public DetachDatabaseTool Detach() => new(Db, Attachments, Resolver);
    public RotateDatabasePasswordTool Rotate() => new(Db, Engine, Access, Resolver, Usage, H.Protector, NullLogger<RotateDatabasePasswordTool>.Instance);
    public StopDatabaseTool Stop(TimeSpan? timeout = null) => new(Db, Engine, Access, Resolver, Usage) { EngineTimeout = timeout ?? TimeSpan.FromMinutes(2) };
    public StartDatabaseTool Start(TimeSpan? timeout = null) => new(Db, Engine, Access, Resolver, Options.Create(Billing)) { EngineTimeout = timeout ?? TimeSpan.FromMinutes(2) };
    public DeleteDatabaseTool DeleteDatabase() => new(Provisioning, Resolver, Usage, Access);

    public DeleteProjectTool DeleteProject() => new(
        new ProjectDeletionService(Db, AppOps, Engine, NullLogger<ProjectDeletionService>.Instance), Access, Resolver, H.Audit);

    public IReadOnlyList<IAgentTool> All() =>
    [
        CreateProject(), AddEnvironment(), CreateDatabase(), Attach(), Detach(),
        Rotate(), Stop(), Start(), DeleteDatabase(), DeleteProject()
    ];

    // ---- calling a tool directly ----

    public AgentToolContext Context => new(H.WorkspaceId, H.UserId, H.Principal, Guid.Empty, null);

    public static JsonElement Args(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    public Task<AgentValidation> ValidateAsync(IAgentTool tool, string json, AgentPlanScope? scope = null) =>
        tool.ValidateAsync(Args(json), Context, scope ?? new AgentPlanScope(), CancellationToken.None);

    public Task<AgentToolResult> ExecuteAsync(IAgentTool tool, string json) =>
        tool.ExecuteAsync(Args(json), Context, CancellationToken.None);

    /// <summary>Tracked changes nobody has saved — what a validation must never leave behind.</summary>
    public int PendingChanges => Db.ChangeTracker.Entries()
        .Count(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted);

    public RowCounts Counts() => new(
        Db.Projects.IgnoreQueryFilters().Count(), Db.Environments.IgnoreQueryFilters().Count(),
        Db.ManagedServices.IgnoreQueryFilters().Count(), Db.ManagedServiceDatabases.IgnoreQueryFilters().Count(),
        Db.AppManagedServices.IgnoreQueryFilters().Count(), Db.Apps.IgnoreQueryFilters().Count(),
        H.Audit.Entries.Count, Engine.Provisioned.Count, Engine.Removed.Count, Engine.Started.Count,
        Engine.Stopped.Count, Engine.Rotated.Count, Quota.LocksTaken);

    internal readonly record struct RowCounts(
        int Projects, int Environments, int Services, int LogicalDatabases, int Attachments, int Apps,
        int Audits, int Provisions, int Removals, int Starts, int Stops, int Rotations, int Locks);

    // ---- seeding ----

    public string ProjectSlug => H.ProjectSlug;

    public ManagedService SeedDatabase(
        string name, Guid? environmentId = null, ManagedServiceType type = ManagedServiceType.PostgreSql,
        ServiceStatus status = ServiceStatus.Running, Guid? primaryId = null)
    {
        var service = new ManagedService
        {
            WorkspaceId = H.WorkspaceId, EnvironmentId = environmentId ?? H.EnvironmentId, ServerId = Guid.CreateVersion7(),
            Name = name, Type = type, Version = type == ManagedServiceType.PostgreSql ? "16" : "7", Status = status,
            ContainerName = DatabaseProvisioningService.ContainerPrefix + name,
            VolumeName = DatabaseProvisioningService.ContainerPrefix + name + "-data",
            InternalPort = 5432, Username = "harbora", DatabaseName = name.Replace('-', '_'),
            EncryptedPassword = H.Protector.Protect("OldPassw0rdFor" + name.Replace("-", "")),
            PrimaryManagedServiceId = primaryId
        };
        Db.ManagedServices.Add(service);
        if (ManagedServiceDatabase.DefaultFor(service) is { } logical) Db.ManagedServiceDatabases.Add(logical);
        Db.SaveChanges();
        return service;
    }

    public App SeedApp(string slug, Guid? environmentId = null)
    {
        var app = new App
        {
            WorkspaceId = H.WorkspaceId, EnvironmentId = environmentId ?? H.EnvironmentId, ServerId = Guid.CreateVersion7(),
            Name = slug, Slug = slug, SourceType = AppSourceType.PrebuiltImage, PrebuiltImage = "nginx:1.27",
            Status = AppStatus.Running
        };
        Db.Apps.Add(app);
        Db.SaveChanges();
        return app;
    }

    public AppManagedService AttachRow(App app, ManagedService service, string? alias = null)
    {
        var row = new AppManagedService
        {
            AppId = app.Id, ManagedServiceId = service.Id,
            ManagedServiceDatabaseId = Db.ManagedServiceDatabases.Where(d => d.ManagedServiceId == service.Id && d.IsDefault)
                .Select(d => (Guid?)d.Id).FirstOrDefault(),
            Alias = alias ?? AppManagedServiceAlias.Resolve(null, service.Name, []), AttachOrder = 1
        };
        Db.AppManagedServices.Add(row);
        Db.SaveChanges();
        return row;
    }

    public (Project Project, ProjectEnvironment Production) SeedProject(string slug, string? name = null)
    {
        var project = new Project { WorkspaceId = H.WorkspaceId, Name = name ?? slug, Slug = slug };
        var production = new ProjectEnvironment
        {
            WorkspaceId = H.WorkspaceId, ProjectId = project.Id, Name = "Production", Slug = "production", IsDefault = true
        };
        Db.Projects.Add(project);
        Db.Environments.Add(production);
        Db.SaveChanges();
        return (project, production);
    }

    public ProjectEnvironment SeedEnvironment(Guid projectId, string slug)
    {
        var environment = new ProjectEnvironment { WorkspaceId = H.WorkspaceId, ProjectId = projectId, Name = slug, Slug = slug };
        Db.Environments.Add(environment);
        Db.SaveChanges();
        return environment;
    }

    public InstanceSize SeedSize(string key, bool enabled = true)
    {
        var size = new InstanceSize
        {
            Key = key, Name = key, NameFa = key, CpuCores = 1, MemoryBytes = 1024L * 1024 * 1024,
            DiskBytes = 10L * 1024 * 1024 * 1024, IsEnabled = enabled
        };
        Db.InstanceSizes.Add(size);
        Db.SaveChanges();
        return size;
    }

    /// <summary>A scope in which an earlier step creates these.</summary>
    public static AgentPlanScope Planning(params AgentPlannedResource[] planned)
    {
        var scope = new AgentPlanScope();
        foreach (var resource in planned) scope.Add(resource);
        return scope;
    }

    private sealed class Person(Guid userId, Guid workspaceId) : ICurrentUser
    {
        public Guid? UserId => userId;
        public string? Email => "person@example.com";
        public bool IsAuthenticated => true;
        public Guid? WorkspaceId => workspaceId;
    }
}

/// <summary>
/// The managed-service engine, minus Docker: records what it was asked, and writes the row the way the real
/// engine does when the container part succeeds — or refuses, hangs or quietly does nothing when told to.
/// </summary>
internal sealed class ToolEngine(HarboraDbContext db, ISecretProtector protector) : IManagedServiceEngine
{
    public const string NewPassword = "Fr3shlyGeneratedPassw0rd";

    public IReadOnlyList<ServiceCatalogEntry> Catalog { get; } =
    [
        new(ManagedServiceType.PostgreSql, "PostgreSQL", "پستگرس", "postgres:16-alpine", ["16", "15"], 5432, true),
        new(ManagedServiceType.Redis, "Redis", "ردیس", "redis:7-alpine", ["7"], 6379, false),
        new(ManagedServiceType.MongoDb, "MongoDB", "مونگو", "mongo:7", ["7"], 27017, true)
    ];

    public List<Guid> Provisioned { get; } = [];
    public List<(Guid Id, bool DeleteData)> Removed { get; } = [];
    public List<Guid> Started { get; } = [];
    public List<Guid> Stopped { get; } = [];
    public List<Guid> Rotated { get; } = [];

    public QuotaCheck? StartRefusal { get; set; }
    public bool Hang { get; set; }
    public string? RotateRefusal { get; set; }
    public bool RotateDoesNothing { get; set; }
    public Exception? RotateThrowsBeforeStoring { get; set; }
    public Exception? RotateThrowsAfterStoring { get; set; }
    public List<RotatedApp> Rewrites { get; } = [];

    public Task QueueProvisionAsync(Guid serviceId, CancellationToken ct)
    {
        Provisioned.Add(serviceId);
        return Task.CompletedTask;
    }

    public async Task StartAsync(Guid serviceId, CancellationToken ct)
    {
        if (Hang) await Task.Delay(Timeout.Infinite, ct);
        if (StartRefusal is { } refusal) throw new QuotaRefusedException(refusal);
        var service = await db.ManagedServices.IgnoreQueryFilters().FirstAsync(s => s.Id == serviceId, ct);
        service.Status = ServiceStatus.Running;
        await db.SaveChangesAsync(ct);
        Started.Add(serviceId);
    }

    public async Task StopAsync(Guid serviceId, CancellationToken ct)
    {
        if (Hang) await Task.Delay(Timeout.Infinite, ct);
        var service = await db.ManagedServices.IgnoreQueryFilters().FirstAsync(s => s.Id == serviceId, ct);
        service.Status = ServiceStatus.Stopped;
        await db.SaveChangesAsync(ct);
        Stopped.Add(serviceId);
    }

    public async Task RemoveAsync(Guid serviceId, bool deleteData, CancellationToken ct)
    {
        Removed.Add((serviceId, deleteData));
        var service = await db.ManagedServices.FirstAsync(s => s.Id == serviceId, ct);
        db.ManagedServices.Remove(service);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<RotatedApp>> RotatePasswordAsync(Guid serviceId, CancellationToken ct)
    {
        Rotated.Add(serviceId);
        if (RotateRefusal is { } refusal) throw new InvalidOperationException(refusal);
        if (RotateThrowsBeforeStoring is { } before) throw before;
        if (RotateDoesNothing) return [];
        var service = await db.ManagedServices.FirstAsync(s => s.Id == serviceId, ct);
        service.EncryptedPassword = protector.Protect(NewPassword);
        await db.SaveChangesAsync(ct);
        if (RotateThrowsAfterStoring is { } after) throw after;
        return Rewrites.ToList();
    }

    public Task<long?> MeasureStorageAsync(Guid serviceId, CancellationToken ct) => throw new NotSupportedException();
    public Task<string?> TestConnectionAsync(Guid serviceId, CancellationToken ct) => throw new NotSupportedException();
    public Task<ServiceConnectionInfo> GetConnectionInfoAsync(Guid serviceId, CancellationToken ct) => throw new NotSupportedException();
    public Task<IReadOnlyDictionary<string, string>> BuildAttachEnvAsync(Guid serviceId, CancellationToken ct) => throw new NotSupportedException();
    public Task<RedisMemoryPolicyOutcome> UpdateRedisMemoryPolicyAsync(Guid serviceId, string? policy, long maxMemoryBytes, CancellationToken ct) => throw new NotSupportedException();
    public Task<(bool Ok, string? Error)> PromoteReplicaAsync(Guid replicaId, CancellationToken ct) => throw new NotSupportedException();
}

/// <summary>Quota answers a test sets, with a hook on the governed check for the "taken meanwhile" race.</summary>
internal sealed class ToolQuota : IQuotaService
{
    public QuotaCheck ServiceCheck { get; set; } = QuotaCheck.Ok;
    public QuotaCheck GovernedCheck { get; set; } = QuotaCheck.Ok;
    public Action<int>? OnGovernedCheck { get; set; }
    public int GovernedChecks { get; private set; }
    public int LocksTaken { get; private set; }

    public Task<IQuotaReservation> AcquireCreationLockAsync(Guid workspaceId, CancellationToken ct)
    {
        LocksTaken++;
        return Task.FromResult<IQuotaReservation>(NoopQuotaReservation.Instance);
    }

    public Task<QuotaCheck> CanAddGovernedResourcesAsync(Guid workspaceId, GovernanceQuotaDelta delta, CancellationToken ct)
    {
        OnGovernedCheck?.Invoke(++GovernedChecks);
        return Task.FromResult(GovernedCheck);
    }

    public Task<QuotaCheck> CanAddServiceAsync(Guid w, string? size, CancellationToken ct) => Task.FromResult(ServiceCheck);
    public Task<QuotaCheck> CanAddAppAsync(Guid w, string? s, Guid? e, CancellationToken ct) => throw new NotSupportedException();
    public Task<WorkspaceUsage> GetUsageAsync(Guid w, CancellationToken ct) => throw new NotSupportedException();
}

internal sealed class ToolScheduler : ISchedulerService
{
    public Guid PlacedOn { get; } = Guid.CreateVersion7();

    public Task<PlacementResult> PlaceAsync(long memoryBytes, double cpu, string? pool, CancellationToken ct) =>
        Task.FromResult(new PlacementResult(true, PlacedOn, null));

    public Task<PlacementResult> CheckAsync(Guid serverId, long memoryBytes, double cpu, CancellationToken ct) =>
        Task.FromResult(new PlacementResult(true, serverId, null));
}

/// <summary>Deletes the app row the way the real operations eventually do, unless told it will not stop.</summary>
internal sealed class DeletingAppOps(HarboraDbContext db) : IAppOperationsService
{
    public HashSet<Guid> Refuses { get; } = [];
    public List<Guid> Deleted { get; } = [];

    public async Task DeleteAsync(Guid appId, bool removeVolumes, CancellationToken ct)
    {
        if (Refuses.Contains(appId)) throw new InvalidOperationException($"container for app {appId} would not stop");
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
        Guid appId, bool enabled, string? messageEn, string? messageFa, CancellationToken ct) => throw new NotSupportedException();
    public Task<RateLimitToggleResult> SetRateLimitAsync(
        Guid appId, bool enabled, int average, int burst, CancellationToken ct) => throw new NotSupportedException();
    public Task<LogRetentionResult> SetLogRetentionAsync(Guid appId, int days, CancellationToken ct) => throw new NotSupportedException();
}
