using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Deployments;
using Harbora.Domain.Networking;
using Harbora.Domain.Services;
using Harbora.Domain.Identity;
using Harbora.Infrastructure.Deployments;
using Harbora.Infrastructure.Security;
using Harbora.Infrastructure.Verification;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Environment = Harbora.Domain.Projects.Environment;
using Project = Harbora.Domain.Projects.Project;

namespace Harbora.Tests.Verification;

/// <summary>A connection test that says whatever the test needs it to, and counts how often it was asked.</summary>
internal sealed class ScriptedServices : IManagedServiceEngine
{
    public List<Guid> Tested { get; } = [];

    /// <summary>Null means it answered. The default is a database that answers.</summary>
    public Func<Guid, CancellationToken, Task<string?>> Test { get; set; } = (_, _) => Task.FromResult<string?>(null);

    public Task<string?> TestConnectionAsync(Guid serviceId, CancellationToken ct)
    {
        Tested.Add(serviceId);
        return Test(serviceId, ct);
    }

    public IReadOnlyList<ServiceCatalogEntry> Catalog => [];
    public Task QueueProvisionAsync(Guid serviceId, CancellationToken ct) => throw new NotSupportedException();
    public Task StartAsync(Guid serviceId, CancellationToken ct) => throw new NotSupportedException();
    public Task StopAsync(Guid serviceId, CancellationToken ct) => throw new NotSupportedException();
    public Task RemoveAsync(Guid serviceId, bool deleteData, CancellationToken ct) => throw new NotSupportedException();
    public Task<long?> MeasureStorageAsync(Guid serviceId, CancellationToken ct) => throw new NotSupportedException();
    public Task<IReadOnlyList<RotatedApp>> RotatePasswordAsync(Guid serviceId, CancellationToken ct) => throw new NotSupportedException();
    public Task<ServiceConnectionInfo> GetConnectionInfoAsync(Guid serviceId, CancellationToken ct) => throw new NotSupportedException();
    public Task<IReadOnlyDictionary<string, string>> BuildAttachEnvAsync(Guid serviceId, CancellationToken ct) => throw new NotSupportedException();
    public Task<RedisMemoryPolicyOutcome> UpdateRedisMemoryPolicyAsync(Guid serviceId, string? policy, long maxMemoryBytes, CancellationToken ct) => throw new NotSupportedException();
    public Task<(bool Ok, string? Error)> PromoteReplicaAsync(Guid replicaId, CancellationToken ct) => throw new NotSupportedException();
}

/// <summary>An outside-in probe that answers what it is told and remembers what it was asked.</summary>
internal sealed class ScriptedProbe : IUptimeProbe
{
    public List<(Uri Url, int ExpectedStatus)> Asked { get; } = [];

    public UptimeProbeResult Answer { get; set; } = new(ProbeOutcome.Up, 200, 5, "answered 200.");

    public Task<UptimeProbeResult> ProbeAsync(
        Uri url, int expectedStatus, string? bodyContains, TimeSpan timeout, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Asked.Add((url, expectedStatus));
        return Task.FromResult(Answer);
    }
}

/// <summary>
/// A real <see cref="FakeDockerEngine"/> with some of its methods answering differently, for the
/// engines this platform really has: a v1 node that refuses a verb, a daemon that hangs.
/// </summary>
internal static class EngineWith
{
    public static IDockerEngine Overrides(
        IDockerEngine inner, params (string Method, Func<object?[], object?> Answer)[] overrides)
    {
        var byName = overrides.ToDictionary(o => o.Method, o => o.Answer);
        return DispatchStandIn.Create<IDockerEngine>((method, args) =>
        {
            if (byName.TryGetValue(method.Name, out var answer)) return answer(args);
            try
            {
                return method.Invoke(inner, args);
            }
            catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
        });
    }

    /// <summary>Never finishes until it is cancelled, which is what a hung daemon's HTTP request does.</summary>
    public static async Task<T> HangAsync<T>(CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct);
        throw new InvalidOperationException("A delay with no end ended.");
    }
}

/// <summary>
/// One workspace with one environment, and everything <see cref="AppVerifier"/> reaches outside the
/// database as a double. Unscoped by default, which is exactly what the launch tick is.
/// </summary>
internal sealed class VerifierHarness
{
    public static readonly DateTimeOffset Now = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

    private readonly string _store = "verifier-" + Guid.NewGuid();

    public HarboraDbContext Db { get; }
    public Guid WorkspaceId { get; } = Guid.CreateVersion7();
    public Guid EnvironmentId { get; } = Guid.CreateVersion7();
    public Guid ServerId { get; } = Guid.CreateVersion7();

    public FakeDockerEngine Docker { get; } = new();
    public FakeServerEngineFactory Engines { get; }
    public ScriptedServices Services { get; } = new();
    public ScriptedProbe Probe { get; } = new();
    public FixedClock Clock { get; } = new(Now);
    public PassthroughProtector Protector { get; } = new();

