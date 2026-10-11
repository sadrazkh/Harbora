using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Identity;
using Harbora.Domain.Services;
using Harbora.Infrastructure.Billing;
using Harbora.Infrastructure.Projects;
using Harbora.Infrastructure.Security;
using Harbora.Infrastructure.Services;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Environment = Harbora.Domain.Projects.Environment;
using Project = Harbora.Domain.Projects.Project;

namespace Harbora.Tests;

/// <summary>
/// One workspace on EF InMemory, under its own tenant filter, with the fakes the round-six database
/// services need: a recording audit, an engine that records provisioning and really removes the row,
/// a quota and a scheduler whose answers a test sets. Shared by
/// <see cref="DatabaseProvisioningServiceTests"/> and <see cref="DatabaseAttachmentServiceTests"/>.
/// </summary>
internal sealed class DatabaseServiceHarness : IDisposable
{
    public Guid WorkspaceId { get; } = Guid.CreateVersion7();
    public Guid UserId { get; } = Guid.CreateVersion7();
    public HarboraDbContext Db { get; }
    public Project Project { get; }
    public Environment Environment { get; }
    public RecordingAudit Audit { get; } = new();
    public RecordingEngine Engine { get; }
    public ScriptedQuota Quota { get; } = new();
    public ScriptedScheduler Scheduler { get; } = new();
    public FixedClock Clock { get; } = new();
    public bool BillingEnabled { get; set; }

    private readonly Caller _caller;

    public DatabaseServiceHarness(WorkspaceRole role = WorkspaceRole.Admin, bool withEnvironment = true)
    {
        Db = new HarboraDbContext(new DbContextOptionsBuilder<HarboraDbContext>()
                .UseInMemoryDatabase("db-services-" + Guid.NewGuid()).Options,
            new FixedWorkspaceScope(WorkspaceId));
        Engine = new RecordingEngine(Db);
        _caller = new Caller(WorkspaceId, UserId);

        Db.Workspaces.Add(new Workspace { Id = WorkspaceId, Name = "Acme", Slug = "acme-" + WorkspaceId.ToString("N")[..8] });
        Db.Users.Add(new User { Id = UserId, Email = "me@example.com", DisplayName = "Me", Role = SystemRole.Member });
        Db.WorkspaceMembers.Add(new WorkspaceMember { WorkspaceId = WorkspaceId, UserId = UserId, Role = role });

        Project = new Project { WorkspaceId = WorkspaceId, Name = "Shop", Slug = "shop" };
        Environment = new Environment
        {
            WorkspaceId = WorkspaceId, ProjectId = Project.Id, Name = "Production", Slug = "production", IsDefault = true
        };
        if (withEnvironment)
        {
            Db.Projects.Add(Project);
            Db.Environments.Add(Environment);
        }

        Db.SaveChanges();
    }

    public void Dispose() => Db.Dispose();

    /// <summary>Makes the caller a member scoped to one project, holding a Member grant there.</summary>
    public void ScopeCallerTo(Guid projectId)
    {
        var member = Db.WorkspaceMembers.IgnoreQueryFilters().Single(m => m.UserId == UserId);
        member.Role = WorkspaceRole.Member;
        member.ScopedToProjects = true;
        Db.ProjectGrants.Add(new Harbora.Domain.Authorization.ProjectGrant
        {
            WorkspaceId = WorkspaceId, UserId = UserId, ProjectId = projectId, Role = SystemRole.Member
        });
        Db.SaveChanges();
    }

    /// <summary>A second project with its own environment — another private network.</summary>
    public Environment AddOtherProject(string slug = "other")
    {
        var project = new Project { WorkspaceId = WorkspaceId, Name = slug, Slug = slug };
        var environment = new Environment
        {
            WorkspaceId = WorkspaceId, ProjectId = project.Id, Name = "Production", Slug = "production", IsDefault = true
        };
        Db.Projects.Add(project);
        Db.Environments.Add(environment);
        Db.SaveChanges();
        return environment;
    }

