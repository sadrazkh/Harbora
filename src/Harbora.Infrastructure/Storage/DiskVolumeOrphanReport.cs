using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Infrastructure.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Harbora.Infrastructure.Storage;

/// <summary>
/// HARBORA-0033's disk-side half — the question <see cref="VolumeOrphanReport"/> could only ever
/// answer with "not checked": a volume that physically exists on a server with no
/// <see cref="Domain.Apps.Volume"/> row pointing at it anywhere, left behind by an unmount or an app
/// delete that kept its data.
///
/// <para>
/// <see cref="VolumeOrphanReport"/> is a pure database query and can run from
/// <c>AdminCommands</c> even when the panel itself refuses to start — this cannot. Answering "what is
/// actually on disk" needs a live connection to every server's engine (<see cref="IServerEngineFactory"/>),
/// which in turn needs the master key, the secret protector and the node command channel — exactly the
/// infrastructure <c>AdminCommands</c> deliberately does not build. So this runs inside the running
/// application instead (see <c>ServersController.DiskVolumeReport</c>), the same way
/// <see cref="Maintenance.DiskCleanupService"/> and <see cref="Monitoring.MetricsCollector"/> already
/// reach every server through that same factory.
/// </para>
///
/// <para>
/// Every server is visited and every server is named in the result — reached, refused, or
/// unreachable — never silently folded into a total. A server a v1 node stands behind refuses this
/// specific question by name (<see cref="Nodes.NodeWorkloadEngine.ListVolumesAsync"/>: the v1 contract
/// has no verb for enumerating a node's own disk), which is reported exactly like the D4 finding it
/// mirrors — a named "cannot", not a quiet zero.
/// </para>
///
/// <para>
/// Only volumes named with <see cref="MountPath.HarboraVolumePrefix"/> are ever considered — see that
/// constant's own remarks for why a managed service's database volume or a compose stack's volume can
/// never be mistaken for an orphan here. Nothing outside that naming scheme was ever a candidate for a
/// <see cref="Domain.Apps.Volume"/> row in the first place, so treating it as "orphaned" would be
/// inventing a claim about something this report was never in a position to track.
/// </para>
/// </summary>
public sealed class DiskVolumeOrphanReport(
    HarboraDbContext db,
    IServerEngineFactory engines,
    ILogger<DiskVolumeOrphanReport> logger)
{
    public async Task<DiskVolumeOrphanReportResult> BuildAsync(CancellationToken ct)
    {
        // IgnoreQueryFilters for the same reason VolumeOrphanReport's own Q1/Q2 give: this has to see
        // every workspace's volumes to tell a live one from an orphan, not just the caller's own.
        var servers = await db.Servers.IgnoreQueryFilters()
            .Select(s => new { s.Id, s.Name })
            .ToListAsync(ct);

        var known = await db.Volumes.IgnoreQueryFilters()
            .Join(db.Apps.IgnoreQueryFilters(), v => v.AppId, a => a.Id, (v, a) => new { a.ServerId, v.Name })
            .ToListAsync(ct);
        var knownByServer = known
            .GroupBy(x => x.ServerId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Name).ToHashSet(StringComparer.Ordinal));

        var results = new List<ServerDiskVolumeResult>(servers.Count);
        foreach (var server in servers)
        {
            ct.ThrowIfCancellationRequested();
            results.Add(await CheckServerAsync(
                server.Id, server.Name,
                knownByServer.TryGetValue(server.Id, out var names) ? names : new HashSet<string>(StringComparer.Ordinal),
                ct));
        }

        return new DiskVolumeOrphanReportResult(results);
    }

    private async Task<ServerDiskVolumeResult> CheckServerAsync(
        Guid serverId, string serverName, HashSet<string> knownVolumeNames, CancellationToken ct)
    {
        IDockerEngine engine;
        try
        {
            engine = await engines.ResolveAsync(serverId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The factory itself refuses a server with no agent endpoint and no enrolled node rather
            // than silently handing back this panel's own engine — naming that refusal here is the
            // whole reason this method exists rather than just calling ListVolumesAsync directly.
            logger.LogWarning(ex, "Disk volume report could not reach server {Server}.", serverName);
            return ServerDiskVolumeResult.Unreachable(serverId, serverName, ex.Message);
        }

        IReadOnlyList<VolumeInfo> onDisk;
        try
        {
            onDisk = await engine.ListVolumesAsync(ct);
        }
        catch (NodeCapabilityException ex)
        {
            // Not a network hiccup: the v1 contract has no verb for this, on purpose (see
            // NodeWorkloadEngine.ListVolumesAsync's own doc comment). Distinguished from a plain
            // "unreachable" below because this server answers every other question just fine — it
            // is this ONE capability the platform never gave it, and an operator reading "refused"
            // needs to know that, not go chasing a flaky connection that was never the problem.
            logger.LogInformation(ex, "Server {Server} cannot be asked to list its volumes.", serverName);
            return ServerDiskVolumeResult.Refused(serverId, serverName, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Disk volume report could not list volumes on {Server}.", serverName);
            return ServerDiskVolumeResult.Unreachable(serverId, serverName,
                $"was reached, but listing its volumes failed: {ex.Message}");
        }

        var orphans = onDisk
            .Where(v => v.Name.StartsWith(MountPath.HarboraVolumePrefix, StringComparison.Ordinal))
            .Where(v => !knownVolumeNames.Contains(v.Name))
            .OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
            .Select(v => new DiskOrphanVolume(v.Name, v.CreatedAt))
            .ToList();

        return ServerDiskVolumeResult.Reached(serverId, serverName, orphans);
    }

    /// <summary>
    /// Formats a built report for a person to read — same idiom as
    /// <see cref="VolumeOrphanReport.Render"/>: a server with nothing wrong says so explicitly, and a
    /// server that could not be asked at all is named, never merged silently into "0 found".
    /// </summary>
    public static string Render(DiskVolumeOrphanReportResult report)
    {
        var w = new System.Text.StringBuilder();

        w.AppendLine("Disk volume orphan report");
        w.AppendLine("────────────────────────────────────────");
        w.AppendLine();

        if (report.Servers.Count == 0)
        {
            w.AppendLine("No servers are registered — nothing to check.");
            return w.ToString();
        }

        var reached = report.Servers.Where(s => s.Outcome == ServerVolumeCheckOutcome.Reached).ToList();
        var notReached = report.Servers.Where(s => s.Outcome != ServerVolumeCheckOutcome.Reached).ToList();

        w.AppendLine($"Servers checked: {reached.Count} of {report.Servers.Count}");
        if (notReached.Count > 0)
        {
            w.AppendLine($"Servers NOT checked: {notReached.Count} — named below, never folded into the totals.");
            foreach (var s in notReached)
                w.AppendLine($"   - {s.ServerName} ({(s.Outcome == ServerVolumeCheckOutcome.Refused ? "refused" : "unreachable")}): {s.Reason}");
        }
        else
        {
            w.AppendLine("Every registered server answered.");
        }
        w.AppendLine();

        var totalOrphans = reached.Sum(s => s.Orphans.Count);
        w.AppendLine($"Volumes on disk with no database row, across {reached.Count} checked server(s): {totalOrphans}");
        if (totalOrphans == 0 && reached.Count > 0)
        {
            w.AppendLine("   None found on any server this report could actually reach.");
        }
        else
        {
            foreach (var server in reached)
            {
                if (server.Orphans.Count == 0)
                {
                    w.AppendLine($"   {server.ServerName}: none found.");
                    continue;
                }

                foreach (var v in server.Orphans)
                    w.AppendLine($"   {server.ServerName}: {v.Name}" +
                                  (v.CreatedAt is { } created ? $" (created {created:yyyy-MM-dd})" : " (creation time unknown)"));
            }
        }

        w.AppendLine();
        w.AppendLine("Read-only: this report finds volumes, it does not remove them. Deleting a volume the");
        w.AppendLine("platform has no row for is a human decision, made on the server, after checking what is");
        w.AppendLine("actually inside it.");

        return w.ToString();
    }
}

/// <summary>Whether a server could actually be asked to list its volumes.</summary>
public enum ServerVolumeCheckOutcome
{
    /// <summary>Answered, and its disk was compared against the database.</summary>
    Reached,

    /// <summary>Answered every other question but has no verb for this one — a v1 node, today.</summary>
    Refused,

    /// <summary>Could not be reached at all, or answered this specific request with a failure.</summary>
    Unreachable
}

/// <summary>One volume found on a server's disk with no <see cref="Domain.Apps.Volume"/> row anywhere
/// pointing at it. <see cref="CreatedAt"/> is null when the engine could not report one.</summary>
public sealed record DiskOrphanVolume(string Name, DateTimeOffset? CreatedAt);

/// <summary>
/// One server's share of the report. <see cref="Reason"/> is null only when <see cref="Outcome"/> is
/// <see cref="ServerVolumeCheckOutcome.Reached"/> — every other outcome names why, in the engine's or
/// factory's own words, exactly the discipline <see cref="Maintenance.DiskCleanupServerResult.Skipped"/>
/// already applies to the sibling image-cleanup report.
/// </summary>
public sealed record ServerDiskVolumeResult(
    Guid ServerId, string ServerName, ServerVolumeCheckOutcome Outcome, string? Reason,
    IReadOnlyList<DiskOrphanVolume> Orphans)
{
    public static ServerDiskVolumeResult Reached(Guid id, string name, IReadOnlyList<DiskOrphanVolume> orphans) =>
        new(id, name, ServerVolumeCheckOutcome.Reached, null, orphans);

    public static ServerDiskVolumeResult Refused(Guid id, string name, string reason) =>
        new(id, name, ServerVolumeCheckOutcome.Refused, reason, []);

    public static ServerDiskVolumeResult Unreachable(Guid id, string name, string reason) =>
        new(id, name, ServerVolumeCheckOutcome.Unreachable, reason, []);
}

/// <summary>The full report: one entry per registered server, none of them omitted.</summary>
public sealed record DiskVolumeOrphanReportResult(IReadOnlyList<ServerDiskVolumeResult> Servers);
