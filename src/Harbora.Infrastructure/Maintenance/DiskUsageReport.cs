using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Infrastructure.Deployments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Harbora.Infrastructure.Maintenance;

/// <summary>
/// One living app's share of a server's build images: what <see cref="DiskCleanupService"/> and
/// <see cref="DeploymentPlanning"/> already know about this app, read rather than re-derived.
/// </summary>
/// <param name="ActiveTag">The image tag currently serving traffic, or null when the app has never
/// deployed successfully or carries no active deployment.</param>
/// <param name="RollbackEligibleTags">
/// Distinct tags <see cref="DeploymentPlanning.RollbackEligibleDeploymentIds"/> says still resolve to
/// an instant rollback.
/// </param>
/// <param name="PrunableNowTags">
/// Exactly what <see cref="DeploymentPlanning.ImagesToPrune"/> would remove for this app right now —
/// called directly, not re-implemented, so this list and the cleanup button can never disagree about
/// what counts as superseded.
/// </param>
/// <param name="PrunableNowBytes">
/// The on-host size of <paramref name="PrunableNowTags"/>, summed. Shared Docker layers mean this can
/// overstate what removing them would actually free — see <see cref="ServerDiskUsage.ReclaimableBytes"/>.
/// </param>
public sealed record AppImageUsage(
    Guid AppId,
    string AppSlug,
    int ImageCount,
    long TotalBytes,
    string? ActiveTag,
    IReadOnlyList<string> RollbackEligibleTags,
    IReadOnlyList<string> PrunableNowTags,
    long PrunableNowBytes);

/// <summary>Build images whose app no longer exists — <see cref="CleanupPlan.OrphanedBuildImages"/>,
/// called directly rather than re-derived.</summary>
public sealed record OrphanedImagesUsage(int Count, long TotalBytes, IReadOnlyList<string> Tags);

/// <summary>
/// One app volume as the database already knows it: <see cref="SizeBytes"/> is
/// <see cref="Domain.Apps.Volume.StorageBytes"/>, last written by <c>StorageMeasurer</c>'s periodic
/// walk — null when that walk has not reached this volume yet, which is a different fact from "empty"
/// and must read as one.
/// </summary>
public sealed record VolumeUsage(string Name, long? SizeBytes, DateTimeOffset? MeasuredAt);

/// <summary>
/// One machine's share of the report. <see cref="NotExamined"/> is null only when the server actually
/// answered — every other case (the engine factory refused it, or it is a v1 node with no image verbs)
/// names why, the same discipline <see cref="DiskCleanupServerResult.Skipped"/> already keeps for the
/// sweep this report describes without running.
/// </summary>
/// <param name="ReclaimableBytes">
/// <see cref="OrphanedImagesUsage.TotalBytes"/> plus every app's <see cref="AppImageUsage.PrunableNowBytes"/>
/// — an upper bound, not a promise: Docker layers are shared, so adding up per-image sizes can overstate
/// what a cleanup actually frees, which is exactly why <see cref="DiskCleanupService"/> measures the
/// disk's own before/after difference instead of trusting this sum. Zero on a server that was not
/// examined, which is why callers must always check <see cref="NotExamined"/> first — zero here never
/// means "clean".
/// </param>
public sealed record ServerDiskUsage(
    Guid ServerId,
    string ServerName,
    string? NotExamined,
    IReadOnlyList<AppImageUsage> Apps,
    OrphanedImagesUsage Orphaned,
    IReadOnlyList<VolumeUsage> Volumes,
    long ReclaimableBytes)
{
    public static ServerDiskUsage Skip(Guid id, string name, string reason) =>
        new(id, name, reason, [], new OrphanedImagesUsage(0, 0, []), [], 0);
}

/// <summary>The full report: one entry per server this run knew about, examined or not.</summary>
public sealed record DiskUsageReportResult(IReadOnlyList<ServerDiskUsage> Servers)
{
    /// <summary>Summed only over servers this run could actually reach — a server that was not
    /// examined contributes nothing here rather than a false zero.</summary>
    public long ReclaimableBytes => Servers.Where(s => s.NotExamined is null).Sum(s => s.ReclaimableBytes);