    public DatabaseProvisioningService Provisioning() => new(
        Db, Engine, Quota, Scheduler, new PassthroughProtector(), new ProjectService(Db, Clock, audit: Audit),
        new ProjectAccessService(Db, _caller),
        new ResourceCreationBilling(Db, Clock, Options.Create(new BillingOptions { Enabled = BillingEnabled })),
        Audit, _caller);

    public DatabaseAttachmentService Attachments() =>
        new(Db, new ProjectAccessService(Db, _caller), Audit, _caller);

    public DatabaseCreateRequest Request(string name = "shop-db", ManagedServiceType type = ManagedServiceType.PostgreSql) =>
        new(WorkspaceId, UserId, name, type);

    public ManagedService SeedService(
        string name, Guid? environmentId = null, Guid? primaryId = null, bool withDefaultDatabase = true)
    {
        var service = new ManagedService
        {
            WorkspaceId = WorkspaceId, EnvironmentId = environmentId ?? Environment.Id, ServerId = Guid.CreateVersion7(),
            Name = name, Type = ManagedServiceType.PostgreSql, Version = "16", Status = ServiceStatus.Running,
            ContainerName = DatabaseProvisioningService.ContainerPrefix + name,
            VolumeName = DatabaseProvisioningService.ContainerPrefix + name + "-data",
            InternalPort = 5432, Username = "harbora", DatabaseName = name.Replace('-', '_'),
            EncryptedPassword = "secret", PrimaryManagedServiceId = primaryId
        };
        Db.ManagedServices.Add(service);
        if (withDefaultDatabase && ManagedServiceDatabase.DefaultFor(service) is { } defaultDatabase)
            Db.ManagedServiceDatabases.Add(defaultDatabase);
        Db.SaveChanges();
        return service;
    }

    public App SeedApp(string name, Guid? environmentId = null, Guid? workspaceId = null)
    {
        var app = new App
        {
            WorkspaceId = workspaceId ?? WorkspaceId, EnvironmentId = environmentId ?? Environment.Id,
            ServerId = Guid.CreateVersion7(), Name = name, Slug = name,
            SourceType = AppSourceType.PrebuiltImage, PrebuiltImage = "nginx:1.27", Status = AppStatus.Running
        };
        Db.Apps.Add(app);
        Db.SaveChanges();
        return app;
    }

    /// <summary>What a test compares before and after a validate: every table a database operation writes.</summary>
    public Snapshot Counts() => new(
        Db.ManagedServices.IgnoreQueryFilters().Count(), Db.ManagedServiceDatabases.IgnoreQueryFilters().Count(),
        Db.AppManagedServices.IgnoreQueryFilters().Count(), Db.Projects.IgnoreQueryFilters().Count(),
        Db.Environments.IgnoreQueryFilters().Count(), Audit.Entries.Count, Engine.Provisioned.Count,
        Engine.Removed.Count, Quota.LocksTaken);

    internal readonly record struct Snapshot(
        int Services, int Databases, int Attachments, int Projects, int Environments, int Audits,
        int Provisions, int Removals, int Locks);

    internal sealed class Caller(Guid workspaceId, Guid userId) : ICurrentUser
    {
        public Guid? UserId { get; } = userId;
        public string? Email => "me@example.com";
        public bool IsAuthenticated => true;
        public Guid? WorkspaceId { get; } = workspaceId;
    }

    internal sealed record AuditEntry(string Action, string? TargetType, string? TargetId, string? IpAddress,
        string? MetadataJson, Guid? WorkspaceId);

    internal sealed class RecordingAudit : IAuditLogger
    {
        public List<AuditEntry> Entries { get; } = [];

