using Harbora.Application.Abstractions;
using Harbora.Modules.Backup.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

// Both namespaces declare an IBackupEngine — the platform's target-oriented service and this
// module's storage-engine port (ARCHITECTURE.md § 2). Registering the wrong one would compile only
// by accident, so the intended type is named outright.
using IBackupEngine = Harbora.Modules.Backup.Contracts.IBackupEngine;

namespace Harbora.Modules.Backup.Infrastructure;

/// <summary>
/// The module's single wiring point.
///
/// <para>
/// One call from <c>Program.cs</c>, so enabling or removing the module is a one-line change to the
/// host rather than a hunt through startup.
/// </para>
/// </summary>
public static class BackupModuleServiceCollectionExtensions
{
    /// <summary>
    /// Registers the backup module.
    ///
    /// <para>
    /// Services are registered whether or not <c>Features:Backup</c> is on. The flag governs what
    /// the module <em>does</em> — routes, navigation, scheduled work — not whether its types can be
    /// constructed. Registering conditionally would mean a flag flip needs a different DI graph, and
    /// a mis-set flag would surface as a resolution failure at request time rather than as a feature
    /// simply being off.
    /// </para>
    /// </summary>
    public static IServiceCollection AddBackupModule(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<BackupFeatureOptions>(configuration.GetSection(BackupFeatureOptions.SectionName));
        services.Configure<KopiaOptions>(configuration.GetSection(KopiaOptions.SectionName));
        services.Configure<BackupModuleOptions>(configuration.GetSection(BackupModuleOptions.SectionName));

        // Scoped, not singleton: the redactor accumulates the secrets used in one operation. A
        // singleton would collect every credential the panel has ever handled and mask their
        // substrings in unrelated output.
        services.AddScoped<EngineOutputRedactor>();
        services.AddScoped<IEngineProcessRunner, EngineProcessRunner>();

        services.AddScoped<IRepositoryCredentialReader, RepositoryCredentialReader>();
        services.AddScoped<IRepositoryDestinationFactory, RepositoryDestinationFactory>();

        // Both engines are always registered. Which one serves a repository is decided per row by
        // the resolver, from the Engine column.
        services.AddScoped<IBackupEngine, HarboraNativeBackupEngine>();
        services.AddScoped<IBackupEngine, KopiaBackupEngine>();
        services.AddScoped<IBackupEngineResolver, BackupEngineResolver>();

        services.AddScoped<IBackupTargetResolver, BackupTargetResolver>();
        services.AddScoped<IBackupNotificationService, BackupNotificationService>();
        services.AddScoped<IDatabaseTargetStager, DatabaseTargetStager>();
        services.AddScoped<IDatabaseRestoreExecutor, DatabaseRestoreExecutor>();
        services.AddScoped<IApplicationTargetStager, ApplicationTargetStager>();

        // One provider instance per engine, all served by the same container-based implementation:
        // the per-engine differences live in DatabaseDumpCommands, which is pure and tested there.
        // Mongo and Redis are registered too, so asking for them yields their specific refusal
        // rather than "no provider" — the reason is what tells an operator what to do instead.
        foreach (var engine in Enum.GetValues<DatabaseEngine>())
        {
            var captured = engine;
            services.AddScoped<IDatabaseBackupProvider>(sp => new ContainerDatabaseBackupProvider(
                captured,
                sp.GetRequiredService<Harbora.Application.Abstractions.IDockerEngine>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ContainerDatabaseBackupProvider>>()));
        }
        services.AddScoped<IDatabaseBackupProviderResolver, DatabaseBackupProviderResolver>();

        services.AddScoped<BackupRepositoryService>();
        services.AddScoped<BackupSnapshotService>();
        services.AddScoped<RestoreService>();
        services.AddScoped<BackupRetentionService>();
        services.AddScoped<BackupPolicyService>();

        // Job handlers are resolved by JobDispatcher from the worker's scope. Registered as
        // IJobHandler so the core dispatcher can find them without referencing this module.
        services.AddScoped<IJobHandler, BackupSnapshotJobHandler>();
        services.AddScoped<IJobHandler, BackupRestoreJobHandler>();
        services.AddScoped<IJobHandler, BackupVerifyJobHandler>();
        services.AddScoped<IJobHandler, BackupPruneJobHandler>();
        services.AddScoped<IJobHandler, RepositoryHealthCheckJobHandler>();

        // Checks the flag itself and returns immediately when the module is off, so a disabled
        // module costs one log line rather than a timer ticking for the process's lifetime.
        services.AddHostedService<BackupPolicyScheduler>();

        return services;
    }
}
