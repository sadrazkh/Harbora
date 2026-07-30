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
        // Crash recovery: reconcile in-flight deployments on startup (ADR-005).
        services.AddHostedService<Deployments.DeploymentReconciler>();

        // Managed services (databases/caches). Concrete type is registered too so background
        // jobs can resolve ProvisionAsync directly.
        services.AddScoped<Services.ManagedServiceEngine>();
        services.AddScoped<IManagedServiceEngine>(sp => sp.GetRequiredService<Services.ManagedServiceEngine>());

        // Backups (config + volume/db), storage (local + S3), and the schedule runner.
        services.Configure<Backups.BackupOptions>(config.GetSection("Backups"));
        services.AddSingleton<IBackupStorage, Backups.BackupStorage>();
        services.AddScoped<Backups.BackupEngine>();
        // Runs before migrations at startup: an upgrade of an existing install gets a restore point.
        services.AddScoped<Backups.UpgradeSafetyService>();
        services.AddScoped<IBackupEngine>(sp => sp.GetRequiredService<Backups.BackupEngine>());
        services.AddHostedService<Backups.BackupScheduler>();

        // Tenancy quotas + node capacity (PaaS).
        services.AddScoped<IQuotaService, Tenancy.QuotaService>();
        services.AddScoped<INodeCapacityService, Tenancy.NodeCapacityService>();
        services.AddScoped<ISchedulerService, Tenancy.SchedulerService>();
        services.AddHostedService<Tenancy.MeteringService>();

        // Tells the truth about custom domains: where DNS points and what certificate is live.
        services.AddScoped<IDomainInspector, Networking.DomainInspector>();

        // Monitoring + notifications.
        services.AddHttpClient();
        services.AddScoped<INotificationService, Notifications.NotificationService>();
        // Survives the collector's per-pass scope, so a recurring condition alerts once per interval.
        services.AddSingleton<Monitoring.AlertThrottle>();
        services.AddScoped<IMetricsCollector, Monitoring.MetricsCollector>();
        services.AddHostedService<Monitoring.MetricsCollectorHostedService>();
        // Raises the SSL-expiry alert the rule engine has always offered but nothing ever fired.
        services.AddHostedService<Monitoring.CertificateWatcher>();

        return services;
    }

    private static string? Coalesce(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}