        public Task LogAsync(string action, string? targetType = null, string? targetId = null,
            string? ipAddress = null, string? actorEmailOverride = null, Guid? userIdOverride = null,
            string? metadataJson = null, Guid? workspaceId = null, CancellationToken ct = default)
        {
            Entries.Add(new AuditEntry(action, targetType, targetId, ipAddress, metadataJson, workspaceId));
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Provisions nothing but records that it was asked, and removes the row the way
    /// <see cref="ManagedServiceEngine.RemoveAsync"/> does — unless told to behave like the real one
    /// does when its tenant-filtered read finds nothing, and return quietly.
    /// </summary>
    internal sealed class RecordingEngine(HarboraDbContext db) : IManagedServiceEngine
    {
        public List<Guid> Provisioned { get; } = [];
        public List<(Guid Id, bool DeleteData)> Removed { get; } = [];
        public bool RemoveQuietlyDoesNothing { get; set; }

        public IReadOnlyList<ServiceCatalogEntry> Catalog { get; } =
        [
            new(ManagedServiceType.PostgreSql, "PostgreSQL", "پستگرس", "postgres:16-alpine", ["16", "15"], 5432, true),
            new(ManagedServiceType.Redis, "Redis", "ردیس", "redis:7-alpine", ["7"], 6379, false)
        ];

        public Task QueueProvisionAsync(Guid serviceId, CancellationToken ct)
        {
            Provisioned.Add(serviceId);
            return Task.CompletedTask;
        }

        public async Task RemoveAsync(Guid serviceId, bool deleteData, CancellationToken ct)
        {
            Removed.Add((serviceId, deleteData));
            if (RemoveQuietlyDoesNothing) return;
            var svc = await db.ManagedServices.FirstAsync(s => s.Id == serviceId, ct);
            db.ManagedServices.Remove(svc);
            await db.SaveChangesAsync(ct);
        }

        public Task StartAsync(Guid serviceId, CancellationToken ct) => throw new NotSupportedException();
        public Task StopAsync(Guid serviceId, CancellationToken ct) => throw new NotSupportedException();
        public Task<long?> MeasureStorageAsync(Guid serviceId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<RotatedApp>> RotatePasswordAsync(Guid serviceId, CancellationToken ct) => throw new NotSupportedException();
        public Task<string?> TestConnectionAsync(Guid serviceId, CancellationToken ct) => throw new NotSupportedException();
        public Task<ServiceConnectionInfo> GetConnectionInfoAsync(Guid serviceId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<string, string>> BuildAttachEnvAsync(Guid serviceId, CancellationToken ct) => throw new NotSupportedException();
        public Task<RedisMemoryPolicyOutcome> UpdateRedisMemoryPolicyAsync(Guid serviceId, string? policy, long maxMemoryBytes, CancellationToken ct) => throw new NotSupportedException();
        public Task<(bool Ok, string? Error)> PromoteReplicaAsync(Guid replicaId, CancellationToken ct) => throw new NotSupportedException();
    }

    /// <summary>Answers the service check with whatever a test set, and counts the locks taken and committed.</summary>
    internal sealed class ScriptedQuota : IQuotaService
    {
        public QuotaCheck ServiceCheck { get; set; } = QuotaCheck.Ok;
        public int LocksTaken { get; private set; }
        public int Commits { get; private set; }

        public Task<IQuotaReservation> AcquireCreationLockAsync(Guid workspaceId, CancellationToken ct)
        {
            LocksTaken++;
            return Task.FromResult<IQuotaReservation>(new Reservation(this));
        }

        public Task<QuotaCheck> CanAddServiceAsync(Guid w, string? size, CancellationToken ct) => Task.FromResult(ServiceCheck);
        public Task<QuotaCheck> CanAddAppAsync(Guid w, string? s, Guid? e, CancellationToken ct) => throw new NotSupportedException();
        public Task<WorkspaceUsage> GetUsageAsync(Guid w, CancellationToken ct) => throw new NotSupportedException();

        private sealed class Reservation(ScriptedQuota owner) : IQuotaReservation
        {
            public Task CommitAsync(CancellationToken ct)
            {
                owner.Commits++;
                return Task.CompletedTask;
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    internal sealed class ScriptedScheduler : ISchedulerService
    {
        public Guid PlacedOn { get; set; } = Guid.CreateVersion7();
        public PlacementResult? Refusal { get; set; }

        public Task<PlacementResult> PlaceAsync(long memoryBytes, double cpu, string? pool, CancellationToken ct) =>
            Task.FromResult(Refusal ?? PlacementResult.Placed(PlacedOn));

        public Task<PlacementResult> CheckAsync(Guid serverId, long memoryBytes, double cpu, CancellationToken ct) =>
            Task.FromResult(Refusal ?? PlacementResult.Placed(serverId));
    }
}
