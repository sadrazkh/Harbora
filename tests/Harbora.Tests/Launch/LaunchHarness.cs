using System.IO.Compression;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Common;
using Harbora.Domain.Identity;
using Harbora.Domain.Launch;
using Harbora.Domain.Servers;
using Harbora.Domain.Settings;
using Harbora.Domain.Tenancy;
using Harbora.Infrastructure.Apps;
using Harbora.Infrastructure.Billing;
using Harbora.Infrastructure.Deployments;
using Harbora.Infrastructure.Launch;
using Harbora.Infrastructure.Networking;
using Harbora.Infrastructure.Projects;
using Harbora.Infrastructure.Security;
using Harbora.Infrastructure.Services;
using Harbora.Infrastructure.Tenancy;
using Harbora.Infrastructure.Verification;
using Harbora.Shared.Inspection;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Environment = Harbora.Domain.Projects.Environment;
using Project = Harbora.Domain.Projects.Project;

namespace Harbora.Tests.Launch;

/// <summary>
/// One workspace on an EF InMemory store, and the real services a launch goes through. Everything off
/// the panel is a double: the provisioner, git, the reader, the checks.
///
/// <para>
/// <see cref="Request"/> and <see cref="Worker"/> each build a new context and a new service, so every
/// call is already "a panel that restarted": nothing a launch needs can be remembered on an instance.
/// The worker runs under a scope that sees no tenant at all, which is harsher than production's unscoped
/// one and is what proves the tick's reads are deliberately unfiltered rather than unfiltered by luck.
/// </para>
/// </summary>
internal sealed class LaunchHarness : IDisposable
{
    public const string RootDomain = "apps.example.com";
    public const string SizeKey = "small";

    private readonly string _store = "launch-" + Guid.NewGuid();
    private readonly List<HarboraDbContext> _contexts = [];

    public string WorkDir { get; } = Path.Combine(Path.GetTempPath(), "harbora-launch-" + Guid.NewGuid().ToString("N"));
    public Guid WorkspaceId { get; } = Guid.CreateVersion7();
    public Guid UserId { get; } = Guid.CreateVersion7();
    public Guid OtherWorkspaceId { get; } = Guid.CreateVersion7();
    public Environment? Environment { get; }
    public Server ServerA { get; } = new() { Name = "a", Hostname = "a.example.com", IsLocal = true, Status = ServerStatus.Online };
    public Server ServerB { get; } = new() { Name = "b", Hostname = "b.example.com", Status = ServerStatus.Online };

    public FixedClock Clock { get; } = new(DateTimeOffset.UtcNow);
    public AppServiceHarness.RecordingAudit Audit { get; } = new();
    public BillingOptions Billing { get; } = new() { Enabled = false };
    public RecordingScheduler Scheduler { get; } = new();
    public RecordingProvisioner Provisioner { get; } = new();
    public RecordingJobs Jobs { get; } = new();
    public ScriptedGit Git { get; } = new();
    public ScriptedReader Reader { get; } = new();
    public ScriptedVerifier Verifier { get; } = new();
    public ScriptedCreates Creates { get; } = new();
    public FakeDockerEngine Docker { get; } = new();
    public CapturingLogger Logs { get; } = new();

    /// <summary>Thrown by the next queue of a deployment, then forgotten.</summary>
    public Exception? RefuseNextDeployment { get; set; }
    public int DeploymentsQueued { get; set; }

    public LaunchHarness(bool withEnvironment = true, WorkspaceRole role = WorkspaceRole.Admin, bool scoped = false)
    {
        Directory.CreateDirectory(WorkDir);
        Scheduler.PlaceOn(ServerA.Id);

        using var db = Open(new FixedWorkspaceScope(WorkspaceId));
        var plan = new Plan { Name = "Team", IsEnabled = true };
        db.Plans.Add(plan);
        db.Workspaces.Add(new Workspace { Id = WorkspaceId, Name = "Acme", Slug = "acme", PlanId = plan.Id });
        db.Workspaces.Add(new Workspace { Id = OtherWorkspaceId, Name = "Other", Slug = "other", PlanId = plan.Id });
        db.Users.Add(new User { Id = UserId, Email = "member@example.com", DisplayName = "Member" });
        db.WorkspaceMembers.Add(new WorkspaceMember { WorkspaceId = WorkspaceId, UserId = UserId, Role = role, ScopedToProjects = scoped });
        db.Servers.AddRange(ServerA, ServerB);
        db.Settings.Add(new Setting { Key = SettingKeys.PlatformRootDomain, Value = RootDomain });
        db.InstanceSizes.Add(new InstanceSize
        {
            Key = SizeKey, Name = "Small", CpuCores = 1, MemoryBytes = 1L << 30, DiskBytes = 10L << 30,
            RunningRatePerHourMinor = 125, StoppedRatePerHourMinor = 10
        });

        if (withEnvironment)
        {
            var project = new Project { WorkspaceId = WorkspaceId, Name = "Shop", Slug = "shop" };
            Environment = new Environment
            {
                WorkspaceId = WorkspaceId, ProjectId = project.Id, Name = "Production", Slug = "production", IsDefault = true
            };
            db.Projects.Add(project);
            db.Environments.Add(Environment);
        }

        db.SaveChanges();
    }

