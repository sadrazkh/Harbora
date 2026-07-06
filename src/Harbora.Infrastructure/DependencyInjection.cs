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

        // Source + proxy engines
        services.AddSingleton<IGitService, LibGit2GitService>();
        services.AddSingleton<IProxyEngine, TraefikProxyEngine>();

        // Git providers (repo import) + webhook processing (deploy on push/tag).
        services.AddScoped<IGitProviderClient, Git.GitProviderClient>();
        services.AddScoped<IGitWebhookProcessor, Git.GitWebhookProcessor>();

        // Security
        var masterKey = config["Harbora:MasterKey"]
                        ?? Environment.GetEnvironmentVariable("HARBORA_MASTER_KEY")
                        ?? "dev-insecure-master-key-change-me";
        services.AddSingleton<ISecretProtector>(new AesGcmSecretProtector(masterKey));
        services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
        services.AddSingleton<ISecretRedactor, SecretRedactor>();
        services.AddScoped<ITokenService, TokenService>();

        // Platform services
        services.AddSingleton<ISystemClock, SystemClock>();
        services.AddSingleton<IBackgroundJobQueue, ChannelBackgroundJobQueue>();
        services.AddHostedService<BackgroundJobWorker>();

        // Deployment engine
        services.AddScoped<IDeploymentEngine, DeploymentEngine>();
        services.AddScoped<DeploymentPipeline>();

        // Managed services (databases/caches). Concrete type is registered too so background
        // jobs can resolve ProvisionAsync directly.
        services.AddScoped<Services.ManagedServiceEngine>();
        services.AddScoped<IManagedServiceEngine>(sp => sp.GetRequiredService<Services.ManagedServiceEngine>());

        // Backups (config + volume/db), storage (local + S3), and the schedule runner.
        services.Configure<Backups.BackupOptions>(config.GetSection("Backups"));
        services.AddSingleton<IBackupStorage, Backups.BackupStorage>();
        services.AddScoped<Backups.BackupEngine>();
        services.AddScoped<IBackupEngine>(sp => sp.GetRequiredService<Backups.BackupEngine>());
        services.AddHostedService<Backups.BackupScheduler>();

        // Monitoring + notifications.
        services.AddHttpClient();
        services.AddScoped<INotificationService, Notifications.NotificationService>();
        services.AddScoped<IMetricsCollector, Monitoring.MetricsCollector>();
        services.AddHostedService<Monitoring.MetricsCollectorHostedService>();

        return services;
    }

    private static string? Coalesce(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}
