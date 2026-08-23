using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Infrastructure.Services;

/// <summary>
/// Which apps are actually using a managed database.
///
/// Needed in two places that were each getting it wrong on their own: the architecture view, which
/// drew no connection at all for an attached database, and the delete flow, which removed a database
/// without ever asking who was relying on it.
/// </summary>
public sealed class ServiceUsageService(HarboraDbContext db, ISecretProtector protector)
{
    /// <summary>Apps in this workspace whose environment points at the given service.</summary>
    public async Task<IReadOnlyList<App>> AppsUsingAsync(Guid serviceId, CancellationToken ct)
    {
        var service = await db.ManagedServices.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == serviceId, ct);
        if (service is null) return [];

        var apps = await db.Apps.AsNoTracking()
            .Include(a => a.EnvironmentVariables)
            // C1 (2026-08-22 config-delivery plan): an explicit AppManagedService join is now also
            // "using" a service — see Uses below.
            .Include(a => a.ManagedServices).ThenInclude(ms => ms.ManagedService)
            .Where(a => a.WorkspaceId == service.WorkspaceId)
            .ToListAsync(ct);

        return apps.Where(a => Uses(a, service.ContainerName)).ToList();
    }

    /// <summary>
    /// Which of these services each app connects to, by container name — the architecture view's
    /// question, answered once for the whole page rather than per row.
    /// </summary>
    public IReadOnlyDictionary<Guid, IReadOnlyList<string>> ConnectionsFor(
        IEnumerable<App> apps, IEnumerable<string> containerNames)
    {
        var hosts = containerNames.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().ToList();

        return apps.ToDictionary(
            app => app.Id,
            app => (IReadOnlyList<string>)hosts.Where(h => Uses(app, h)).ToList());
    }

    private bool Uses(App app, string containerName) =>
        app.EnvironmentVariables.Any(v => ServiceUsage.Mentions(Reveal(v), containerName))
        // C1 (2026-08-22 config-delivery plan): DatabasesController.Attach no longer writes any
        // EnvironmentVariable row for a new attach — the connection string is computed live from
        // this join (ConfigGroupMerge/DeploymentPipeline.BuildEnv) — so the heuristic above alone
        // would report a freshly attached app as not using its database at all. A caller whose
        // `app` was not loaded with ManagedServices sees an empty collection here, which degrades to
        // exactly the old heuristic-only behaviour rather than throwing.
        || app.ManagedServices.Any(ms => ms.ManagedService?.ContainerName == containerName);

    /// <summary>
    /// The value as the app will see it.
    ///
    /// A secret that cannot be decrypted falls back to what is stored rather than being skipped,
    /// because the two possible mistakes here are not equal: a missed user is what lets a database
    /// be deleted out from under a running app, while a spurious one only makes a warning too
    /// cautious. A variable flagged secret but written in plain text — the shape of that bug — is
    /// still worth reading.
    /// </summary>
    private string? Reveal(EnvironmentVariable variable)
    {
        if (!variable.IsSecret) return variable.Value;
        try { return protector.Unprotect(variable.Value); }
        catch { return variable.Value; }
    }
}
