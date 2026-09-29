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
/// <param name="DanglingRemoved">
/// Dangling images the daemon reported deleting when it was asked to prune them — its own count, kept
/// apart from <paramref name="OrphanRemoved"/> and <paramref name="RetentionRemoved"/>, which are
/// tagged build images this platform named itself. Zero here means "none" ONLY when
/// <paramref name="DanglingNotExamined"/> is null.
/// </param>
/// <param name="DanglingReclaimedBytes">
/// The daemon's own <c>SpaceReclaimed</c> for that prune, or null when it was not asked. Reported
/// beside <paramref name="FreedBytes"/> and never in place of it: the daemon's figure is its own
/// claim, the before/after difference is what the disk actually did.
/// </param>
/// <param name="DanglingNotExamined">
/// Why the dangling sweep did not happen on this machine, or null when it did: a v1 node, an inbound
/// agent too old to have the endpoint, a server that could not be reached, or a prune the daemon
/// failed. The other two sweeps can have run on a machine where this is set — an agent too old for the
/// prune still lists and removes tagged images — which is why it is its own field and not
/// <paramref name="Skipped"/>.
/// </param>
public sealed record DiskCleanupServerResult(
    Guid ServerId,
    string ServerName,
    int OrphanRemoved,
    int RetentionRemoved,
    int Failed,
    long? FreedBytes,
    string? Skipped,
    int DanglingRemoved = 0,
    long? DanglingReclaimedBytes = null,
    string? DanglingNotExamined = null);

/// <summary>What one cleanup run did, in figures a person can check against <c>df</c>.</summary>
/// <param name="OrphanRemoved">Build images of deleted apps that were removed.</param>
/// <param name="RetentionRemoved">Superseded build images of living apps that were removed.</param>
/// <param name="Failed">Removals the engine refused — an image still in use is left alone by contract.
/// A dangling prune that failed outright is not counted here: it is one call, not a removal, and it is
/// reported with its reason in <see cref="DiskCleanupServerResult.DanglingNotExamined"/>.</param>
/// <param name="FreedBytes">
/// The disks' own before/after difference, summed over the machines that reported one, or null when
/// none did. Deliberately measured rather than summed from image sizes: layers are shared, so adding
/// up per-image sizes overstates the win, and a cleanup that reports more than it freed is the kind
/// of number that teaches people to distrust the page. It covers all three sweeps together.
/// </param>
/// <param name="Servers">The same figures per machine, including the ones that were not swept.</param>
/// <param name="DanglingRemoved">Dangling images the daemons reported deleting, summed over the
/// machines that were pruned.</param>
/// <param name="DanglingReclaimedBytes">The daemons' own reported <c>SpaceReclaimed</c>, summed over
/// the machines that reported one, or null when none did — the daemons' claim, kept apart from
/// <paramref name="FreedBytes"/>.</param>
public sealed record DiskCleanupResult(
    int OrphanRemoved,
    int RetentionRemoved,
    int Failed,
    long? FreedBytes,
    IReadOnlyList<DiskCleanupServerResult> Servers,
    int DanglingRemoved = 0,
    long? DanglingReclaimedBytes = null);

/// <summary>One machine's share of a dangling-only sweep: what the daemon said it did, or why it was
/// not asked.</summary>
/// <param name="Removed">Images the daemon reported deleting. Meaningful only when
/// <paramref name="NotExamined"/> is null — otherwise it is zero because nothing was asked.</param>
/// <param name="ReclaimedBytes">The daemon's own <c>SpaceReclaimed</c>; null when it was not asked.</param>
/// <param name="NotExamined">Why the machine was not pruned, or null when it was.</param>
/// <param name="Faulted">True when the machine WAS asked and the prune threw — as opposed to being
/// structurally unable to answer (a v1 node, an agent too old to have the endpoint), which is an
/// expected steady state and not a problem to raise.</param>
public sealed record DanglingSweepServerResult(
    Guid ServerId,
    string ServerName,
    int Removed,
    long? ReclaimedBytes,
    string? NotExamined,
    bool Faulted);

