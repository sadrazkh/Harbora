using Docker.DotNet;
using Harbora.Application.Abstractions;
using Harbora.Infrastructure.Common;
using Harbora.Infrastructure.Deployments;
using Harbora.Infrastructure.Docker;
using Harbora.Infrastructure.Git;
using Harbora.Infrastructure.Jobs;
using Harbora.Infrastructure.Proxy;
using Harbora.Infrastructure.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Harbora.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// Registers every infrastructure adapter. The host (Web) must additionally register an
    /// <see cref="IDeploymentLogStream"/> (the SignalR-backed one) after calling this.
    /// </summary>
    public static IServiceCollection AddHarboraInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<TraefikOptions>(config.GetSection("Traefik"));
        services.Configure<HarboraRuntimeOptions>(config.GetSection("Runtime"));
        services.Configure<Nodes.NodeAgentControlPlaneOptions>(
            config.GetSection(Nodes.NodeAgentControlPlaneOptions.SectionName));

        // Node agent v1. The registry is a singleton because it holds live sockets; everything that
        // touches the database around them is scoped, as usual.
        services.AddSingleton<Nodes.NodeChannelRegistry>();
        // Also a singleton, and for the same reason: it holds the listeners that carry traffic back
        // down a node's ingress tunnel.
        services.AddSingleton<Nodes.NodeIngressRegistry>();
        services.AddHostedService<Nodes.NodeIngressRebinder>();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<Nodes.NodeCertificateAuthority>();
        services.AddScoped<Nodes.NodeEnrollmentService>();
        services.AddScoped<Nodes.NodeCommandService>();
        services.AddScoped<Nodes.NodeChannelSession>();
        services.AddHostedService<Nodes.NodeHeartbeatMonitor>();
        services.AddHostedService<Nodes.NodeTunnelGateway>();

        // Scheduling onto nodes: the Server projection that makes a node visible to the scheduler,
        // the engine's read of what a node is, and the digest resolution a node insists on.
        services.AddScoped<Nodes.NodeServerLink>();
        services.AddScoped<Nodes.NodeHostFacts>();
        services.AddScoped<Nodes.ImageDigestResolver>();
        services.AddScoped<Nodes.NodeIngressRouter>();

        // Container runtime
        services.AddSingleton<IDockerClient>(_ =>
        {
            var host = Coalesce(config["Docker:Host"], Environment.GetEnvironmentVariable("DOCKER_HOST"))
                       ?? (OperatingSystem.IsWindows() ? "npipe://./pipe/docker_engine" : "unix:///var/run/docker.sock");
            return new DockerClientConfiguration(new Uri(host)).CreateClient();
        });
        services.AddScoped<IDockerEngine, DockerEngine>();
        // Per-server engine resolution (local in-process vs remote agent) for multi-server.
        services.AddScoped<IServerEngineFactory, Docker.ServerEngineFactory>();

        // Source + proxy engines
        services.AddSingleton<IGitService, LibGit2GitService>();
        // The proxy engine renders one file for the whole install, so it reads the platform's routes
        // itself through the catalog rather than being handed a caller's slice of them.
        services.AddSingleton<IRouteCatalog, Proxy.RouteCatalog>();
        services.AddSingleton<IProxyEngine, TraefikProxyEngine>();

        // Git providers (repo import) + webhook processing (deploy on push/tag).
        services.AddScoped<IGitProviderClient, Git.GitProviderClient>();
        services.AddScoped<IGitWebhookProcessor, Git.GitWebhookProcessor>();
        services.AddScoped<IGitOAuthService, Git.GitOAuthService>();

        // Security — resolve the master key fail-closed (ADR-009 / doc 10 §2.2): Production must
        // supply a key; only Development may fall back to the insecure dev key (with a loud warning).
        // Coalesce so a blank appsettings value (the shipped default) falls through to the env var.
        var configuredKey = Coalesce(config["Harbora:MasterKey"],
                                     Environment.GetEnvironmentVariable("HARBORA_MASTER_KEY"));
        var isProduction = !string.Equals(
            Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production",
            "Development", StringComparison.OrdinalIgnoreCase);
        var masterKey = MasterKeyResolver.Resolve(configuredKey, isProduction);
        if (masterKey.UsedDevFallback)
            Console.Error.WriteLine(
                "⚠ HARBORA_MASTER_KEY is not set — using the INSECURE development key. " +
                "Never run Production like this.");
        services.AddSingleton<ISecretProtector>(new AesGcmSecretProtector(masterKey.Key));
        services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
        services.AddSingleton<ISecretRedactor, SecretRedactor>();
        services.AddScoped<ITokenService, TokenService>();
        services.AddScoped<IAuditLogger, Auditing.AuditLogger>();
        // "May this person do this here" — asked the same way by every screen.
        services.AddScoped<Security.ProjectAccessService>();

        // Platform services
        services.AddSingleton<ISystemClock, SystemClock>();
        // Durable job queue (P3): work is persisted before it runs, so a restart resumes from the
        // database rather than losing whatever was in memory.
        services.AddSingleton<IJobCancellationRegistry, JobCancellationRegistry>();
        services.AddSingleton<JobSignal>();
        // Holds the worker's claim loop until every startup reconciler below has finished. The
        // worker is a BackgroundService, so its StartAsync returns immediately and it would
        // otherwise be claiming work while the reconcilers are still deciding what that work means.
        services.AddSingleton<JobStartupGate>();
        // How much of the platform's background work may happen at once. One reproduces the worker
        // the platform ran before jobs went parallel, and is the rollback path.
        services.Configure<JobQueueOptions>(config.GetSection(JobQueueOptions.SectionName));
        services.AddScoped<IJobQueue, DatabaseJobQueue>();
        // Settles jobs orphaned by a crash BEFORE deployments are reconciled — order matters.
        services.AddHostedService<JobReconciler>();
        services.AddHostedService<JobWorker>();

        // Deployment engine
        services.AddScoped<IDeploymentEngine, DeploymentEngine>();
        services.AddScoped<DeploymentPipeline>();
        services.AddScoped<IAppOperationsService, AppOperationsService>();
        services.AddScoped<IRollbackPlanner, Deployments.RollbackPlanner>();
        // Remote-node host ports are reserved, not guessed (see HostPortRange).
        services.AddScoped<Deployments.HostPortAllocator>();
        // Scheduled jobs: each run is a short-lived container, and the history it leaves is the point.
        // The run itself is registered on its own too — the schedule and the "run now" button
        // share one path, so a job tested by hand behaves exactly as it will at 03:00.
        services.AddScoped<Deployments.CronJobRunner>();
        services.AddHostedService<Deployments.CronRunner>();
        // Crash recovery: reconcile in-flight deployments on startup (ADR-005).
        services.AddHostedService<Deployments.DeploymentReconciler>();
        // Lets the job worker start claiming. Hosted services start in registration order, so every
        // startup reconciler — including any added later — must be registered ABOVE this line; that
        // is the whole guarantee, and it is on the reconciler's registration, not on this one.
        services.AddHostedService<JobStartupGateOpener>();

        // Managed services (databases/caches). Concrete type is registered too so background
        // jobs can resolve ProvisionAsync directly.
        services.AddScoped<Services.ManagedServiceEngine>();
        // Who is actually using a database — needed before deleting one, and by the architecture view.
        services.AddScoped<Services.ServiceUsageService>();
        services.AddScoped<Storage.VolumeFileService>();
        services.Configure<Storage.ObjectStorageOptions>(
            config.GetSection(Storage.ObjectStorageOptions.SectionName));
        services.AddScoped<Storage.ObjectStorageAdmin>();
        services.AddScoped<Storage.BucketObjectService>();
        services.AddScoped<Projects.EnvironmentCloner>();
        services.AddScoped<IManagedServiceEngine>(sp => sp.GetRequiredService<Services.ManagedServiceEngine>());

        // Backups (config + volume/db), storage (local + S3), and the schedule runner.
        services.Configure<Backups.BackupOptions>(config.GetSection("Backups"));
        services.AddSingleton<Backups.ArtifactRelayRegistry>();
        services.Configure<Terminals.TerminalFeatureOptions>(
            config.GetSection(Terminals.TerminalFeatureOptions.SectionName));
        // BackupStorage executes SFTP transfers through IDockerEngine, which is scoped because its
        // connection options may be resolved per request/node. Registering the storage adapter as a
        // singleton captured that scoped engine and made Development startup fail DI validation.
        services.AddScoped<IBackupStorage, Backups.BackupStorage>();
        // Idempotency-Key handling, shared by every module's API. Platform-level rather than in one
        // module, so a second module does not have to depend on the first to reuse the table.
        services.AddScoped<IIdempotencyStore, Common.IdempotencyStore>();
        services.AddScoped<Backups.BackupEngine>();
        // Sends a copy of each finished backup to Telegram/email, alongside the stored artifact.
        services.AddScoped<Backups.BackupDeliveryService>();
        // Runs before migrations at startup: an upgrade of an existing install gets a restore point.
        services.AddScoped<Backups.UpgradeSafetyService>();
        services.AddScoped<IBackupEngine>(sp => sp.GetRequiredService<Backups.BackupEngine>());
        services.AddHostedService<Backups.BackupScheduler>();
        // Checks backups on its own, so a backup that will not restore is found on an ordinary
        // afternoon rather than during an incident.
        services.AddHostedService<Backups.BackupVerifier>();

        // What the dashboard opens with: findings a person can act on, from stored facts only.
        services.AddScoped<Dashboard.AttentionService>();

        // Projects + environments: the grouping every screen and the private network hang off.
        services.AddScoped<Projects.ProjectService>();
        services.AddScoped<Security.WorkspaceAccountService>();
        services.AddScoped<Security.AccountSessionService>();
        services.AddScoped<Templates.TemplateDeploymentService>();
        // A branch gets an environment of its own, and loses it again when the branch goes.
        services.AddScoped<Projects.PreviewEnvironmentService>();
        // Branches get abandoned rather than deleted, so the webhook alone would leak services.
        services.AddHostedService<Projects.PreviewSweeper>();

        // Explaining a failed deployment, behind a flag and an administrator's own API key. The
        // redaction boundary it depends on is a pure rule — see AssistantRedaction.
        services.AddScoped<Assistant.AssistantClient>();
        services.AddScoped<Assistant.AssistantService>();

        // Turns the stored cumulative counters into a rate — see NetworkThroughput for why a
        // restart has to read as a gap rather than a spike.
        services.AddScoped<Monitoring.NetworkHistory>();

        // The AI gateway. The adapter is registered against the interface so a second provider
        // shape can be added without touching the routing or metering above it.
        services.AddScoped<Ai.AiGatewayService>();
        services.AddScoped<Ai.IAiProviderAdapter, Ai.OpenRouterProviderAdapter>();

        // A singleton, because it is the counter. Scoped, every request would get a fresh one and
        // every limit would be judged against a history of exactly one request — a rate limiter that
        // is present, tested, configured and enforces nothing.
        services.AddSingleton<Ai.AiRateLimiter>();

        // Newer versions of the ready-made apps. The background job checks a setting before it does
        // anything, so registering it does not start talking to registries.
        services.AddScoped<IContainerRegistry, Templates.ContainerRegistryClient>();

        // Registered by its own type and then handed to the host, rather than AddHostedService<T>.
        // That overload registers it only as IHostedService, so the admin page's "check now" button
        // would fail to resolve it — at runtime, on a page that compiles and looks finished.
        services.AddSingleton<Templates.RegistryDiscoveryService>();
        services.AddHostedService(sp => sp.GetRequiredService<Templates.RegistryDiscoveryService>());

        // Outside access to a managed database, and the sweeper that makes "temporary" true.
        // The gateway and the grant executor are what turn it from a contract into an open port:
        // on a single-server install the control plane already talks to the same Docker daemon the
        // databases run on, which was true the whole time this was documented as blocked on a node
        // agent that has not shipped.
        services.AddScoped<Services.DockerTcpGateway>();
        services.AddScoped<Services.DatabaseGrantExecutor>();
        services.AddScoped<Services.DatabaseAccessService>();
        services.AddHostedService<Services.DatabaseAccessSweeper>();
        services.AddHostedService<Storage.BucketMeasurementSweeper>();
        services.AddScoped<Maintenance.DiskCleanupService>();
        services.AddScoped<Notifications.PlatformMailer>();
        services.AddHostedService<Maintenance.UpdateCheckService>();
        // Bounds the seven tables that had no retention at all — build logs first among them. A
        // nightly timer, and so deliberately BELOW JobStartupGateOpener: it is not a startup
        // reconciler, it settles nothing the job worker is waiting on, and putting a delete pass on
        // the boot path would hold the worker behind it for no benefit.
        services.Configure<Maintenance.RetentionOptions>(
            config.GetSection(Maintenance.RetentionOptions.SectionName));
        services.AddHostedService<Maintenance.DataRetentionSweeper>();

        // The durable scheduler queues every ended UTC hour and retries incomplete accounting runs.
        services.Configure<Billing.BillingOptions>(config.GetSection(Billing.BillingOptions.SectionName));
        services.AddScoped<Billing.BillingTick>();
        services.AddScoped<Billing.BillingRunHandler>();
        services.AddScoped<Billing.BillingRunRetryService>();
        services.AddHostedService<Billing.BillingScheduler>();
        services.AddScoped<Billing.ResourceCreationBilling>();
        services.AddScoped<Billing.WorkspaceBudgetService>();
        services.AddScoped<Mail.StalwartClient>();
        services.AddScoped<Mail.MailPlatformService>();
        // Stopping what a workspace is running once its balance is gone, and bringing back exactly
        // what that stop took away. Registered beside the tick and, like it, scheduled by nothing
        // yet; it refuses to suspend anybody at all while Billing:Enabled is false.
        services.AddScoped<Billing.BillingSuspension>();
        // Money in, and the bill that says where the money went. Unlike the three above it is NOT
        // switched off by Billing:Enabled: taking a payment and showing somebody what they were
        // charged neither costs a customer money nor stops their workloads, and an install that
        // switched billing off after a suspension must still be able to lift it.
        services.AddScoped<Billing.WalletService>();
        services.AddScoped<Billing.VoucherService>();
        // The gate every start path asks before a container runs. Registered here rather than beside
        // the deployment engine because the rule it holds is a billing rule, and a second copy of it
        // living next to the thing it refuses is how a rule quietly stops being one. Like the tick
        // and the suspension it refuses nothing at all while Billing:Enabled is false.
        services.AddScoped<Application.Abstractions.IBillingGate, Billing.BillingGate>();
        services.AddScoped<Services.AdminerService>();
        services.AddHostedService<Services.AdminerSweeper>();

        // Until the real agent ships, the fake stands in — and warns on every call, so a production
        // deployment that never configured an agent cannot quietly report tunnels it never made.
        services.AddSingleton<Application.Abstractions.INodeAgentClient, Nodes.FakeNodeAgentClient>();

        // Tenancy quotas + node capacity (PaaS).
        services.AddScoped<IQuotaService, Tenancy.QuotaService>();
        services.AddScoped<INodeCapacityService, Tenancy.NodeCapacityService>();
        services.AddScoped<ISchedulerService, Tenancy.SchedulerService>();
        services.AddHostedService<Tenancy.MeteringService>();
        // Measures one volume at a time, so the disk quota is checked against something real.
        services.AddHostedService<Tenancy.StorageMeasurer>();

        // Tells the truth about custom domains: where DNS points and what certificate is live.
        services.AddScoped<IDomainInspector, Networking.DomainInspector>();
        services.AddScoped<Networking.CloudflarePlatformService>();
        // The one place every app-creation path asks what hostname an app should get.
        services.AddScoped<Networking.AppAddressAssigner>();

        // Monitoring + notifications.
        services.AddHttpClient();
        services.Configure<Notifications.NotificationOptions>(config.GetSection("Notifications"));
        services.AddScoped<INotificationService, Notifications.NotificationService>();
        // Survives the collector's per-pass scope, so a recurring condition alerts once per interval.
        services.AddSingleton<Monitoring.AlertThrottle>();
        // Summarises finished hours and days so history outlives the raw samples.
        services.AddScoped<Monitoring.MetricsRollupService>();
        services.AddScoped<IMetricsCollector, Monitoring.MetricsCollector>();
        services.AddHostedService<Monitoring.MetricsCollectorHostedService>();
        // Raises the SSL-expiry alert the rule engine has always offered but nothing ever fired.
        services.AddHostedService<Monitoring.CertificateWatcher>();

        return services;
    }

    private static string? Coalesce(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}
