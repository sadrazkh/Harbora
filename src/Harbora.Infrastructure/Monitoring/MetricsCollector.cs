using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Common;
using Harbora.Domain.Monitoring;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Harbora.Infrastructure.Monitoring;

/// <summary>
/// Samples host + per-container metrics into the time series, watches for crashed apps and low
/// disk, and fires the matching alerts. Old samples are trimmed each pass to bound table growth.
/// </summary>
public sealed class MetricsCollector(
    HarboraDbContext db,
    IServerEngineFactory engineFactory,
    INotificationService notifications,
    AlertThrottle throttle,
    ISystemClock clock,
    MetricsRollupService rollups,
    ILogger<MetricsCollector> logger) : IMetricsCollector
{
    private const double DiskWarnRatio = 0.85;
    /// <summary>How long raw samples are kept. Beyond this, the summaries answer instead.</summary>
    private static readonly TimeSpan Retention = MetricRollups.RawRetention;
    private static readonly TimeSpan DiskAlertInterval = TimeSpan.FromHours(1);

    public async Task CollectAsync(CancellationToken ct)
    {
        var now = clock.UtcNow;
        var servers = await db.Servers.ToListAsync(ct);

        // Sample every registered node through its own engine (local in-process or remote agent).
        foreach (var server in servers)
        {
            var docker = await engineFactory.ResolveAsync(server.Id, ct);
            await CollectServerAsync(server, docker, now, ct);
        }

        // Summarise BEFORE pruning, never the other way round. Getting that order wrong loses the
        // history silently: the charts keep working, on data quietly missing a week.
        try { await rollups.RunAsync(ct); }
        catch (Exception ex) { logger.LogError(ex, "Rolling up metrics failed; raw samples were kept."); return; }

        var cutoff = now - Retention;
        await db.MonitoringMetrics.Where(m => m.Timestamp < cutoff).ExecuteDeleteAsync(ct);
        await db.SaveChangesAsync(ct);
    }

    private async Task CollectServerAsync(Domain.Servers.Server server, IDockerEngine docker, DateTimeOffset now, CancellationToken ct)
    {
        var samples = new List<MonitoringMetric>();

        // --- host ---
        try
        {
            var host = await docker.GetHostInfoAsync(ct);
            var diskUsed = host.TotalDiskBytes - host.FreeDiskBytes;
            samples.Add(Metric(server.Id, "disk.used", null, diskUsed, now));
            samples.Add(Metric(server.Id, "disk.total", null, host.TotalDiskBytes, now));
            samples.Add(Metric(server.Id, "mem.total", null, host.TotalMemoryBytes, now));
            samples.Add(Metric(server.Id, "containers.running", null, host.ContainersRunning, now));

            server.CpuCores = host.CpuCores;
            server.TotalMemoryBytes = host.TotalMemoryBytes;
            server.TotalDiskBytes = host.TotalDiskBytes;
            server.DockerVersion = host.DockerVersion;
            server.Status = ServerStatus.Online;
            server.LastHeartbeatAt = now;

            if (host.TotalDiskBytes > 0 && (double)diskUsed / host.TotalDiskBytes >= DiskWarnRatio)
                await MaybeDiskAlert(server, diskUsed, host.TotalDiskBytes, now, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Host metrics unavailable.");
            server.Status = ServerStatus.Offline;
        }

        // --- containers + app-crash detection ---
        try
        {
            var containers = await docker.ListContainersAsync("harbora.app", ct);
            double totalCpu = 0;
            long totalRx = 0, totalTx = 0, totalMemory = 0;
            // Whether anything was actually read this tick. Docker's stats call fails intermittently,
            // and without this the loop falls through with its totals still at zero and records them
            // as a measurement — which reads as "no traffic" and, on the next tick, as a spike back
            // up to the real counter. Unknown is not zero, here least of all.
            var measured = 0;
            foreach (var c in containers.Where(c => c.State.Equals("running", StringComparison.OrdinalIgnoreCase)))
            {
                var stats = await docker.GetStatsAsync(c.Id, ct);
                if (stats is null) continue;
                totalCpu += stats.CpuPercent;
                samples.Add(Metric(server.Id, "cpu.percent", c.Name, stats.CpuPercent, now));
                samples.Add(Metric(server.Id, "mem.used", c.Name, stats.MemoryUsedBytes, now));

                // Stored raw, as the counters Docker gives us. They are cumulative since the
                // container started, so the rate is worked out at read time by NetworkThroughput —
                // which is also where a restart is recognised as a reset rather than a spike.
                // Recording a rate here instead would bake this tick's interval into the row and
                // lose the ability to tell "no traffic" from "no measurement".
                samples.Add(Metric(server.Id, "net.rx", c.Name, stats.NetRxBytes, now));
                samples.Add(Metric(server.Id, "net.tx", c.Name, stats.NetTxBytes, now));

                totalRx += stats.NetRxBytes;
                totalTx += stats.NetTxBytes;
                totalMemory += stats.MemoryUsedBytes;
                measured++;
            }

            // Host totals, so the dashboard has a series without summing every container. Written
            // only when at least one container answered — see `measured`.
            if (measured > 0)
            {
                samples.Add(Metric(server.Id, "cpu.percent", null, Math.Round(totalCpu, 2), now));
                samples.Add(Metric(server.Id, "mem.used", null, totalMemory, now));
                samples.Add(Metric(server.Id, "net.rx", null, totalRx, now));
                samples.Add(Metric(server.Id, "net.tx", null, totalTx, now));
            }

            await ReconcileAppStatusesAsync(containers, now, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Container metrics unavailable for {Server}.", server.Name);
        }

        db.MonitoringMetrics.AddRange(samples);
    }

    /// <summary>
    /// Brings each app's status in line with its containers, in both directions: an app whose
    /// container is crash-looping stops being advertised as running, and one that has recovered stops
    /// being advertised as crashed.
    /// </summary>
    private async Task ReconcileAppStatusesAsync(
        IReadOnlyList<ContainerInfo> containers, DateTimeOffset now, CancellationToken ct)
    {
        var bySlug = containers
            .Where(c => c.Labels.ContainsKey("harbora.app"))
            .GroupBy(c => c.Labels["harbora.app"], StringComparer.Ordinal);

        foreach (var group in bySlug)
        {
            var app = await db.Apps.FirstOrDefaultAsync(a => a.Slug == group.Key, ct);
            if (app is null) continue;

            var observed = AppHealthDiagnosis.Observe(group);
            if (AppHealthDiagnosis.NextStatus(app.Status, observed) is not { } next) continue;

            var wasCrashed = app.Status == AppStatus.Crashed;
            app.Status = next;
            app.UpdatedAt = now;
            await db.SaveChangesAsync(ct);

            if (next == AppStatus.Crashed)
            {
                var how = observed == ObservedAppState.CrashLooping
                    ? "keeps crashing and being restarted"
                    : "exited unexpectedly";
                await notifications.NotifyAsync(app.WorkspaceId, AlertEvent.AppCrashed, AlertSeverity.Critical,
                    $"App crashed: {app.Name}", $"The container for '{app.Name}' {how}.", ct);
            }
            else if (wasCrashed)
            {
                logger.LogInformation("App {Slug} recovered; status returned to Running.", app.Slug);
            }
        }
    }

    private async Task MaybeDiskAlert(
        Domain.Servers.Server server, long used, long total, DateTimeOffset now, CancellationToken ct)
    {
        // Once per hour per node, so a full disk doesn't spam every tick — and so one node filling
        // up doesn't silence the warning for every other node.
        if (!throttle.ShouldFire($"disk:{server.Id}", now, DiskAlertInterval)) return;

        var pct = (int)((double)used / total * 100);
        foreach (var wsId in await db.Alerts.Where(a => a.IsEnabled && a.OnDiskWarning)
                     .Select(a => a.WorkspaceId).Distinct().ToListAsync(ct))
        {
            await notifications.NotifyAsync(wsId, AlertEvent.DiskWarning, AlertSeverity.Warning,
                "Low disk space", $"Disk usage on {server.Name} is at {pct}%.", ct);
        }
    }

    private static MonitoringMetric Metric(Guid serverId, string name, string? resource, double value, DateTimeOffset ts) =>
        new() { ServerId = serverId, Name = name, ResourceRef = resource, Value = value, Timestamp = ts };
}
