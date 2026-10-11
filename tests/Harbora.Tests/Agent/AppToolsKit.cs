using System.Text.Json;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Authorization;
using Harbora.Domain.Common;
using Harbora.Domain.Deployments;
using Harbora.Domain.Identity;
using Harbora.Domain.Networking;
using Harbora.Domain.Servers;
using Harbora.Domain.Settings;
using Harbora.Domain.Tenancy;
using Harbora.Infrastructure.Agent;
using Harbora.Infrastructure.Agent.Tools;
using Harbora.Infrastructure.Apps;
using Harbora.Infrastructure.Billing;
using Harbora.Infrastructure.Deployments;
using Harbora.Infrastructure.Networking;
using Harbora.Infrastructure.Projects;
using Harbora.Infrastructure.Templates;
using Harbora.Infrastructure.Tenancy;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProjectEnvironment = Harbora.Domain.Projects.Environment;
using Project = Harbora.Domain.Projects.Project;

namespace Harbora.Tests.Agent;

/// <summary>
/// The agent core (<see cref="AgentHarness"/>) with the thirteen app tools plugged in over the real app
/// services — provisioning, configuration, the template service, the rollback planner, the operations
/// service's delete — all on the harness's one EF InMemory context and one signed-in person, so a tool
/// is tested against the code the panel runs. Only what leaves the process is a double: Docker, the job
/// queue behind deployments, the scheduler, the managed-service provisioner.
/// </summary>
internal sealed class AppToolsHarness : IDisposable
{
    public const string RootDomain = "apps.example.com";
    public const string PanelDomain = "panel.example.com";

    public AgentHarness Agent { get; }
    public HarboraDbContext Db => Agent.Db;
    public Plan Plan { get; } = new() { Name = "Team", IsEnabled = true };
    public Server Server { get; } = new() { Name = "local", Hostname = "localhost", IsLocal = true, Status = ServerStatus.Online };
    public FakeDockerEngine Docker { get; } = new();
    public RowDeployments Deployments { get; }
    public RecordingOperations Operations { get; }
    public AppServiceHarness.StubScheduler Scheduler { get; } = new();
    public RecordingManagedServices ManagedServices { get; } = new();

    /// <summary>The lock button's vault, in memory: what the person entered privately, by handle.</summary>
    public FakeSecretVault Vault { get; } = new();

    /// <summary>Set to false for a host that holds no vault at all (the tools get null).</summary>
    public bool HasVault { get; set; } = true;
    public BillingOptions Billing { get; } = new() { Enabled = false };

    /// <summary>Replaces the real billing gate for restart_app, when a test needs a refusal.</summary>
    public IBillingGate? GateOverride { get; set; }

    public AppToolsHarness(SystemRole role = SystemRole.Owner, bool scoped = false)
    {
        Agent = new AgentHarness(role: role, scopedToProjects: scoped);
        Deployments = new RowDeployments(Db);
        Operations = new RecordingOperations(RealOperations());

        Db.Plans.Add(Plan);
        Db.Workspaces.IgnoreQueryFilters().Single(w => w.Id == Agent.WorkspaceId).PlanId = Plan.Id;
        Db.Servers.Add(Server);
        Db.Settings.Add(new Setting { Key = SettingKeys.PlatformRootDomain, Value = RootDomain });
        Db.SaveChanges();
        Scheduler.Result = PlacementResult.Placed(Server.Id);

        Agent.Tools.AddRange(AllTools());
    }

    public void Dispose() => Agent.Dispose();

    public Guid WorkspaceId => Agent.WorkspaceId;
    public Guid UserId => Agent.UserId;
    public Guid ProjectId => Agent.ProjectId;
    public Guid EnvironmentId => Agent.EnvironmentId;
    public string ProjectSlug => Agent.ProjectSlug;

    // ---- the real services, over the harness's context ----