    public VerifierHarness(IWorkspaceScope? scope = null)
    {
        Db = Open(scope ?? SystemWorkspaceScope.Instance);
        Engines = new FakeServerEngineFactory(Docker);

        var projectId = Guid.CreateVersion7();
        Db.Workspaces.Add(new Workspace { Id = WorkspaceId, Name = "Acme", Slug = "acme" });
        Db.Projects.Add(new Project { Id = projectId, WorkspaceId = WorkspaceId, Name = "Shop", Slug = "shop" });
        Db.Environments.Add(new Environment
        {
            Id = EnvironmentId, WorkspaceId = WorkspaceId, ProjectId = projectId,
            Name = "Production", Slug = "production", IsDefault = true
        });
        Db.SaveChanges();
    }

    /// <summary>The same store seen through another scope: a panel request, or another tenant's.</summary>
    public HarboraDbContext Open(IWorkspaceScope scope) =>
        new(new DbContextOptionsBuilder<HarboraDbContext>().UseInMemoryDatabase(_store).Options, scope);

    public AppVerifier Verifier(
        TimeSpan? connectionTestTimeout = null, TimeSpan? servicesBudget = null, TimeSpan? engineTimeout = null) =>
        new(Db, Engines, Services, Probe, Protector, new SecretRedactor(), Clock, NullLogger<AppVerifier>.Instance)
        {
            ConnectionTestTimeout = connectionTestTimeout ?? TimeSpan.FromMinutes(2),
            ServicesBudget = servicesBudget ?? TimeSpan.FromMinutes(5),
            EngineTimeout = engineTimeout ?? TimeSpan.FromSeconds(20)
        };

    /// <summary>An app with nothing deployed.</summary>
    public App GivenApp(string slug = "shop", ServiceKind kind = ServiceKind.Web, Action<App>? configure = null)
    {
        var app = new App
        {
            WorkspaceId = WorkspaceId,
            ServerId = ServerId,
            EnvironmentId = EnvironmentId,
            Name = slug,
            Slug = slug,
            Kind = kind,
            SourceType = AppSourceType.PrebuiltImage,
            PrebuiltImage = "ghcr.io/example/shop:1.0",
            Status = AppStatus.Running
        };
        configure?.Invoke(app);
        Db.Apps.Add(app);
        Db.SaveChanges();
        return app;
    }

    /// <summary>A deployment that went live, with its container running and up for an hour.</summary>
    public App GivenDeployedApp(string slug = "shop", ServiceKind kind = ServiceKind.Web, Action<App>? configure = null)
    {
        var app = GivenApp(slug, kind, configure);
        var deployment = GivenActiveDeployment(app);
        GivenContainer(ContainerOf(app, deployment.Number), app);
        return app;
    }

    public Deployment GivenActiveDeployment(App app, int number = 7)
    {
        var deployment = new Deployment
        {
            AppId = app.Id, WorkspaceId = app.WorkspaceId, Number = number, Status = DeploymentStatus.Succeeded
        };
        Db.Deployments.Add(deployment);
        app.ActiveDeploymentId = deployment.Id;
        Db.SaveChanges();
        return deployment;
    }

    public string GivenContainer(
        string name, App app, string state = "running", int? restartCount = 0, DateTimeOffset? startedAt = null,
        string? composeService = null)
    {
        Docker.SeedContainer(name, app.Slug, state, composeService: composeService, appId: app.Id, workspaceId: app.WorkspaceId);
        Docker.SeedDetail(name, new ContainerDetail(
            name, name, "img", null, state, "Up 1 hour", true, restartCount, startedAt ?? Now.AddHours(-1)));
        return name;
    }

    public static string ContainerOf(App app, int number = 7) =>
        DeploymentPlanning.ContainerName(app.WorkspaceId, app.Slug, number);

    public ManagedService GivenService(
        App app, string name = "orders", ManagedServiceType type = ManagedServiceType.PostgreSql,
        ServiceStatus status = ServiceStatus.Running, bool unpublished = false, string password = "db-password-value",
        Action<ManagedService>? configure = null)
    {
        var service = new ManagedService
        {
            WorkspaceId = app.WorkspaceId,
            EnvironmentId = app.EnvironmentId,
            ServerId = app.ServerId,
            Name = name,
            Type = type,
            Version = "16",
            Status = status,
            ContainerName = $"harbora-svc-{name}",
            VolumeName = $"harbora-svc-{name}-data",
            Username = "app",
            EncryptedPassword = Protector.Protect(password)
        };
        configure?.Invoke(service);
        Db.ManagedServices.Add(service);
        Db.AppManagedServices.Add(new AppManagedService
        {
            AppId = app.Id,
            ManagedServiceId = service.Id,
            Alias = name,
            AttachOrder = Db.AppManagedServices.IgnoreQueryFilters().Count(a => a.AppId == app.Id) + 1,
            HasUnpublishedChanges = unpublished
        });
        Db.SaveChanges();
        return service;
    }

    public DomainName GivenDomain(App app, string host = "shop.example.com", bool primary = true, bool ssl = true)
    {
        var domain = new DomainName { AppId = app.Id, Host = host, IsPrimary = primary, SslEnabled = ssl };
        Db.Domains.Add(domain);
        Db.SaveChanges();
        return domain;
    }

    public void GivenSecret(App app, string key, string value)
    {
        Db.Set<EnvironmentVariable>().Add(new EnvironmentVariable
        {
            AppId = app.Id, Key = key, Value = Protector.Protect(value), IsSecret = true
        });
        Db.SaveChanges();
    }
}
