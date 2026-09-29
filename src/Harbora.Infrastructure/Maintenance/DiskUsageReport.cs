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
/// A server's dangling images — untagged, referenced by nothing, and invisible to every figure above
/// because those all read tagged images under the build prefix. On the server that prompted this line
/// they were 165 images and 70 GB of a disk at 96%.
/// </summary>
/// <param name="TotalBytes">
/// The sum of each image's own size, so an upper bound: dangling layers can be shared too (with each
/// other and with tagged images), which is why the cleanup measures the disk instead of trusting it.
/// </param>
/// <param name="NotExamined">
/// Why the daemon was not asked, or null when it answered: a v1 node, an inbound agent too old to have
/// the endpoint, or a listing that failed. When this is set <paramref name="Count"/> and
/// <paramref name="TotalBytes"/> are zero because nothing was read — <b>not</b> because nothing is
/// there — and the page must say so rather than render them.
/// </param>
public sealed record DanglingImagesUsage(int Count, long TotalBytes, string? NotExamined)
{
    public static DanglingImagesUsage NotExaminedBecause(string reason) => new(0, 0, reason);
}

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
/// plus <see cref="DanglingImagesUsage.TotalBytes"/> (when the dangling images were actually read) —
/// an upper bound, not a promise: Docker layers are shared, so adding up per-image sizes can overstate
/// what a cleanup actually frees, which is exactly why <see cref="DiskCleanupService"/> measures the
/// disk's own before/after difference instead of trusting this sum. Zero on a server that was not
/// examined, which is why callers must always check <see cref="NotExamined"/> first — zero here never
/// means "clean".
/// </param>
/// <param name="Dangling">
/// The untagged, unreferenced images on this server. Has its own <see cref="DanglingImagesUsage.NotExamined"/>
/// because a server can be examined for tagged images and still not for these — an inbound agent that
/// predates the dangling endpoints lists tagged images perfectly well.
/// </param>
public sealed record ServerDiskUsage(
    Guid ServerId,
    string ServerName,
    string? NotExamined,
    IReadOnlyList<AppImageUsage> Apps,
    OrphanedImagesUsage Orphaned,
    IReadOnlyList<VolumeUsage> Volumes,
    long ReclaimableBytes,
    DanglingImagesUsage Dangling)
{
    public static ServerDiskUsage Skip(Guid id, string name, string reason) =>
        new(id, name, reason, [], new OrphanedImagesUsage(0, 0, []), [], 0,
            DanglingImagesUsage.NotExaminedBecause(reason));
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
            return ServerDiskUsage.Skip(serverId, serverName, Nodes.NodeWorkloadEngine.ImagesManagedByNodeReason(nodeId));

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

        // The half of the disk none of the figures above can see: untagged images. A cleanup now
        // prunes these too, so — when they were actually read — they are part of what it would reclaim.
        var dangling = await ReadDanglingAsync(docker, serverName, ct);

        var reclaimable = orphanBytes + appUsages.Sum(a => a.PrunableNowBytes)
            + (dangling.NotExamined is null ? dangling.TotalBytes : 0);

        return new ServerDiskUsage(serverId, serverName, null, appUsages, orphaned, volumes, reclaimable, dangling);
    }

    /// <summary>
    /// The dangling images on one machine, or the reason they could not be read. Read with the same
    /// <c>dangling=true</c> filter the prune uses, so the count here is what a prune would consider.
    /// Never fails the page: a machine whose tagged images were read fine but whose dangling images
    /// could not be (an agent that predates the endpoint, a daemon that errored) is named as not
    /// examined for these alone, never as having none.
    /// </summary>
    private async Task<DanglingImagesUsage> ReadDanglingAsync(IDockerEngine docker, string serverName, CancellationToken ct)
    {
        try
        {
            var found = await docker.GetDanglingImagesAsync(ct);
            return new DanglingImagesUsage(found.Count, found.SizeBytes, null);
        }
        catch (ImageSweepUnavailableException e)
        {
            logger.LogInformation("Dangling images on {Server} were not examined: {Reason}", serverName, e.Reason);
            return DanglingImagesUsage.NotExaminedBecause(e.Reason);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(e, "Disk usage report could not read the dangling images on {Server}.", serverName);
            return DanglingImagesUsage.NotExaminedBecause($"listing its dangling images failed: {e.Message}");
        }
    }
}
