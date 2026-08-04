using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Settings;
using Harbora.Domain.Templates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Harbora.Infrastructure.Templates;

/// <summary>
/// Looks for newer tags of the ready-made apps and records them as drafts.
///
/// **Off unless an operator turns it on.** It makes outbound requests to third-party registries from
/// the server, and that is a decision about someone else's infrastructure and someone else's rate
/// limits — not a default. The setting is <c>templates.registry_discovery</c>.
///
/// It never publishes. A tag appearing upstream is not an operator deciding their customers should
/// run it; what this produces is a list for somebody to look at, and the whole feature is pointless
/// unless they can — which is why the version admin page went in at the same time.
/// </summary>
public sealed class RegistryDiscoveryService(
    IServiceScopeFactory scopeFactory,
    ILogger<RegistryDiscoveryService> logger) : BackgroundService
{
    /// <summary>
    /// Once a day. Releases do not appear hourly, and a registry answers anonymous callers on a
    /// quota that a chatty job would spend on nothing.
    /// </summary>
    private static readonly TimeSpan Tick = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Not on the first seconds of startup: a control plane restarting in a loop would otherwise
        // hammer the registries once per restart.
        try { await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(Tick);
        do
        {
            try { await DiscoverAsync(stoppingToken); }
            catch (Exception ex) { logger.LogError(ex, "Registry discovery failed."); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>
    /// One pass over every template. Public so a run can be exercised directly rather than by
    /// waiting a day and hoping.
    /// </summary>
    /// <returns>How many draft versions were added.</returns>
    public async Task<int> DiscoverAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();
        var registry = scope.ServiceProvider.GetRequiredService<IContainerRegistry>();
        var clock = scope.ServiceProvider.GetRequiredService<ISystemClock>();

        var enabled = await db.Settings.IgnoreQueryFilters()
            .Where(s => s.Key == SettingKeys.RegistryDiscoveryEnabled)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(ct);

        if (!string.Equals(enabled, "true", StringComparison.OrdinalIgnoreCase))
            return 0;

        // Unfiltered: this runs on a timer with no session, and the tenant filter would otherwise
        // return an empty catalogue and report a clean run over nothing.
        var templateIds = await db.AppTemplates.IgnoreQueryFilters()
            .Where(t => t.IsBuiltIn)
            .Select(t => t.Id)
            .ToListAsync(ct);

        var added = 0;

        foreach (var templateId in templateIds)
        {
            if (ct.IsCancellationRequested) break;

            try { added += await DiscoverOneAsync(db, registry, clock, templateId, ct); }
            catch (Exception ex)
            {
                // One template that cannot be read must not stop the rest, or a single broken
                // repository freezes discovery for the whole catalogue.
                logger.LogWarning(ex, "Discovery failed for template {Template}.", templateId);
            }
        }

        if (added > 0)
            logger.LogInformation("Discovered {Count} new draft version(s) awaiting review.", added);

        return added;
    }

    private static async Task<int> DiscoverOneAsync(
        HarboraDbContext db, IContainerRegistry registry, ISystemClock clock,
        Guid templateId, CancellationToken ct)
    {
        var existing = await db.AppTemplateVersions.IgnoreQueryFilters()
            .Where(v => v.AppTemplateId == templateId)
            .ToListAsync(ct);

        if (existing.Count == 0) return 0;

        var readable = existing
            .Select(v => (Version: v, Parsed: RegistryTag.Parse(v.Version)))
            .Where(v => v.Parsed is not null)
            .ToList();

        if (readable.Count == 0) return 0;

        var newest = readable.MaxBy(v => v.Parsed!).Version;
        if (string.IsNullOrWhiteSpace(newest.ImageRepository)) return 0;

        var tags = await registry.ListTagsAsync(newest.ImageRepository, ct);
        var candidates = RegistryDiscovery.Candidates(existing, tags);

        var added = 0;
        foreach (var tag in candidates)
        {
            var digest = await registry.ResolveDigestAsync(newest.ImageRepository, tag, ct);

            // No digest, no version. One that cannot be pinned is refused at deploy time anyway, so
            // storing it would produce a row that looks like an option and fails every time it is
            // chosen.
            if (digest is null) continue;

            db.AppTemplateVersions.Add(RegistryDiscovery.Build(newest, tag, digest, clock.UtcNow));
            added++;
        }

        if (added > 0) await db.SaveChangesAsync(ct);
        return added;
    }
}