    private IConfiguration Config() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["PANEL_DOMAIN"] = PanelDomain })
        .Build();

    public QuotaService Quota() => new(Db, Options.Create(Billing));
    private AppAddressAssigner Addresses() => new(Db, Config());
    private ProjectService Projects() => new(Db, Agent.Clock, Quota());
    private ResourceCreationBilling CreationBilling() => new(Db, Agent.Clock, Options.Create(Billing));

    public AppProvisioningService Provisioning() => new(
        Db, Quota(), Scheduler, Agent.Protector, Agent.Audit, Deployments, Projects(), Agent.Access, Addresses(), CreationBilling());

    public AppConfigurationService Configuration() => new(
        Db, Quota(), Agent.Protector, Agent.Audit, Agent.Access, Addresses(),
        new FakeServerEngineFactory(Docker), Config(), NullLogger<AppConfigurationService>.Instance);

    private AppOperationsService RealOperations() => new(
        Db, new FakeServerEngineFactory(Docker),
        new RecordingProxyEngine(() => Db.Routes.IgnoreQueryFilters().AsNoTracking().ToList()),
        new BillingGate(Db, Options.Create(Billing)),
        new HostPortAllocator(Db, TestIngress.Registry(), NullLogger<HostPortAllocator>.Instance),
        NullLogger<AppOperationsService>.Instance);

    public TemplateDeploymentService Templates() => new(
        Db, Projects(), Quota(), Agent.Protector, ManagedServices, Deployments, CreationBilling(), Addresses());

    private IBillingGate Gate() => new GateProxy(this);

    // ---- the tools ----

    public CreateAppTool CreateApp() => new(Db, Agent.Resolver, Agent.Access, Provisioning(), Options.Create(Billing));
    public DeployTemplateTool DeployTemplate() => new(Db, Agent.Resolver, Agent.Access, Templates(), Deployments, Quota(), Agent.Audit, Options.Create(Billing),
        NullLogger<DeployTemplateTool>.Instance, HasVault ? Vault : null);
    public SetEnvVarsTool SetEnvVars() => new(Db, Agent.Resolver, Agent.Access, Configuration(), HasVault ? Vault : null);
    public DeleteEnvVarTool DeleteEnvVar() => new(Db, Agent.Resolver, Agent.Access, Configuration());
    public AddDomainTool AddDomain() => new(Db, Agent.Resolver, Agent.Access, Configuration(), Quota());
    public RemoveDomainTool RemoveDomain() => new(Db, Agent.Resolver, Agent.Access, Configuration());
    public AddVolumeTool AddVolume() => new(Agent.Resolver, Agent.Access, Configuration(), Quota());
    public RemoveVolumeTool RemoveVolume() => new(Db, Agent.Resolver, Agent.Access, Configuration());
    public DeployAppTool DeployApp() => new(Db, Agent.Resolver, Agent.Access, Deployments, Quota(), Agent.Audit);
    public RestartAppTool RestartApp() => new(Agent.Resolver, Agent.Access, Operations, Gate());
    public StopAppTool StopApp() => new(Agent.Resolver, Agent.Access, Operations);
    public RollbackAppTool RollbackApp() => new(Db, Agent.Resolver, Agent.Access, new RollbackPlanner(Db, new FakeServerEngineFactory(Docker)), Deployments, Agent.Audit);
    public DeleteAppTool DeleteApp() => new(Db, Agent.Resolver, Agent.Access, Operations, Agent.Audit);

    public IReadOnlyList<IAgentTool> AllTools() =>
    [
        CreateApp(), DeployTemplate(), SetEnvVars(), DeleteEnvVar(), AddDomain(), RemoveDomain(), AddVolume(),
        RemoveVolume(), DeployApp(), RestartApp(), StopApp(), RollbackApp(), DeleteApp()
    ];

    public IAgentTool Tool(string name) => AllTools().Single(t => t.Name == name);

    // ---- calling a tool directly ----

    public AgentToolContext Context => new(WorkspaceId, UserId, Agent.Principal, Guid.NewGuid(), null);

    /// <summary>What the pane's lock button does: the value goes to the vault, the handle comes back.</summary>
    public string Lock(string name, string value) => Vault.Put(WorkspaceId, UserId, name, value).Handle;

    public static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);

    public Task<AgentValidation> ValidateAsync(IAgentTool tool, object args, AgentPlanScope? scope = null) =>
        tool.ValidateAsync(Args(args), Context, scope ?? new AgentPlanScope(), CancellationToken.None);

    public Task<AgentToolResult> ExecuteAsync(IAgentTool tool, object args) =>
        tool.ExecuteAsync(Args(args), Context, CancellationToken.None);

    // ---- seeding ----

    /// <summary>
    /// An app every app tool can validate against: deployed twice (both succeeded, #2 live, #1's image still
    /// on the server), running, with one plain and one secret variable, a primary domain and a volume.
    /// </summary>
    public App SeedApp(string slug = "web", Guid? environmentId = null, bool deployed = true)
    {
        var app = new App
        {
            WorkspaceId = WorkspaceId, EnvironmentId = environmentId ?? EnvironmentId, ServerId = Server.Id,
            Name = slug, Slug = slug, SourceType = AppSourceType.PrebuiltImage, PrebuiltImage = "nginx:alpine",
            Status = deployed ? AppStatus.Running : AppStatus.Created, GitRef = "main"
        };
        app.EnvironmentVariables.Add(new EnvironmentVariable { Key = "PORT", Value = "8080" });
        app.EnvironmentVariables.Add(new EnvironmentVariable { Key = "API_KEY", Value = Agent.Protector.Protect("sk-live-0000"), IsSecret = true });
        app.Domains.Add(new DomainName { Host = $"{slug}.example.org", IsPrimary = true, SslEnabled = true });
        app.Volumes.Add(new Volume { Name = $"harbora-vol-{slug}-files", MountPath = "/files" });
        Db.Apps.Add(app);

        if (deployed)
        {
            var first = new Deployment
            {
                AppId = app.Id, WorkspaceId = WorkspaceId, Number = 1, Status = DeploymentStatus.Succeeded,
                ImageTag = $"harbora/{slug}:1", CommitSha = "aaaaaaaa1111", CommitMessage = "First release",
                FinishedAt = AgentHarness.Start.AddDays(-2)
            };
            var second = new Deployment
            {
                AppId = app.Id, WorkspaceId = WorkspaceId, Number = 2, Status = DeploymentStatus.Succeeded,
                ImageTag = $"harbora/{slug}:2", FinishedAt = AgentHarness.Start.AddDays(-1)
            };
            Db.Deployments.AddRange(first, second);
            app.ActiveDeploymentId = second.Id;
            Docker.SeedImage(first.ImageTag, second.ImageTag);
        }

        Db.SaveChanges();
        Db.ChangeTracker.Clear();
        return app;
    }

    /// <summary>Another workspace, with a project, an environment and an app of its own.</summary>
    public App SeedForeignApp(string slug = "theirs")
    {
        var workspace = new Workspace { Name = "Other", Slug = "other-" + Guid.NewGuid().ToString("N")[..8] };
        var project = new Project { WorkspaceId = workspace.Id, Name = "Theirs", Slug = "theirs" };
        var environment = new ProjectEnvironment
        {
            WorkspaceId = workspace.Id, ProjectId = project.Id, Name = "Production", Slug = "production", IsDefault = true
        };
        var app = new App
        {
            WorkspaceId = workspace.Id, EnvironmentId = environment.Id, ServerId = Server.Id, Name = slug, Slug = slug,
            SourceType = AppSourceType.PrebuiltImage, PrebuiltImage = "nginx:alpine", Status = AppStatus.Running
        };
        Db.Workspaces.Add(workspace);
        Db.Projects.Add(project);
        Db.Environments.Add(environment);
        Db.Apps.Add(app);
        Db.SaveChanges();
        Db.ChangeTracker.Clear();
        return app;
    }

    public void MakeProtected(Guid? environmentId = null)
    {
        Db.Environments.Single(e => e.Id == (environmentId ?? EnvironmentId)).IsProtected = true;
        Db.SaveChanges();
        Db.ChangeTracker.Clear();
    }

    /// <summary>A second project with its own default environment, so "which project?" has two answers.</summary>
    public (Project Project, ProjectEnvironment Environment) SeedProject(string slug)
    {
        var project = new Project { WorkspaceId = WorkspaceId, Name = slug, Slug = slug };
        var environment = new ProjectEnvironment
        {
            WorkspaceId = WorkspaceId, ProjectId = project.Id, Name = "Production", Slug = "production", IsDefault = true
        };
        Db.Projects.Add(project);
        Db.Environments.Add(environment);
        Db.SaveChanges();
        Db.ChangeTracker.Clear();
        return (project, environment);
    }

    /// <summary>A grant on the harness project, for a scoped member.</summary>
    public void Grant(SystemRole role)
    {
        Db.ProjectGrants.Add(new ProjectGrant { WorkspaceId = WorkspaceId, UserId = UserId, ProjectId = ProjectId, Role = role });
        Db.SaveChanges();
        Db.ChangeTracker.Clear();
    }

    /// <summary>
    /// Every table an app tool can write, across all tenants, plus the audit log, the queued work and what
    /// the context is tracking as added, changed or deleted — "changed nothing" asserted as a whole.
    /// </summary>
    public string Snapshot()
    {
        Db.ChangeTracker.DetectChanges();
        var pending = Db.ChangeTracker.Entries().Count(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted);
        return string.Join(", ",
            $"apps={Db.Apps.IgnoreQueryFilters().Count()}",
            $"env={string.Join("|", Db.EnvironmentVariables.IgnoreQueryFilters().OrderBy(v => v.Key).Select(v => v.Key + "=" + v.Value + ":" + v.IsSecret))}",
            $"domains={string.Join("|", Db.Domains.IgnoreQueryFilters().OrderBy(d => d.Host).Select(d => d.Host + ":" + d.IsPrimary))}",
            $"volumes={string.Join("|", Db.Volumes.IgnoreQueryFilters().OrderBy(v => v.MountPath).Select(v => v.MountPath))}",
            $"projects={Db.Projects.IgnoreQueryFilters().Count()}",
            $"environments={Db.Environments.IgnoreQueryFilters().Count()}",
            $"services={Db.ManagedServices.IgnoreQueryFilters().Count()}",
            $"deployments={Db.Deployments.IgnoreQueryFilters().Count()}",
            $"status={string.Join("|", Db.Apps.IgnoreQueryFilters().OrderBy(a => a.Slug).Select(a => a.Slug + ":" + a.Status))}",
            $"ledger={Db.BillingLedger.IgnoreQueryFilters().Count()}",
            $"audit={Agent.Audit.Entries.Count}",
            $"queued={Deployments.Queued.Count}",
            $"ops={Operations.Calls.Count}",
            $"provisioned={ManagedServices.Provisioned.Count}",
            $"docker={Docker.Calls.Count(c => c.Operation is "RemoveVolumeAsync" or "RemoveContainerAsync" or "StopContainerAsync" or "RestartContainerAsync")}",
            $"pending={pending}");
    }

    private sealed class GateProxy(AppToolsHarness harness) : IBillingGate
    {
        private IBillingGate Inner => harness.GateOverride ?? new BillingGate(harness.Db, Options.Create(harness.Billing));
        public Task<QuotaCheck> CanStartAsync(Guid workspaceId, CancellationToken ct) => Inner.CanStartAsync(workspaceId, ct);
        public Task<QuotaCheck> CanStartAsync(Guid workspaceId, Domain.Billing.BilledResourceType resourceType, Guid resourceId, CancellationToken ct) =>
            Inner.CanStartAsync(workspaceId, resourceType, resourceId, ct);
    }
}