    public bool AnyExamined => Servers.Any(s => s.NotExamined is null);
    public bool AnyNotExamined => Servers.Any(s => s.NotExamined is not null);
}

/// <summary>
/// The read-only half of what <see cref="DiskCleanupService"/> would do: per server, per app, the
/// build-image count and bytes, which tag is active, which are rollback-eligible, and which a cleanup
/// would remove right now — plus orphaned build images and known app-volume sizes.
///
/// <para>
/// Every figure that decides "what is prunable" is the cleanup's own: <see cref="CleanupPlan.OrphanedBuildImages"/>
/// and <see cref="DeploymentPlanning.ImagesToPrune"/> are called here exactly as
/// <see cref="DiskCleanupService"/> calls them, never re-implemented. A second notion of "what is
/// prunable" would drift from the cleaner that actually deletes, and the drift would show up here as a
/// number promising space a cleanup does not free.
/// </para>
///
/// <para>
/// <see cref="ServerDiskUsage.ReclaimableBytes"/> sums on-host image sizes for the candidates above.
/// That is deliberately an upper bound, not a prediction: Docker images share layers, so two prunable
/// tags can double-count the same bytes on disk. <see cref="DiskCleanupService.RunAsync"/> itself never
/// trusts this kind of sum — it measures the disk's own before/after difference — and a page that
/// quoted this figure as an exact promise would teach people to distrust it the first time a real
/// cleanup freed less. Say so on the page, not just here.
/// </para>
///
/// <para>
/// A server the engine factory refuses, or one behind a v1 node with no image verbs, is named as not
/// examined rather than folded into the totals as a clean machine — see
/// <see cref="Storage.DiskVolumeOrphanReport"/> for the same shape applied to volumes.
/// </para>
/// </summary>
public sealed class DiskUsageReport(
    HarboraDbContext db,
    IServerEngineFactory engines,
    IOptions<HarboraRuntimeOptions> options,
    ILogger<DiskUsageReport> logger)
{
    public async Task<DiskUsageReportResult> BuildAsync(CancellationToken ct)
    {
        var opt = options.Value;

        var apps = await db.Apps.IgnoreQueryFilters()
            .Select(a => new { a.Id, a.Slug, a.ActiveDeploymentId, a.ServerId })
            .ToListAsync(ct);

        // Every registered server, plus any server id an app still carries with no row of its own —
        // the same union DiskCleanupService builds, and for the same reason: an app created before
        // the platform had servers carries Guid.Empty, and leaving it out would silently drop it from
        // the report the moment servers were introduced.
        var servers = await db.Servers.IgnoreQueryFilters()
            .Select(s => new { s.Id, s.Name })
            .ToListAsync(ct);

        var targets = servers.Select(s => (s.Id, s.Name))
            .Concat(apps.Select(a => a.ServerId).Distinct()
                .Where(id => servers.All(s => s.Id != id))
                .Select(id => (Id: id, Name: $"server {id}")))
            .ToList();

        var everySlug = apps.Select(a => a.Slug).ToList();

        // App-volume sizes are already sitting in the database — StorageMeasurer's own periodic walk
        // wrote them — so this needs no engine round trip at all, unlike the image figures below.
        var knownVolumes = await db.Volumes.IgnoreQueryFilters()
            .Join(db.Apps.IgnoreQueryFilters(), v => v.AppId, a => a.Id,
                (v, a) => new { a.ServerId, v.Name, v.StorageBytes, v.StorageMeasuredAt })
            .ToListAsync(ct);
        var volumesByServer = knownVolumes
            .GroupBy(v => v.ServerId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<VolumeUsage>)g
                    .Select(v => new VolumeUsage(v.Name, v.StorageBytes, v.StorageMeasuredAt))
                    .OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList());

        var results = new List<ServerDiskUsage>(targets.Count);
        foreach (var target in targets)
        {
            ct.ThrowIfCancellationRequested();
            results.Add(await BuildServerAsync(
                target.Id, target.Name,
                apps.Where(a => a.ServerId == target.Id)
                    .Select(a => (a.Id, a.Slug, a.ActiveDeploymentId)).ToList(),
                everySlug,
                volumesByServer.TryGetValue(target.Id, out var v) ? v : [],
                opt, ct));
        }

        return new DiskUsageReportResult(results);
    }

    private async Task<ServerDiskUsage> BuildServerAsync(
        Guid serverId,
        string serverName,
        IReadOnlyList<(Guid Id, string Slug, Guid? ActiveDeploymentId)> appsHere,
        IReadOnlyList<string> everySlug,
        IReadOnlyList<VolumeUsage> volumes,
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
            // Named, not folded into a "0 images" reading — the same refusal DiskCleanupService's own
            // sweep would hit for this machine.
            logger.LogWarning(e, "Disk usage report could not reach server {Server}.", serverName);
            return ServerDiskUsage.Skip(serverId, serverName, e.Message);
        }

        if (Nodes.NodeWorkloadEngine.NodeBehind(docker) is { } nodeId)
            return ServerDiskUsage.Skip(serverId, serverName,
                $"node {nodeId} manages its own images; the panel can neither list nor remove them, " +
                "so nothing here was examined");

        var onHost = await docker.ListImagesAsync(opt.ImagePrefix + "/", ct);
        var bytesByTag = onHost
            .GroupBy(i => i.Tag, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().SizeBytes, StringComparer.Ordinal);

        var appUsages = new List<AppImageUsage>(appsHere.Count);
        foreach (var app in appsHere)
        {
            ct.ThrowIfCancellationRequested();

            var history = await db.Deployments.IgnoreQueryFilters()
                .Where(d => d.AppId == app.Id).ToListAsync(ct);

            var prefix = DeploymentPlanning.BuildImagePrefix(opt.ImagePrefix, app.Slug);
            var appTags = onHost.Select(i => i.Tag)
                .Where(t => t.StartsWith(prefix, StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            var activeTag = app.ActiveDeploymentId is { } activeId
                ? history.FirstOrDefault(d => d.Id == activeId)?.ImageTag
                : null;

            var eligibleIds = DeploymentPlanning.RollbackEligibleDeploymentIds(
                history, app.ActiveDeploymentId, opt.ImageRetentionCount);
            var rollbackTags = history
                .Where(d => eligibleIds.Contains(d.Id) && !string.IsNullOrWhiteSpace(d.ImageTag))
                .Select(d => d.ImageTag!)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            // Same guard DiskCleanupService.SweepAsync applies: 0 (or less) disables retention
            // entirely, so nothing here is prunable and every image is protected by definition.
            var prunableNow = opt.ImageRetentionCount > 0
                ? DeploymentPlanning.ImagesToPrune(
                    onHost, history, app.ActiveDeploymentId, opt.ImagePrefix, app.Slug, opt.ImageRetentionCount)
                : Array.Empty<string>();

            var prunableBytes = prunableNow.Sum(t => bytesByTag.GetValueOrDefault(t));
            var totalBytes = appTags.Sum(t => bytesByTag.GetValueOrDefault(t));

            appUsages.Add(new AppImageUsage(
                app.Id, app.Slug, appTags.Count, totalBytes, activeTag, rollbackTags, prunableNow, prunableBytes));
        }

        var orphanTags = CleanupPlan.OrphanedBuildImages(onHost, opt.ImagePrefix, everySlug);
        var orphanBytes = orphanTags.Sum(t => bytesByTag.GetValueOrDefault(t));
        var orphaned = new OrphanedImagesUsage(orphanTags.Count, orphanBytes, orphanTags);

        var reclaimable = orphanBytes + appUsages.Sum(a => a.PrunableNowBytes);

        return new ServerDiskUsage(serverId, serverName, null, appUsages, orphaned, volumes, reclaimable);
    }
}