    public void Dispose()
    {
        foreach (var context in _contexts) context.Dispose();
        try { Directory.Delete(WorkDir, recursive: true); }
        catch (IOException) { /* a temp folder; the OS clears it */ }
    }

    private HarboraDbContext Open(IWorkspaceScope scope) => new(
        new DbContextOptionsBuilder<HarboraDbContext>().UseInMemoryDatabase(_store).Options, scope);

    private HarboraDbContext Track(IWorkspaceScope scope)
    {
        var context = Open(scope);
        _contexts.Add(context);
        return context;
    }

    /// <summary>A context that sees every tenant, for a test to read and arrange the store with.</summary>
    public HarboraDbContext Store() => Track(SystemWorkspaceScope.Instance);

    /// <summary>A signed-in request in this workspace.</summary>
    public LaunchService Request() =>
        Build(Track(new FixedWorkspaceScope(WorkspaceId)), new Caller(WorkspaceId, UserId));

    /// <summary>A signed-in request from somebody in another workspace.</summary>
    public LaunchService OtherWorkspace() =>
        Build(Track(new FixedWorkspaceScope(OtherWorkspaceId)), new Caller(OtherWorkspaceId, Guid.CreateVersion7()));

    /// <summary>The tick or the job worker: no signed-in user, and no tenant in the scope.</summary>
    public LaunchService Worker() =>
        Build(Track(new FixedWorkspaceScope(Guid.CreateVersion7())), new NoSession());

    public Task<int> TickAsync() => Worker().AdvanceAllAsync(default);

    public SizePickerProbe SizePicker() => new(Track(new FixedWorkspaceScope(WorkspaceId)), Options.Create(Billing));