/// <summary>
/// The deployment engine's queueing as far as the tools can observe it: a row per request — Queued, or
/// PendingApproval in a protected environment — numbered per app, joined onto an unsettled one exactly as
/// <c>DeploymentEngine</c> coalesces. No job queue behind it.
/// </summary>
public sealed class RowDeployments(HarboraDbContext db) : IDeploymentEngine
{
    public List<DeploymentRequest> Queued { get; } = [];
    public Exception? Refuse { get; set; }

    /// <summary>Thrown from outside the engine — what an EF error inside it would look like to a caller.</summary>
    public Action? FailsFromElsewhere { get; set; }

    public async Task<Guid> QueueDeploymentAsync(DeploymentRequest request, CancellationToken ct)
    {
        if (Refuse is not null) throw Refuse;
        FailsFromElsewhere?.Invoke();
        var app = await db.Apps.IgnoreQueryFilters().AsNoTracking().FirstAsync(a => a.Id == request.AppId, ct);

        var unsettled = DeploymentStateMachine.Unsettled.ToArray();
        var inFlight = await db.Deployments.IgnoreQueryFilters()
            .Where(d => d.AppId == app.Id && unsettled.Contains(d.Status))
            .OrderByDescending(d => d.Number).FirstOrDefaultAsync(ct);
        if (inFlight is not null)
        {
            // DeploymentEngine.RollbackIntentMismatch: a rollback never joins a deploy, nor a deploy a rollback.
            if ((inFlight.RolledBackFromId is not null) != (request.RollbackToDeploymentId is not null))
                throw new InvalidOperationException(request.RollbackToDeploymentId is not null
                    ? $"Deployment #{inFlight.Number} is still running. Wait for it to finish or cancel it, then roll back."
                    : $"A rollback (deployment #{inFlight.Number}) is still running. Wait for it to finish, then deploy.");
            Queued.Add(request);
            return inFlight.Id;
        }
        Queued.Add(request);

        var isProtected = await db.Environments.IgnoreQueryFilters()
            .Where(e => e.Id == app.EnvironmentId).Select(e => e.IsProtected).FirstOrDefaultAsync(ct);
        var number = (await db.Deployments.IgnoreQueryFilters().Where(d => d.AppId == app.Id).MaxAsync(d => (int?)d.Number, ct) ?? 0) + 1;
        var deployment = new Deployment
        {
            AppId = app.Id, WorkspaceId = app.WorkspaceId, Number = number,
            Status = isProtected ? DeploymentStatus.PendingApproval : DeploymentStatus.Queued,
            Trigger = request.Trigger, TriggeredByUserId = request.TriggeredByUserId, GitRef = request.GitRef,
            RolledBackFromId = request.RollbackToDeploymentId
        };
        db.Deployments.Add(deployment);
        await db.SaveChangesAsync(ct);
        return deployment.Id;
    }

