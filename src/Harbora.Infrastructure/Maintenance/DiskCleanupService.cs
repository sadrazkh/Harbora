using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Infrastructure.Deployments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Harbora.Infrastructure.Maintenance;

/// <summary>What one machine's share of a cleanup run did.</summary>
/// <param name="Skipped">
/// Why this machine was not swept, or null when it was. A server behind a v1 node has no image
/// verbs — listing returns nothing and removal does nothing, both by design — so without this it
/// would report "0 images removed" and read exactly like a machine that was already clean.
/// </param>
public sealed record DiskCleanupServerResult(
    Guid ServerId,
    string ServerName,
    int OrphanRemoved,
    int RetentionRemoved,
    int Failed,
    long? FreedBytes,
    string? Skipped);

/// <summary>What one cleanup run did, in figures a person can check against <c>df</c>.</summary>
/// <param name="OrphanRemoved">Build images of deleted apps that were removed.</param>
/// <param name="RetentionRemoved">Superseded build images of living apps that were removed.</param>
/// <param name="Failed">Removals the engine refused — an image still in use is left alone by contract.</param>
/// <param name="FreedBytes">
/// The disks' own before/after difference, summed over the machines that reported one, or null when
/// none did. Deliberately measured rather than summed from image sizes: layers are shared, so adding
/// up per-image sizes overstates the win, and a cleanup that reports more than it freed is the kind
/// of number that teaches people to distrust the page.
/// </param>
/// <param name="Servers">The same figures per machine, including the ones that were not swept.</param>
public sealed record DiskCleanupResult(
    int OrphanRemoved,
    int RetentionRemoved,
    int Failed,
    long? FreedBytes,
    IReadOnlyList<DiskCleanupServerResult> Servers);