/// <summary>What one dangling-only sweep did, per machine and in total.</summary>
/// <param name="Removed">Images the daemons reported deleting, summed over the machines pruned.</param>
/// <param name="ReclaimedBytes">The daemons' own reported bytes, summed over the machines that
/// reported one, or null when none did.</param>
public sealed record DanglingSweepResult(
    int Removed,
    long? ReclaimedBytes,
    IReadOnlyList<DanglingSweepServerResult> Servers)
{
    public bool AnyFaulted => Servers.Any(s => s.Faulted);
}

/// <summary>
/// Frees the disk of Harbora's own leftovers, on demand.
///
/// Three sweeps, in this order, on each server:
///
/// <list type="number">
/// <item>Build images whose app no longer exists — <see cref="CleanupPlan.OrphanedBuildImages"/>.
/// Nothing else ever removes these, because per-app retention runs inside a deployment and a
/// deleted app deploys nothing.</item>
/// <item>Per-app retention for every living app, the same rule the pipeline runs after a cutover
/// (<see cref="DeploymentPlanning.ImagesToPrune"/>) — reused, not re-implemented, so the button
/// and the pipeline cannot disagree about what rollback keeps. This catches apps that simply have
/// not deployed since the retention setting was lowered.</item>
/// <item>Dangling images — untagged and referenced by nothing — through the daemon's OWN prune call
/// with the dangling filter (<see cref="IDockerEngine.PruneDanglingImagesAsync"/>). The first two
/// sweeps only ever see tagged images under the build prefix, deliberately, so they can never remove
/// a customer's image; that also made them blind to the half of the disk that actually filled a
/// production server to 96% (165 dangling images, 70 GB, against about 10 GB of tagged build images).
/// Every classic-builder rebuild leaves its predecessor's untagged layers, and nothing tagged names
/// them.</item>
/// </list>
///
/// <para>
/// <b>What the third sweep will never do:</b> remove a tagged image, used or not. That is
/// <c>prune -a</c>, and it would delete build bases such as <c>dotnet/sdk</c> — forcing every next
/// build to re-pull — and the panel's own rollback tags. The dangling sweep is not a list-and-remove
/// loop either: the daemon decides what is dangling at the instant it prunes and never removes an image
/// a container references, and a loop over a stale listing would reintroduce the race that call
/// already handles.
/// </para>
///
/// <para>
/// All three run once per <b>server</b>, against that server's own engine. They used to run once, against
/// the panel's own Docker, while reading applications from every server — so a build image left on a
/// second machine was never a candidate, and the run reported a figure that described one host as if
/// it described the platform.
/// </para>
///
/// Reads are <c>IgnoreQueryFilters</c> throughout: the protection list must contain every
/// workspace's apps, or cleaning as one tenant would delete another tenant's rollback images.
/// Customer images — anything not under the build prefix — are never candidates for the first two
/// sweeps at any point.
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

        var targets = await TargetsAsync(apps.Select(a => a.ServerId), ct);

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
        var danglingRemoved = perServer.Sum(s => s.DanglingRemoved);

        var measured = perServer.Where(s => s.FreedBytes is not null).Select(s => s.FreedBytes!.Value).ToList();
        long? freed = measured.Count == 0 ? null : measured.Sum();

        var reported = perServer.Where(s => s.DanglingReclaimedBytes is not null)
            .Select(s => s.DanglingReclaimedBytes!.Value).ToList();
        long? danglingBytes = reported.Count == 0 ? null : reported.Sum();

        var skipped = perServer.Where(s => s.Skipped is not null).ToList();
        var danglingNotExamined = perServer.Where(s => s.DanglingNotExamined is not null).ToList();

        logger.LogInformation(
            "Disk cleanup swept {Swept} of {Total} server(s): removed {Orphans} orphaned and {Retention} superseded " +
            "image(s), pruned {Dangling} dangling image(s) (the daemons reported {DanglingBytes} reclaimed), " +
            "{Failed} refused, freed {Freed}. Not swept: {Skipped}. Dangling images not examined on: {DanglingSkipped}.",
            perServer.Count - skipped.Count, perServer.Count, orphanRemoved, retentionRemoved, danglingRemoved,
            danglingBytes is { } d ? Tenancy.ByteSize.Measured(d) : "unknown", failed,
            freed is { } f ? Tenancy.ByteSize.Measured(f) : "unknown",
            skipped.Count == 0 ? "none" : string.Join("; ", skipped.Select(s => $"{s.ServerName} — {s.Skipped}")),
            danglingNotExamined.Count == 0
                ? "none"
                : string.Join("; ", danglingNotExamined.Select(s => $"{s.ServerName} — {s.DanglingNotExamined}")));

        return new DiskCleanupResult(
            orphanRemoved, retentionRemoved, failed, freed, perServer, danglingRemoved, danglingBytes);
    }

    /// <summary>
    /// Only the third sweep, on every server: prune dangling images through the daemon and nothing
    /// else — no orphans, no retention, no listing, no removal of any named image. This is what the
    /// unattended background sweep calls (<see cref="DanglingImageSweeper"/>), and the reason it is
    /// its own method rather than <see cref="RunAsync"/> with a flag: an unattended job that CAN reach
    /// the orphan and retention removals is one wrong argument from deleting a rollback image at
    /// three in the morning, and this one cannot.
    ///
    /// <para>
    /// Never throws for a machine: one that cannot be reached, is behind a v1 node, runs an agent too
    /// old for the endpoint, or whose daemon refuses is reported in its own row and the next machine is
    /// still visited. Only cancellation escapes.
    /// </para>
    /// </summary>
    public async Task<DanglingSweepResult> PruneDanglingImagesAsync(CancellationToken ct)
    {
        var appServerIds = await db.Apps.IgnoreQueryFilters().Select(a => a.ServerId).ToListAsync(ct);
        var targets = await TargetsAsync(appServerIds, ct);

        var perServer = new List<DanglingSweepServerResult>(targets.Count);
        foreach (var server in targets)
        {
            ct.ThrowIfCancellationRequested();

            var (docker, notExamined) = await ResolveAsync(server.Id, server.Name, "Dangling image sweep", ct);
            perServer.Add(docker is null
                ? new DanglingSweepServerResult(server.Id, server.Name, 0, null, notExamined, Faulted: false)
                : await PruneDanglingAsync(docker, server.Id, server.Name, ct));
        }

        var reported = perServer.Where(s => s.ReclaimedBytes is not null).Select(s => s.ReclaimedBytes!.Value).ToList();
        return new DanglingSweepResult(
            perServer.Sum(s => s.Removed), reported.Count == 0 ? null : reported.Sum(), perServer);
    }

    /// <summary>
    /// Every server, not only the ones with apps on them. An orphan is by definition an image
    /// whose app is gone, so the machine whose last app was deleted is precisely the machine with
    /// the most to reclaim and the one a "servers that still have apps" list would never visit — and a
    /// dangling image belongs to no app at all.
    ///
    /// <para>
    /// Plus any server id an app still carries that has no row — an app created before the
    /// platform had servers carries <c>Guid.Empty</c>, and the factory answers for an unknown server
    /// exactly as it did before this method knew about servers at all: the local machine. Leaving
    /// those out would quietly stop pruning those apps' superseded releases.
    /// </para>
    /// </summary>
    private async Task<List<(Guid Id, string Name)>> TargetsAsync(IEnumerable<Guid> appServerIds, CancellationToken ct)
    {
        var servers = await db.Servers.IgnoreQueryFilters()
            .Select(s => new { s.Id, s.Name })
            .ToListAsync(ct);

        return servers.Select(s => (s.Id, s.Name))
            .Concat(appServerIds.Distinct()
                .Where(id => servers.All(s => s.Id != id))
                .Select(id => (Id: id, Name: $"server {id}")))
            .ToList();
    }

    /// <summary>
    /// The engine for one machine, or why there is none to sweep: the factory refuses a server with no
    /// agent endpoint and no enrolled node rather than handing back this panel's engine (naming it is
    /// the whole value of the refusal), and a v1 node manages its own images so there is nothing the
    /// panel may examine.
    /// </summary>
    private async Task<(IDockerEngine? Docker, string? Skipped)> ResolveAsync(
        Guid serverId, string serverName, string what, CancellationToken ct)
    {
        IDockerEngine docker;
        try
        {
            docker = await engines.ResolveAsync(serverId, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "{What} could not reach server {Server}.", what, serverName);
            return (null, e.Message);
        }

        return Nodes.NodeWorkloadEngine.NodeBehind(docker) is { } nodeId
            ? (null, Nodes.NodeWorkloadEngine.ImagesManagedByNodeReason(nodeId))
            : (docker, null);
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
        var (docker, notSwept) = await ResolveAsync(serverId, serverName, "Disk cleanup", ct);
        if (docker is null)
            // The whole machine, so the dangling sweep did not happen on it either — said in the
            // dangling field too, so a reader of those figures alone never takes zero for clean.
            return new DiskCleanupServerResult(
                serverId, serverName, 0, 0, 0, null, notSwept, 0, null, notSwept);

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

        // --- 3. Dangling images: the daemon's own prune. Last, so it sees the node as the first two
        // sweeps left it — anything those removals leave untagged goes in this run, not the next one ---
        var dangling = await PruneDanglingAsync(docker, serverId, serverName, ct);

        // Measured after ALL THREE sweeps, so FreedBytes stays the honest total of the run rather than
        // a figure that quietly excludes the sweep that freed the most.
        long? freeAfter = await FreeDiskAsync(docker, ct);
        long? freed = freeBefore is { } b && freeAfter is { } a ? Math.Max(0, a - b) : null;

        return new DiskCleanupServerResult(
            serverId, serverName, orphanRemoved, retentionRemoved, failed, freed, null,
            dangling.Removed, dangling.ReclaimedBytes, dangling.NotExamined);
    }

    /// <summary>
    /// The dangling sweep on one machine: the engine's own prune call, its figures reported as the
    /// daemon gave them, and — for the ways it may not run — a reason instead of a zero.
    ///
    /// <para>
    /// <see cref="ImageSweepUnavailableException"/> is the machine being unable to answer (a v1 node,
    /// or an inbound agent old enough to answer 404): an expected state, reported as not examined and
    /// logged quietly, since a daily warning about an agent nobody has updated yet is noise that hides
    /// real ones. Any other exception is a prune that was attempted and failed — logged as a warning,
    /// and marked <see cref="DanglingSweepServerResult.Faulted"/> — and it never stops the caller's next
    /// machine. A cancellation is the caller's own and passes through.
    /// </para>
    /// </summary>
    private async Task<DanglingSweepServerResult> PruneDanglingAsync(
        IDockerEngine docker, Guid serverId, string serverName, CancellationToken ct)
    {
        try
        {
            var pruned = await docker.PruneDanglingImagesAsync(ct);
            return new DanglingSweepServerResult(
                serverId, serverName, pruned.ImagesDeleted, pruned.BytesReclaimed, NotExamined: null, Faulted: false);
        }
        catch (ImageSweepUnavailableException e)
        {
            logger.LogInformation(
                "Dangling images on {Server} were not examined: {Reason}", serverName, e.Reason);
            return new DanglingSweepServerResult(serverId, serverName, 0, null, e.Reason, Faulted: false);
        }
        // "The caller's token was cancelled", not "an OperationCanceledException was thrown": an HTTP
        // client that timed out on a long prune throws a TaskCanceledException with no cancellation
        // behind it, and that is a failed prune to report, not a shutdown to obey.
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(e, "The dangling-image prune on {Server} failed.", serverName);
            return new DanglingSweepServerResult(
                serverId, serverName, 0, null, $"the dangling-image prune failed: {e.Message}", Faulted: true);
        }
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