    public Task CancelAsync(Guid deploymentId, CancellationToken ct) => throw new NotSupportedException();
    public Task ApproveAsync(Guid deploymentId, Guid approverUserId, CancellationToken ct) => throw new NotSupportedException();
    public Task RejectAsync(Guid deploymentId, Guid approverUserId, string reason, CancellationToken ct) => throw new NotSupportedException();
}

/// <summary>
/// The operations service with its delete running for real (the InMemory provider can run it) and restart
/// and stop recorded — those write the status with <c>ExecuteUpdate</c>, which the InMemory provider cannot.
/// </summary>
public sealed class RecordingOperations(IAppOperationsService inner) : IAppOperationsService
{
    private readonly List<(string Verb, Guid AppId)> _calls = [];
    public IReadOnlyList<(string Verb, Guid AppId)> Calls => _calls;
    public Exception? RestartThrows { get; set; }

    public Task RestartAsync(Guid appId, CancellationToken ct)
    {
        if (RestartThrows is not null) throw RestartThrows;
        _calls.Add(("restart", appId));
        return Task.CompletedTask;
    }

    public Task StopAsync(Guid appId, CancellationToken ct)
    {
        _calls.Add(("stop", appId));
        return Task.CompletedTask;
    }

    public Task StartAsync(Guid appId, CancellationToken ct)
    {
        _calls.Add(("start", appId));
        return Task.CompletedTask;
    }