    private LaunchService Build(HarboraDbContext db, ICurrentUser user)
    {
        var billing = Options.Create(Billing);
        var protector = new PassthroughProtector();
        var quota = new QuotaService(db, billing);
        var projects = new ProjectService(db, Clock, quota, Audit);
        var access = new ProjectAccessService(db, user);
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["PANEL_DOMAIN"] = "panel.example.com" }).Build();
        var addresses = new AppAddressAssigner(db, config);
        var creationBilling = new ResourceCreationBilling(db, Clock, billing);
        var deployments = new GatedDeployments(new DeploymentEngine(db, Jobs, Clock), this);

        var apps = new ScriptedApps(
            new AppProvisioningService(db, quota, Scheduler, protector, Audit, deployments, projects, access, addresses, creationBilling),
            Creates);
        var databases = new ScriptedDatabases(
            new DatabaseProvisioningService(db, Provisioner, quota, Scheduler, protector, projects, access, creationBilling, Audit, user),
            Creates);
        var attachments = new DatabaseAttachmentService(db, access, Audit, user);
        var configuration = new AppConfigurationService(db, quota, protector, Audit, access, addresses,
            new FakeServerEngineFactory(Docker), config, NullLogger<AppConfigurationService>.Instance);

        var provider = new ServiceCollection().AddSingleton<IAppVerifier>(Verifier).BuildServiceProvider();

        return new LaunchService(
            db, apps, databases, attachments, configuration, deployments, Provisioner, Git, Jobs, protector,
            new SecretRedactor(), Audit, user, Clock, access, Scheduler, new NoReadings(), creationBilling,
            Options.Create(new HarboraRuntimeOptions { WorkDir = WorkDir }), billing, Reader, provider, Logs);
    }

    // ---- getting a launch to where a test wants it ---------------------------------------------

    public const string CloneUrl = "https://git.example.com/acme/shop.git";

    public LaunchStartRequest GitRequest(string url = CloneUrl, string? token = null, string? gitRef = null) => new()
    {
        WorkspaceId = WorkspaceId, UserId = UserId, Source = LaunchSourceKind.Git,
        CloneUrl = url, GitToken = token, GitRef = gitRef, ClientIp = "203.0.113.9"
    };

    /// <summary>A launch that has been read and is sitting on its confirm screen.</summary>
    public async Task<Guid> ReadyGitAsync(SourceInspection inspection, string url = CloneUrl, string? token = null)
    {
        Reader.Inspection = inspection;
        var started = await Request().StartAsync(GitRequest(url, token), default);
        if (!started.Ok) throw new InvalidOperationException(started.Refusals[0].Message);
        await Worker().InspectAsync(started.Value, default);
        return started.Value;
    }

    public async Task<(Guid Id, string Archive)> ReadyUploadAsync(SourceInspection inspection)
    {
        Reader.Inspection = inspection;
        var archive = WriteArchive("my-shop-0123456789abcdef0123456789abcdef.zip");
        var started = await Request().StartAsync(new LaunchStartRequest
        {
            WorkspaceId = WorkspaceId, UserId = UserId, Source = LaunchSourceKind.Upload, ArchivePath = archive
        }, default);
        if (!started.Ok) throw new InvalidOperationException(started.Refusals[0].Message);
        await Worker().InspectAsync(started.Value, default);
        return (started.Value, archive);
    }

    /// <summary>A real zip under the upload directory, where a controller would have written it.</summary>
    public string WriteArchive(string fileName, bool junk = false)
    {
        var uploads = Path.Combine(WorkDir, "_uploads");
        Directory.CreateDirectory(uploads);
        var path = Path.Combine(uploads, fileName);

        if (junk)
        {
            File.WriteAllText(path, "this is not an archive");
            return path;
        }

        using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        using var writer = new StreamWriter(zip.CreateEntry("package.json").Open());
        writer.Write("{}");
        return path;
    }

    public async Task<LaunchChoices> DefaultsAsync(Guid id) =>
        (await Request().ProposeAsync(id, default))?.Defaults
        ?? throw new InvalidOperationException("The launch has no proposal.");

    /// <summary>Confirms with what the screen opened on, or with changes to it.</summary>
    public async Task<LaunchView> ConfirmAsync(Guid id, Func<LaunchChoices, LaunchChoices>? change = null)
    {
        var choices = await DefaultsAsync(id);
        var outcome = await Request().ConfirmAsync(id, change?.Invoke(choices) ?? choices, "203.0.113.9", default);
        if (!outcome.Ok) throw new InvalidOperationException(string.Join(" | ", outcome.Refusals.Select(r => r.Message)));
        return outcome.Value!;
    }

    public AppLaunch Row(Guid id) => Store().AppLaunches.IgnoreQueryFilters().AsNoTracking().Single(l => l.Id == id);

    public void ServicesAre(ServiceStatus status, string? error = null)
    {
        using var db = Open(SystemWorkspaceScope.Instance);
        foreach (var service in db.ManagedServices.IgnoreQueryFilters())
        {
            service.Status = status;
            service.ErrorMessage = error;
        }
        db.SaveChanges();
    }

    public void DeploymentIs(Guid id, DeploymentStatus status, string? error = null)
    {
        using var db = Open(SystemWorkspaceScope.Instance);
        var deployment = db.Deployments.IgnoreQueryFilters().Single(d => d.Id == id);
        deployment.Status = status;
        deployment.ErrorMessage = error;
        db.SaveChanges();
    }

    public void ChangeRow(Guid id, Action<AppLaunch> change)
    {
        using var db = Open(SystemWorkspaceScope.Instance);
        change(db.AppLaunches.IgnoreQueryFilters().Single(l => l.Id == id));
        db.SaveChanges();
    }

    public void Advance(TimeSpan by) => Clock.UtcNow += by;

    // ---- the doubles ---------------------------------------------------------------------------

    private sealed class Caller(Guid workspaceId, Guid userId) : ICurrentUser
    {
        public Guid? UserId { get; } = userId;
        public string? Email => "member@example.com";
        public bool IsAuthenticated => true;
        public Guid? WorkspaceId { get; } = workspaceId;
    }

    private sealed class NoSession : ICurrentUser
    {
        public Guid? UserId => null;
        public string? Email => null;
        public bool IsAuthenticated => false;
        public Guid? WorkspaceId => null;
    }

    /// <summary>No host has been measured: the state a fresh install is in, and not "no room".</summary>
    public sealed class NoReadings(IReadOnlyList<NodeCapacity>? readings = null) : INodeCapacityService
    {
        private readonly IReadOnlyList<NodeCapacity> _readings = readings ?? [];
        public Task<IReadOnlyList<NodeCapacity>> GetAllAsync(CancellationToken ct) => Task.FromResult(_readings);
        public Task<NodeCapacity?> GetAsync(Guid serverId, CancellationToken ct) =>
            Task.FromResult(_readings.FirstOrDefault(r => r.ServerId == serverId));
    }

    public sealed class RecordingScheduler : ISchedulerService
    {
        private readonly Queue<Guid> _placements = new();
        private Guid _last;

        public int Placements { get; private set; }
        public List<Guid> Checked { get; } = [];
        public (long Memory, double Cpu, string? Pool) LastAsked { get; private set; }
        public PlacementResult? RefusePlacement { get; set; }

        /// <summary>What it answers from now on, one per question, the last one for ever after.</summary>
        public void PlaceOn(params Guid[] servers)
        {
            _placements.Clear();
            foreach (var server in servers) _placements.Enqueue(server);
        }

        public Task<PlacementResult> PlaceAsync(long memoryBytes, double cpu, string? requiredPool, CancellationToken ct)
        {
            Placements++;
            LastAsked = (memoryBytes, cpu, requiredPool);
            if (RefusePlacement is { } refused) return Task.FromResult(refused);
            if (_placements.Count > 0) _last = _placements.Dequeue();
            return Task.FromResult(PlacementResult.Placed(_last));
        }

        public Task<PlacementResult> CheckAsync(Guid serverId, long memoryBytes, double cpu, CancellationToken ct)
        {
            Checked.Add(serverId);
            return Task.FromResult(PlacementResult.Placed(serverId));
        }
    }

    /// <summary>Provisions nothing, and offers the real catalogue so the real service's rules apply to it.</summary>
    public sealed class RecordingProvisioner : IManagedServiceEngine
    {
        public List<Guid> Provisioned { get; } = [];

        public IReadOnlyList<ServiceCatalogEntry> Catalog { get; } = ServiceCatalog.All.Values
            .Select(d => new ServiceCatalogEntry(d.Type, d.DisplayName, d.DisplayNameFa, $"{d.ImageRepo}:{d.Versions[0]}", d.Versions, d.Port, d.HasDatabaseName))
            .ToList();

        public Task QueueProvisionAsync(Guid serviceId, CancellationToken ct)
        {
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
        public Task<RedisMemoryPolicyOutcome> UpdateRedisMemoryPolicyAsync(Guid serviceId, string? policy, long maxMemoryBytes, CancellationToken ct) => throw new NotSupportedException();
        public Task<(bool Ok, string? Error)> PromoteReplicaAsync(Guid replicaId, CancellationToken ct) => throw new NotSupportedException();
    }

    public sealed class RecordingJobs : IJobQueue
    {
        public List<(Harbora.Domain.Jobs.JobKind Kind, Guid Target)> Enqueued { get; } = [];
        public List<Guid> CancelRequests { get; } = [];

        public Task<Guid> EnqueueAsync(Harbora.Domain.Jobs.JobKind kind, Guid targetId, Guid? workspaceId = null, CancellationToken ct = default)
        {
            Enqueued.Add((kind, targetId));
            return Task.FromResult(Guid.CreateVersion7());
        }

        public Task<Guid> EnqueueExclusiveAsync(Harbora.Domain.Jobs.JobKind kind, Guid targetId, Guid exclusiveWith, Guid? workspaceId = null, CancellationToken ct = default) =>
            EnqueueAsync(kind, targetId, workspaceId, ct);

        public Task<bool> RequestCancellationAsync(Harbora.Domain.Jobs.JobKind kind, Guid targetId, CancellationToken ct = default)
        {
            CancelRequests.Add(targetId);
            return Task.FromResult(true);
        }
    }

    public sealed class ScriptedGit : IGitService
    {
        public List<(string Url, string Ref, string? Token, string Dir)> Checkouts { get; } = [];
        public string? Branch { get; set; } = "trunk";

        /// <summary>Thrown by a clone, as libgit2 would. A real one can quote the URL it was given.</summary>
        public string? FailWith { get; set; }

        /// <summary>Written into the clone, for what only the tree can say.</summary>
        public List<string> Files { get; } = [];

        public Task<GitCheckout> CheckoutAsync(string cloneUrl, string gitRef, string? credentialToken, string workingDir, IProgress<string> log, CancellationToken ct)
        {
            Checkouts.Add((cloneUrl, gitRef, credentialToken, workingDir));
            if (FailWith is { } failure) throw new InvalidOperationException(failure);

            Directory.CreateDirectory(workingDir);
            foreach (var file in Files) File.WriteAllText(Path.Combine(workingDir, file), "");
            return Task.FromResult(new GitCheckout("abc1234", "test commit", "tester", workingDir, Branch));
        }

        public Task<IReadOnlyList<GitRef>> ListRefsAsync(string cloneUrl, string? credentialToken, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<GitRef>>([]);
    }

    public sealed class ScriptedReader : ILaunchSourceReader
    {
        public SourceInspection Inspection { get; set; } = SourceInspection.Nothing;
        public List<string> ReadFrom { get; } = [];

        public SourceInspection Read(string root)
        {
            ReadFrom.Add(root);
            return Inspection;
        }
    }

    public sealed class ScriptedVerifier : IAppVerifier
    {
        public int Runs { get; private set; }
        public List<(Guid AppId, AppVerificationReport? Previous, DateTimeOffset GiveUpAt)> Asked { get; } = [];
        public AppVerificationReport? Latest { get; private set; }
        public Func<Guid, AppVerificationReport?>? Next { get; set; }
        public Func<Guid, Exception?>? ThrowFor { get; set; }

        public static AppVerificationReport Report(Guid appId, params AppCheck[] checks) =>
            new(appId, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, checks);

        public Task<AppVerificationReport> RunAsync(Guid appId, AppVerificationReport? previous, DateTimeOffset giveUpAt, CancellationToken ct)
        {
            Runs++;
            Asked.Add((appId, previous, giveUpAt));
            if (ThrowFor?.Invoke(appId) is { } failure) throw failure;

            Latest = Next?.Invoke(appId) ?? Report(appId, new AppCheck("running", AppCheckStatus.Passed, "Running", "در حال اجرا"));
            return Task.FromResult(Latest);
        }

        public Task<AppVerificationReport?> GetLatestAsync(Guid appId, CancellationToken ct) =>
            Task.FromResult(Latest?.AppId == appId ? Latest : null);
    }

    /// <summary>What a test wants the next create to do, across every context the harness builds.</summary>
    public sealed class ScriptedCreates
    {
        public int DatabasesCreated { get; set; }
        public int AppsCreated { get; set; }
        public ServiceRefusal? RefuseSecondDatabase { get; set; }
        public ServiceRefusal? RefuseApp { get; set; }
        public Exception? ThrowFromApp { get; set; }
    }

    private sealed class ScriptedApps(IAppProvisioningService inner, ScriptedCreates script) : IAppProvisioningService
    {
        public Task<IReadOnlyList<ServiceRefusal>> ValidateCreateAsync(AppCreateRequest request, CancellationToken ct) =>
            inner.ValidateCreateAsync(request, ct);

        public async Task<ServiceOutcome<AppCreateResult>> CreateAsync(AppCreateRequest request, CancellationToken ct)
        {
            if (script.ThrowFromApp is { } failure) { script.ThrowFromApp = null; throw failure; }
            if (script.RefuseApp is { } refused) { script.RefuseApp = null; return ServiceOutcome<AppCreateResult>.Refused(refused); }
            script.AppsCreated++;
            return await inner.CreateAsync(request, ct);
        }
    }

    private sealed class ScriptedDatabases(IDatabaseProvisioningService inner, ScriptedCreates script) : IDatabaseProvisioningService
    {
        public Task<IReadOnlyList<ServiceRefusal>> ValidateCreateAsync(DatabaseCreateRequest request, CancellationToken ct) =>
            inner.ValidateCreateAsync(request, ct);

        public async Task<ServiceOutcome<DatabaseCreateResult>> CreateAsync(DatabaseCreateRequest request, CancellationToken ct)
        {
            if (script.DatabasesCreated >= 1 && script.RefuseSecondDatabase is { } refused)
            {
                script.RefuseSecondDatabase = null;
                return ServiceOutcome<DatabaseCreateResult>.Refused(refused);
            }

            script.DatabasesCreated++;
            return await inner.CreateAsync(request, ct);
        }

        public Task<IReadOnlyList<ServiceRefusal>> ValidateRemoveAsync(Guid serviceId, CancellationToken ct) => inner.ValidateRemoveAsync(serviceId, ct);
        public Task<ServiceOutcome<DatabaseRemoveResult>> RemoveAsync(Guid serviceId, bool deleteData, string? ipAddress, CancellationToken ct) =>
            inner.RemoveAsync(serviceId, deleteData, ipAddress, ct);
    }

    private sealed class GatedDeployments(IDeploymentEngine inner, LaunchHarness harness) : IDeploymentEngine
    {
        public Task<Guid> QueueDeploymentAsync(DeploymentRequest request, CancellationToken ct)
        {
            if (harness.RefuseNextDeployment is { } refused)
            {
                harness.RefuseNextDeployment = null;
                throw refused;
            }

            harness.DeploymentsQueued++;
            return inner.QueueDeploymentAsync(request, ct);
        }

        public Task CancelAsync(Guid deploymentId, CancellationToken ct) => inner.CancelAsync(deploymentId, ct);
        public Task ApproveAsync(Guid deploymentId, Guid approverUserId, CancellationToken ct) => inner.ApproveAsync(deploymentId, approverUserId, ct);
        public Task RejectAsync(Guid deploymentId, Guid approverUserId, string reason, CancellationToken ct) => inner.RejectAsync(deploymentId, approverUserId, reason, ct);
    }

    /// <summary>Everything the service logs, so "a token never reaches a log line" can be asserted.</summary>
    public sealed class CapturingLogger : ILogger<LaunchService>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception) + (exception is null ? "" : " " + exception));
    }

    public sealed class SizePickerProbe(HarboraDbContext db, IOptions<BillingOptions> billing)
    {
        public async Task<string?> OpensOnAsync(Guid workspaceId, IReadOnlyList<NodeCapacity>? readings = null)
        {
            var picker = new Harbora.Web.Infrastructure.SizePickerService(db, new NoReadings(readings), billing);
            var model = await picker.BuildAsync(workspaceId, "size", "server", null, null, allowNoLimit: false, null, default);
            return model.SelectedSizeKey;
        }
    }
}