/// <summary>
/// Frees the disk of Harbora's own leftovers, on demand.
///
/// Two sweeps, both over things the platform itself created:
///
/// <list type="number">
/// <item>Build images whose app no longer exists — <see cref="CleanupPlan.OrphanedBuildImages"/>.
/// Nothing else ever removes these, because per-app retention runs inside a deployment and a
/// deleted app deploys nothing.</item>
/// <item>Per-app retention for every living app, the same rule the pipeline runs after a cutover
/// (<see cref="DeploymentPlanning.ImagesToPrune"/>) — reused, not re-implemented, so the button
/// and the pipeline cannot disagree about what rollback keeps. This catches apps that simply have
/// not deployed since the retention setting was lowered.</item>
/// </list>
///
/// <para>
/// Both run once per <b>server</b>, against that server's own engine. They used to run once, against
/// the panel's own Docker, while reading applications from every server — so a build image left on a
/// second machine was never a candidate, and the run reported a figure that described one host as if
/// it described the platform.
/// </para>
///
/// Reads are <c>IgnoreQueryFilters</c> throughout: the protection list must contain every
/// workspace's apps, or cleaning as one tenant would delete another tenant's rollback images.
/// Customer images — anything not under the build prefix — are never candidates at any point.
/// </summary>
public sealed class DiskCleanupService(
    HarboraDbContext db,
    IServerEngineFactory engines,
    IOptions<HarboraRuntimeOptions> options,
    ILogger<DiskCleanupService> logger)
{
    public async Task<DiskCleanupResult> RunAsync(CancellationToken ct)
    {
        var opt = options.Value;

        var apps = await db.Apps.IgnoreQueryFilters()
            .Select(a => new { a.Id, a.Slug, a.ActiveDeploymentId, a.ServerId })
            .ToListAsync(ct);

        // Every server, not only the ones with apps on them. An orphan is by definition an image
        // whose app is gone, so the machine whose last app was deleted is precisely the machine with
        // the most to reclaim and the one a "servers that still have apps" list would never visit.
        var servers = await db.Servers.IgnoreQueryFilters()
            .Select(s => new { s.Id, s.Name })
            .ToListAsync(ct);

        // Plus any server id an app still carries that has no row — an app created before the
        // platform had servers carries Guid.Empty, and the factory answers for an unknown server
        // exactly as it did before this method knew about servers at all: the local machine.
        // Leaving those out would quietly stop pruning those apps' superseded releases.
        var targets = servers.Select(s => (s.Id, s.Name))
            .Concat(apps.Select(a => a.ServerId).Distinct()
                .Where(id => servers.All(s => s.Id != id))
                .Select(id => (Id: id, Name: $"server {id}")))
            .ToList();

        var perServer = new List<DiskCleanupServerResult>(targets.Count);

        foreach (var server in targets)
        {
            ct.ThrowIfCancellationRequested();
            perServer.Add(await SweepAsync(
                server.Id, server.Name,
                apps.Where(a => a.ServerId == server.Id).Select(a => (a.Id, a.Slug, a.ActiveDeploymentId)).ToList(),
                apps.Select(a => a.Slug).ToList(),
                opt, ct));
        }

        var orphanRemoved = perServer.Sum(s => s.OrphanRemoved);
        var retentionRemoved = perServer.Sum(s => s.RetentionRemoved);
        var failed = perServer.Sum(s => s.Failed);

        var measured = perServer.Where(s => s.FreedBytes is not null).Select(s => s.FreedBytes!.Value).ToList();
        long? freed = measured.Count == 0 ? null : measured.Sum();

        var skipped = perServer.Where(s => s.Skipped is not null).ToList();

        logger.LogInformation(
            "Disk cleanup swept {Swept} of {Total} server(s): removed {Orphans} orphaned and {Retention} superseded " +
            "image(s), {Failed} refused, freed {Freed}. Not swept: {Skipped}.",
            perServer.Count - skipped.Count, perServer.Count, orphanRemoved, retentionRemoved, failed,
            freed is { } f ? Tenancy.ByteSize.Measured(f) : "unknown",
            skipped.Count == 0 ? "none" : string.Join("; ", skipped.Select(s => $"{s.ServerName} — {s.Skipped}")));

        return new DiskCleanupResult(orphanRemoved, retentionRemoved, failed, freed, perServer);
    }

    /// <summary>
    /// One machine's sweep. The candidates are the images on <em>that</em> host; the protection list
    /// is every app on the platform, because an app moved between servers must not have the images
    /// on the machine it left treated as nobody's.
    /// </summary>
    private async Task<DiskCleanupServerResult> SweepAsync(
        Guid serverId,
        string serverName,
        IReadOnlyList<(Guid Id, string Slug, Guid? ActiveDeploymentId)> appsHere,
        IReadOnlyList<string> everySlug,
        HarboraRuntimeOptions opt,
        CancellationToken ct)
    {
        IDockerEngine docker;
        try
        {
            docker = await engines.ResolveAsync(serverId, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The factory refuses a server with no agent endpoint and no enrolled node rather than
            // handing back this panel's engine. Naming it is the whole value of the refusal.
            logger.LogWarning(e, "Disk cleanup could not reach server {Server}.", serverName);
            return new DiskCleanupServerResult(serverId, serverName, 0, 0, 0, null, e.Message);
        }

        if (Nodes.NodeWorkloadEngine.NodeBehind(docker) is { } nodeId)
            return new DiskCleanupServerResult(serverId, serverName, 0, 0, 0, null,
                $"node {nodeId} manages its own images; the panel can neither list nor remove them, " +
                "so nothing here was examined");

        long? freeBefore = await FreeDiskAsync(docker, ct);

        var onHost = await docker.ListImagesAsync(opt.ImagePrefix + "/", ct);

        var failed = 0;

        // --- 1. Orphans: build images of apps that no longer exist ---
        var orphans = CleanupPlan.OrphanedBuildImages(onHost, opt.ImagePrefix, everySlug);
        var orphanRemoved = 0;

        foreach (var tag in orphans)
        {
            ct.ThrowIfCancellationRequested();
            try { await docker.RemoveImageAsync(tag, ct); orphanRemoved++; }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                failed++;
                logger.LogWarning(e, "Cleanup could not remove orphaned image {Tag} on {Server}.", tag, serverName);
            }
        }

        // --- 2. Retention for the living, exactly as the pipeline runs it ---
        var retentionRemoved = 0;

        if (opt.ImageRetentionCount > 0)
        {
            foreach (var app in appsHere)
            {
                ct.ThrowIfCancellationRequested();

                var history = await db.Deployments.IgnoreQueryFilters()
                    .Where(d => d.AppId == app.Id).ToListAsync(ct);

                var prunable = DeploymentPlanning.ImagesToPrune(
                    onHost, history, app.ActiveDeploymentId, opt.ImagePrefix, app.Slug, opt.ImageRetentionCount);

                foreach (var tag in prunable)
                {
                    try { await docker.RemoveImageAsync(tag, ct); retentionRemoved++; }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        failed++;
                        logger.LogWarning(e, "Cleanup could not remove superseded image {Tag} on {Server}.", tag, serverName);
                    }
                }
            }
        }

        long? freeAfter = await FreeDiskAsync(docker, ct);
        long? freed = freeBefore is { } b && freeAfter is { } a ? Math.Max(0, a - b) : null;

        return new DiskCleanupServerResult(
            serverId, serverName, orphanRemoved, retentionRemoved, failed, freed, null);
    }

    /// <summary>Free disk as the host reports it; null when it does not — unknown is not zero.</summary>
    private static async Task<long?> FreeDiskAsync(IDockerEngine docker, CancellationToken ct)
    {
        try
        {
            var info = await docker.GetHostInfoAsync(ct);
            return info.FreeDiskBytes > 0 ? info.FreeDiskBytes : null;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return null;
        }
    }
}
