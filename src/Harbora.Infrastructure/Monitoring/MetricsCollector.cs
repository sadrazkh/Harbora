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
    ISystemClock clock,
    ILogger<MetricsCollector> logger) : IMetricsCollector
{
    private const double DiskWarnRatio = 0.85;
    private static readonly TimeSpan Retention = TimeSpan.FromHours(24);
    private static DateTimeOffset _lastDiskAlert = DateTimeOffset.MinValue;

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
                await MaybeDiskAlert(diskUsed, host.TotalDiskBytes, now, ct);
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
            foreach (var c in containers)
            {
                if (c.State.Equals("running", StringComparison.OrdinalIgnoreCase))
                {
                    var stats = await docker.GetStatsAsync(c.Id, ct);
                    if (stats is not null)
                    {
                        totalCpu += stats.CpuPercent;
                        samples.Add(Metric(server.Id, "cpu.percent", c.Name, stats.CpuPercent, now));
                        samples.Add(Metric(server.Id, "mem.used", c.Name, stats.MemoryUsedBytes, now));
                    }
                }
                else if (c.State.Equals("exited", StringComparison.OrdinalIgnoreCase))
                {
                    await DetectCrashAsync(c.Labels, now, ct);
                }
            }
            samples.Add(Metric(server.Id, "cpu.percent", null, Math.Round(totalCpu, 2), now));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Container metrics unavailable for {Server}.", server.Name);
        }

        db.MonitoringMetrics.AddRange(samples);
    }

    private async Task DetectCrashAsync(IReadOnlyDictionary<string, string> labels, DateTimeOffset now, CancellationToken ct)
    {
        if (!labels.TryGetValue("harbora.app", out var slug)) return;
        var app = await db.Apps.FirstOrDefaultAsync(a => a.Slug == slug, ct);
        if (app is null || app.Status == AppStatus.Crashed || app.Status == AppStatus.Stopped) return;

        app.Status = AppStatus.Crashed;
        app.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        await notifications.NotifyAsync(app.WorkspaceId, AlertEvent.AppCrashed, AlertSeverity.Critical,
            $"App crashed: {app.Name}", $"The container for '{app.Name}' exited unexpectedly.", ct);
    }

    private async Task MaybeDiskAlert(long used, long total, DateTimeOffset now, CancellationToken ct)
    {
        // Throttle to once per hour so a full disk doesn't spam every tick.
        if (now - _lastDiskAlert < TimeSpan.FromHours(1)) return;
        _lastDiskAlert = now;

        var pct = (int)((double)used / total * 100);
        foreach (var wsId in await db.Alerts.Where(a => a.IsEnabled && a.OnDiskWarning)
                     .Select(a => a.WorkspaceId).Distinct().ToListAsync(ct))
        {
            await notifications.NotifyAsync(wsId, AlertEvent.DiskWarning, AlertSeverity.Warning,
                "Low disk space", $"Disk usage is at {pct}%.", ct);
        }
    }

    private static MonitoringMetric Metric(Guid serverId, string name, string? resource, double value, DateTimeOffset ts) =>
        new() { ServerId = serverId, Name = name, ResourceRef = resource, Value = value, Timestamp = ts };
}
