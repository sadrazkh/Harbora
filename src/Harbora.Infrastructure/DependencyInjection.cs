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

        // Managed services (databases/caches). Concrete type is registered too so background
        // jobs can resolve ProvisionAsync directly.
        services.AddScoped<Services.ManagedServiceEngine>();
        // Who is actually using a database — needed before deleting one, and by the architecture view.
        services.AddScoped<Services.ServiceUsageService>();
        services.AddScoped<IManagedServiceEngine>(sp => sp.GetRequiredService<Services.ManagedServiceEngine>());

        // Backups (config + volume/db), storage (local + S3), and the schedule runner.
        services.Configure<Backups.BackupOptions>(config.GetSection("Backups"));
        // BackupStorage executes SFTP transfers through IDockerEngine, which is scoped because its
        // connection options may be resolved per request/node. Registering the storage adapter as a
        // singleton captured that scoped engine and made Development startup fail DI validation.
        services.AddScoped<IBackupStorage, Backups.BackupStorage>();
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

        // Tenancy quotas + node capacity (PaaS).
        services.AddScoped<IQuotaService, Tenancy.QuotaService>();
        services.AddScoped<INodeCapacityService, Tenancy.NodeCapacityService>();
        services.AddScoped<ISchedulerService, Tenancy.SchedulerService>();
        services.AddHostedService<Tenancy.MeteringService>();
        // Measures one volume at a time, so the disk quota is checked against something real.
        services.AddHostedService<Tenancy.StorageMeasurer>();

        // Tells the truth about custom domains: where DNS points and what certificate is live.
        services.AddScoped<IDomainInspector, Networking.DomainInspector>();

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