    public async Task DeleteAsync(Guid appId, bool removeVolumes, CancellationToken ct)
    {
        _calls.Add(("delete", appId));
        await inner.DeleteAsync(appId, removeVolumes, ct);
    }

    public Task<string> GetLogsAsync(Guid appId, int tail, CancellationToken ct) => throw new NotSupportedException();
    public Task<LogSearchResult> SearchLogsAsync(IReadOnlyList<Guid> appIds, string? text, bool problemsOnly, TimeSpan? window,
        int maxLinesPerApp, CancellationToken ct) => throw new NotSupportedException();
    public Task<MaintenanceToggleResult> SetMaintenanceModeAsync(Guid appId, bool enabled, string? messageEn, string? messageFa,
        CancellationToken ct) => throw new NotSupportedException();
    public Task<RateLimitToggleResult> SetRateLimitAsync(Guid appId, bool enabled, int average, int burst, CancellationToken ct) =>
        throw new NotSupportedException();
    public Task<LogRetentionResult> SetLogRetentionAsync(Guid appId, int days, CancellationToken ct) => throw new NotSupportedException();
}

/// <summary>A managed-service engine that records what it was asked to provision and does nothing else.</summary>
public sealed class RecordingManagedServices : IManagedServiceEngine
{
    public List<Guid> Provisioned { get; } = [];
    public Exception? ProvisionThrows { get; set; }
    public IReadOnlyList<ServiceCatalogEntry> Catalog { get; } = [];