/// <summary>What a tree can be found to need, without building the tree.</summary>
internal static class LaunchFixtures
{
    public const string ExampleFile = ".env.example";

    public static DetectedService Certain(DetectedServiceKind kind) =>
        new(kind, [new ServiceEvidence("package.json", $"\"{kind}\" in dependencies")], Certain: true);

    public static DetectedService Guess(DetectedServiceKind kind) =>
        new(kind, [new ServiceEvidence(ExampleFile, "a name in the example file")], Certain: false);

    public static DetectedEnvVar Var(string name, EnvVarDisposition disposition, string? example = null,
        DetectedServiceKind? service = null, EnvAliasRole? role = null) =>
        new(name, disposition, example, service, role, ExampleFile);

    /// <summary>A plain web app that needs nothing: no database, no variable.</summary>
    public static SourceInspection Bare(int port = 3000) =>
        new(DetectedStack.Node, DetectedAppKind.Web, null, [], port, null, null, [], [], []);

    /// <summary>One of everything: a certain database, a guess, and every kind of variable.</summary>
    public static SourceInspection NodeWithPostgres() => new(
        DetectedStack.Node, DetectedAppKind.Web, null, [], 3000, null, "npx prisma migrate deploy",
        [Certain(DetectedServiceKind.Postgres), Guess(DetectedServiceKind.Redis)],
        [
            Var("DATABASE_URL", EnvVarDisposition.ProvidedByAttach, null, DetectedServiceKind.Postgres),
            Var("DB_HOST", EnvVarDisposition.AliasOfAttach, null, DetectedServiceKind.Postgres, EnvAliasRole.Host),
            Var("JWT_SECRET", EnvVarDisposition.GeneratedSecret),
            Var("LOG_LEVEL", EnvVarDisposition.KeepExample, "info"),
            Var("STRIPE_SECRET_KEY", EnvVarDisposition.NeedsAnswer)
        ],
        []);

    public static SourceInspection TwoDatabases() => Bare() with
    {
        Services = [Certain(DetectedServiceKind.Postgres), Certain(DetectedServiceKind.Redis)]
    };
}
