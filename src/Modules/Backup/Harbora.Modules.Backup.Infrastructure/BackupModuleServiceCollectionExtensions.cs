using Harbora.Modules.Backup.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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

        return services;
    }
}
