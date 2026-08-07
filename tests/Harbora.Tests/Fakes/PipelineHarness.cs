using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Deployments;
using Harbora.Domain.Git;
using Harbora.Domain.Identity;
using Harbora.Domain.Networking;
using Harbora.Domain.Servers;
using Harbora.Infrastructure.Deployments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Harbora.Tests.Fakes;

/// <summary>
/// Builds a REAL <see cref="DeploymentPipeline"/> over fake infrastructure, plus the seeded
/// workspace/server/app graph it needs. Everything except the container runtime, git and the proxy
/// is genuine — including the state machine, the EF context and the cutover ordering under test.
/// </summary>
public sealed class PipelineHarness : IDisposable
{
    public HarboraDbContext Db { get; }
    public FakeDockerEngine Docker { get; } = new();
    public RecordingProxyEngine Proxy { get; }
    public RecordingLogStream Stream { get; } = new();
    public RecordingNotificationService Notifications { get; } = new();
    public StubHttpClientFactory Http { get; } = new();
    public FakeGitService Git { get; }
    public FixedClock Clock { get; } = new();
    public HarboraRuntimeOptions Options { get; }
    public PassthroughProtector Protector { get; } = new();

    public Workspace Workspace { get; }
    public Server Server { get; }
    public App App { get; }
    public Harbora.Domain.Projects.Project Project { get; }
    public Harbora.Domain.Projects.Environment Environment { get; }

    private readonly string _workDir;