    public Task QueueProvisionAsync(Guid serviceId, CancellationToken ct)
    {
        if (ProvisionThrows is not null) throw ProvisionThrows;
        Provisioned.Add(serviceId);
        return Task.CompletedTask;
    }

    public Task StartAsync(Guid serviceId, CancellationToken ct) => throw new NotSupportedException();
    public Task StopAsync(Guid serviceId, CancellationToken ct) => throw new NotSupportedException();
    public Task RemoveAsync(Guid serviceId, bool deleteData, CancellationToken ct) => throw new NotSupportedException();
    public Task<long?> MeasureStorageAsync(Guid serviceId, CancellationToken ct) => throw new NotSupportedException();
    public Task<IReadOnlyList<RotatedApp>> RotatePasswordAsync(Guid serviceId, CancellationToken ct) => throw new NotSupportedException();
    public Task<string?> TestConnectionAsync(Guid serviceId, CancellationToken ct) => throw new NotSupportedException();
    public Task<ServiceConnectionInfo> GetConnectionInfoAsync(Guid serviceId, CancellationToken ct) => throw new NotSupportedException();
    public Task<IReadOnlyDictionary<string, string>> BuildAttachEnvAsync(Guid serviceId, CancellationToken ct) => throw new NotSupportedException();
    public Task<RedisMemoryPolicyOutcome> UpdateRedisMemoryPolicyAsync(Guid serviceId, string? policy, long maxMemoryBytes, CancellationToken ct) =>
        throw new NotSupportedException();
    public Task<(bool Ok, string? Error)> PromoteReplicaAsync(Guid replicaId, CancellationToken ct) => throw new NotSupportedException();
}

/// <summary>A billing gate that refuses, in both languages.</summary>
public sealed class RefusingGate : IBillingGate
{
    public Task<QuotaCheck> CanStartAsync(Guid workspaceId, CancellationToken ct) =>
        Task.FromResult(new QuotaCheck(false, "The wallet is empty.", "کیف پول خالی است."));
}

/// <summary>
/// The lock button's vault as the tools see it: a handle per value, scoped to one person in one workspace,
/// and forgettable — <see cref="Forget"/> stands in for expiry or a restart between the card and the run.
/// Records every resolve, so a test can tell that a card was drawn without the value being handed out
/// beyond the existence check.
/// </summary>
public sealed class FakeSecretVault : IAgentSecretVault
{
    private readonly Dictionary<string, (Guid Workspace, Guid User, string Value)> _held = new(StringComparer.Ordinal);
    private int _next;

    public int Resolves { get; private set; }

    public AgentSecretView Put(Guid workspaceId, Guid userId, string name, string value)
    {
        var id = (++_next).ToString("D22");
        var handle = AgentSecretHandles.Format(id);
        _held[handle] = (workspaceId, userId, value);
        return new AgentSecretView(handle, name, DateTimeOffset.UtcNow + AgentSecretHandles.Lifetime);
    }

    public string? Resolve(Guid workspaceId, Guid userId, string handle)
    {
        Resolves++;
        return _held.TryGetValue(handle.Trim(), out var held) && held.Workspace == workspaceId && held.User == userId
            ? held.Value
            : null;
    }

    public void Forget(string handle) => _held.Remove(handle);
}
