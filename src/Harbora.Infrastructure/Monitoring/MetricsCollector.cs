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
    ILogger<MetricsCollector> logger) : IMetricsCollector
{
    private const double DiskWarnRatio = 0.85;
    private static readonly TimeSpan Retention = TimeSpan.FromHours(24);
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
            foreach (var c in containers.Where(c => c.State.Equals("running", StringComparison.OrdinalIgnoreCase)))
            {
                var stats = await docker.GetStatsAsync(c.Id, ct);
                if (stats is null) continue;
                totalCpu += stats.CpuPercent;
                samples.Add(Metric(server.Id, "cpu.percent", c.Name, stats.CpuPercent, now));
                samples.Add(Metric(server.Id, "mem.used", c.Name, stats.MemoryUsedBytes, now));
            }
            samples.Add(Metric(server.Id, "cpu.percent", null, Math.Round(totalCpu, 2), now));

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