    public PipelineHarness(bool localServer = true, AppSourceType sourceType = AppSourceType.PrebuiltImage)
    {
        _workDir = Path.Combine(Path.GetTempPath(), "harbora-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDir);
        Git = new FakeGitService(_workDir);

        Options = new HarboraRuntimeOptions
        {
            WorkDir = _workDir,
            ImagePrefix = "harbora",
            // No real waiting: the ordering guarantees under test are unaffected by wall-clock delay,
            // and a 2s poll would make the suite unusable.
            HealthPollIntervalSeconds = 0,
            HealthRunningAttempts = 3,
            HealthHttpAttempts = 3
        };

        Db = new HarboraDbContext(new DbContextOptionsBuilder<HarboraDbContext>()
            .UseInMemoryDatabase("pipeline-" + Guid.NewGuid()).Options);
        // Reads the platform's stored routes, unfiltered, exactly as the real engine does — a test
        // asserting on Applications is then looking at the config that would have been published.
        Proxy = new RecordingProxyEngine(() => Db.Routes.IgnoreQueryFilters().AsNoTracking().ToList());

        Workspace = new Workspace { Id = Guid.NewGuid(), Name = "Acme", Slug = "acme" };
        Server = new Server
        {
            Id = Guid.NewGuid(), Name = localServer ? "local" : "node-2",
            Hostname = localServer ? "localhost" : "node2.internal", IsLocal = localServer
        };
        // Every service has lived inside a project and environment since they were introduced, and
        // the network it is deployed onto is derived from them — so the harness has them too.
        Project = new Harbora.Domain.Projects.Project
        {
            Id = Guid.NewGuid(), WorkspaceId = Workspace.Id, Name = "Blog", Slug = "blog"
        };
        Environment = new Harbora.Domain.Projects.Environment
        {
            Id = Guid.NewGuid(), WorkspaceId = Workspace.Id, ProjectId = Project.Id,
            Name = "Production", Slug = "production", IsDefault = true
        };
        App = new App
        {
            Id = Guid.NewGuid(), WorkspaceId = Workspace.Id, ServerId = Server.Id,
            EnvironmentId = Environment.Id,
            Name = "Blog", Slug = "blog", SourceType = sourceType,
            PrebuiltImage = sourceType == AppSourceType.PrebuiltImage ? "nginx:1.27" : null,
            ContainerPort = 8080, HealthCheckPath = null
        };

        Db.Workspaces.Add(Workspace);
        Db.Servers.Add(Server);
        Db.Projects.Add(Project);
        Db.Environments.Add(Environment);
        Db.Apps.Add(App);
        Db.SaveChanges();
    }

    /// <summary>
    /// Attaches a domain so the proxy-wiring stage actually runs. An app may carry several, so
    /// <paramref name="primary"/> is how a test says which one the verification probe should ask for.
    /// </summary>
    public PipelineHarness WithDomain(string host = "blog.example.com", bool primary = false)
    {
        Db.Domains.Add(new DomainName { Id = Guid.NewGuid(), AppId = App.Id, Host = host, IsPrimary = primary });
        Db.SaveChanges();
        return this;
    }

    /// <summary>Switches the app to a Git source with a fully-formed provider + repository.</summary>
    public PipelineHarness WithGitSource(string fullName = "acme/blog", string defaultBranch = "main")
    {
        var provider = new GitProvider
        {
            Id = Guid.NewGuid(), WorkspaceId = Workspace.Id, Name = "GitHub",
            ApiBaseUrl = "https://api.github.com"
        };
        var repository = new GitRepository
        {
            Id = Guid.NewGuid(), GitProviderId = provider.Id, FullName = fullName,
            CloneUrl = $"https://example.com/{fullName}.git", DefaultBranch = defaultBranch
        };
        Db.GitProviders.Add(provider);
        Db.GitRepositories.Add(repository);

        App.SourceType = AppSourceType.GitRepository;
        App.GitRepositoryId = repository.Id;
        App.PrebuiltImage = null;
        Db.SaveChanges();
        return this;
    }

    /// <summary>Writes a Dockerfile into the fake checkout so the build stage is reached.</summary>
    public PipelineHarness WithDockerfile()
    {
        File.WriteAllText(Path.Combine(_workDir, "Dockerfile"), "FROM scratch\n");
        return this;
    }

    /// <summary>Turns on the HTTP health probe (off by default so tests opt in explicitly).</summary>
    public PipelineHarness WithHealthPath(string path = "/healthz")
    {
        App.HealthCheckPath = path;
        Db.SaveChanges();
        return this;
    }

    /// <summary>
    /// Records a previous successful deployment and the container it left running — the state a
    /// zero-downtime deploy must not disturb until cutover.
    /// </summary>
    public Deployment WithPreviousDeployment(int number = 1, string image = "harbora/blog:build-1")
    {
        var previous = new Deployment
        {
            Id = Guid.NewGuid(), AppId = App.Id, Number = number,
            Status = DeploymentStatus.Succeeded, ImageTag = image,
            CreatedAt = Clock.UtcNow, FinishedAt = Clock.UtcNow
        };
        Db.Deployments.Add(previous);
        App.ActiveDeploymentId = previous.Id;
        App.Status = AppStatus.Running;
        Db.SaveChanges();

        Docker.SeedContainer(DeploymentPlanning.ContainerName(App.Slug, number), App.Slug, image: image);
        // A deployment that really ran left its image on the node too — rollback depends on it.
        if (!string.IsNullOrWhiteSpace(image)) Docker.SeedImage(image);
        return previous;
    }

    /// <summary>
    /// Adds a past successful deployment to the history WITHOUT making it active — used to build up
    /// enough history for retention windows to be meaningful.
    /// </summary>
    public Deployment SeedSucceededDeployment(int number, string? image)
    {
        var deployment = new Deployment
        {
            Id = Guid.NewGuid(), AppId = App.Id, Number = number,
            Status = DeploymentStatus.Succeeded, ImageTag = image,
            CreatedAt = Clock.UtcNow, FinishedAt = Clock.UtcNow
        };
        Db.Deployments.Add(deployment);
        Db.SaveChanges();
        return deployment;
    }

    /// <summary>Queues a deployment row the pipeline can pick up (bypassing the engine).</summary>
    public Deployment QueueDeployment(int number = 2, Guid? rollbackTo = null)
    {
        var deployment = new Deployment
        {
            Id = Guid.NewGuid(), AppId = App.Id, Number = number,
            Status = DeploymentStatus.Queued,
            Trigger = rollbackTo is null ? DeploymentTrigger.Manual : DeploymentTrigger.Rollback,
            RolledBackFromId = rollbackTo,
            CreatedAt = Clock.UtcNow
        };
        Db.Deployments.Add(deployment);
        Db.SaveChanges();
        return deployment;
    }

    /// <summary>Configures the app with a port, so a test can make it disagree with the image.</summary>
    public PipelineHarness WithContainerPort(int port)
    {
        App.ContainerPort = port;
        Db.SaveChanges();
        return this;
    }

    public DeploymentPipeline BuildPipeline() => new(
        Db,
        new SingleEngineFactory(Docker),
        Git,
        Proxy,
        Stream,
        Protector,
        // The real redactor, not a passthrough: what reaches a stored field or a notification is a
        // guarantee about secrets, and a fake that redacts nothing cannot show it holding.
        new Harbora.Infrastructure.Security.SecretRedactor(),
        Notifications,
        Http,
        Clock,
        Microsoft.Extensions.Options.Options.Create(Options),
        // The real allocator over the same in-memory context: host ports are a database reservation
        // now, so a fake would test the fake rather than the guarantee.
        new HostPortAllocator(Db, Ingress, NullLogger<HostPortAllocator>.Instance),
        // The real router too: whether an app's upstream is the node's own address or a port on the
        // panel is a decision the pipeline makes on every remote deploy, and a fake would answer it
        // for the pipeline rather than let the pipeline be watched answering it.
        new Harbora.Infrastructure.Nodes.NodeIngressRouter(
            Db,
            Ingress,
            new HostPortAllocator(Db, Ingress, NullLogger<HostPortAllocator>.Instance),
            Microsoft.Extensions.Options.Options.Create(new Harbora.Infrastructure.Nodes.NodeAgentControlPlaneOptions()),
            Microsoft.Extensions.Options.Options.Create(Options),
            NullLogger<Harbora.Infrastructure.Nodes.NodeIngressRouter>.Instance),
        NullLogger<DeploymentPipeline>.Instance);

    /// <summary>Shared with the pipeline, so a test can assert on what it bound.</summary>
    public Harbora.Infrastructure.Nodes.NodeIngressRegistry Ingress { get; } = TestIngress.Registry();

    /// <summary>Runs the real pipeline end-to-end for the given deployment.</summary>
    public async Task<Deployment> RunAsync(Deployment deployment)
    {
        await BuildPipeline().ExecuteAsync(deployment.Id, default);
        return await Db.Deployments.AsNoTracking().FirstAsync(d => d.Id == deployment.Id);
    }

    /// <summary>The container name a given deployment number gets.</summary>
    public string ContainerFor(int number) => DeploymentPlanning.ContainerName(App.Slug, number);

    public void Dispose()
    {
        Db.Dispose();
        try { Directory.Delete(_workDir, recursive: true); } catch { /* temp dir — best effort */ }
    }

    private sealed class SingleEngineFactory(FakeDockerEngine engine) : Harbora.Application.Abstractions.IServerEngineFactory
    {
        public Harbora.Application.Abstractions.IDockerEngine Local => engine;
        public Task<Harbora.Application.Abstractions.IDockerEngine> ResolveAsync(Guid serverId, CancellationToken ct)
            => Task.FromResult<Harbora.Application.Abstractions.IDockerEngine>(engine);
    }
}
