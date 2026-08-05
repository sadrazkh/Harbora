using Harbora.Shared;
using Harbora.Modules.Sync.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Harbora.Modules.Sync.Infrastructure;

/// <summary>The sync module's single wiring point.</summary>
public static class SyncModuleServiceCollectionExtensions
{
    /// <summary>
    /// Registers the sync module.
    ///
    /// <para>
    /// Like the backup module, services are registered regardless of the flag: <c>Features:Sync</c>
    /// governs what the module does, not whether its types can be constructed.
    /// </para>
    /// </summary>
    public static IServiceCollection AddSyncModule(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<SyncFeatureOptions>(configuration.GetSection(SyncFeatureOptions.SectionName));
        services.Configure<SyncthingOptions>(configuration.GetSection(SyncthingOptions.SectionName));
        services.Configure<SyncModuleOptions>(configuration.GetSection(SyncModuleOptions.SectionName));

        // A named client so the base address and timeout live with the options rather than being
        // repeated at every call site.
        services.AddHttpClient<ISyncEngine, SyncthingSyncEngine>((provider, client) =>
        {
            var options = provider.GetRequiredService<IOptions<SyncthingOptions>>().Value;
            client.BaseAddress = new Uri(options.BaseUrl);
            client.Timeout = options.RequestTimeout;
        });

        services.AddScoped<SyncSpaceService>();
        services.AddHostedService<SyncStatusRefresher>();

        return services;
    }
}
