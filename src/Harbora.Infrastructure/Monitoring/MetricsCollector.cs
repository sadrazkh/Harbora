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

        // After sampling and before pruning: the rule reads the very samples just written, and the
        // window it needs is well inside retention.
        try { await EvaluateThresholdsAsync(now, ct); }
        catch (Exception ex) { logger.LogWarning(ex, "Evaluating per-application thresholds failed."); }

        // Summarise BEFORE pruning, never the other way round. Getting that order wrong loses the
        // history silently: the charts keep working, on data quietly missing a week.
        try { await rollups.RunAsync(ct); }
        catch (Exception ex) { logger.LogError(ex, "Rolling up metrics failed; raw samples were kept."); return; }

        var cutoff = now - Retention;
        await db.MonitoringMetrics.Where(m => m.Timestamp < cutoff).ExecuteDeleteAsync(ct);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Per-application thresholds: "tell me when this app holds above 90% of its memory".
    ///
    /// Read with <c>IgnoreQueryFilters</c> throughout. This runs on a timer with no session, and the
    /// workspace filter would return an empty set and report a clean pass over nothing — the trap
    /// this codebase has paid for more than once.
    ///
    /// Percentages come from <see cref="AllocationReading"/>, the same rule the app page uses, so a
    /// figure the panel calls "unmeasured" can never become an alert. An app with no limit has no
    /// percentage to be over, and is skipped rather than treated as 0%.
    /// </summary>
    private async Task EvaluateThresholdsAsync(DateTimeOffset now, CancellationToken ct)
    {
        var rules = await db.Alerts.IgnoreQueryFilters()
            .Where(a => a.IsEnabled && a.AppId != null && a.Metric != null && a.ThresholdPercent > 0)
            .ToListAsync(ct);

        if (rules.Count == 0) return;

        var appIds = rules.Select(a => a.AppId!.Value).Distinct().ToList();
        var rows = await db.Apps.IgnoreQueryFilters()
            .Where(a => appIds.Contains(a.Id))
            .Select(a => new { a.Id, a.Name, a.Slug, a.ActiveDeploymentId, a.MemoryLimitBytes, a.CpuLimit })
            .ToListAsync(ct);

        // The samples are keyed by container name, which is slug + deployment number — the same
        // derivation the per-app charts use. An app between deployments has no container and so no
        // samples, which is silence rather than a breach.
        var activeIds = rows.Where(r => r.ActiveDeploymentId is not null)
            .Select(r => r.ActiveDeploymentId!.Value).ToList();
        var numbers = await db.Deployments.IgnoreQueryFilters()
            .Where(d => activeIds.Contains(d.Id))
            .Select(d => new { d.Id, d.Number })
            .ToDictionaryAsync(d => d.Id, d => d.Number, ct);

        var apps = rows.Select(r => new
        {
            r.Id,
            r.Name,
            r.MemoryLimitBytes,
            r.CpuLimit,
            ContainerName = r.ActiveDeploymentId is { } d && numbers.TryGetValue(d, out var n)
                ? Deployments.DeploymentPlanning.ContainerName(r.Slug, n)
                : null
        }).ToList();

        // One read for the longest window any rule asks for, rather than a query per rule.
        var widest = TimeSpan.FromMinutes(Math.Max(1, rules.Max(r => r.SustainedMinutes)));
        var since = now - widest - ThresholdRule.Tolerance;

        var containerNames = apps.Select(a => a.ContainerName).Where(n => !string.IsNullOrEmpty(n)).ToList();
        var raw = await db.MonitoringMetrics.AsNoTracking()
            .Where(m => m.Timestamp >= since && m.ResourceRef != null && containerNames.Contains(m.ResourceRef))
            .Select(m => new { m.Name, m.ResourceRef, m.Value, m.Timestamp })
            .ToListAsync(ct);

        foreach (var rule in rules)
        {
            var app = apps.FirstOrDefault(a => a.Id == rule.AppId);
            if (app is null || string.IsNullOrEmpty(app.ContainerName)) continue;

            (string Series, double Allocation) watched = rule.Metric == AlertMetric.CpuPercent
                ? ("cpu.percent", app.CpuLimit * 100)
                : ("mem.used", app.MemoryLimitBytes);
            var (series, allocation) = watched;

            // No allocation means no percentage. Alerting on an unlimited app would be inventing a
            // ceiling nobody set.
            if (allocation <= 0) continue;

            var samples = raw
                .Where(m => m.ResourceRef == app.ContainerName && m.Name == series)
                .Select(m => new MetricSample(
                    m.Timestamp,
                    AllocationReading.Of(m.Value, allocation) is { Kind: AllocationKind.Known } r ? r.Percent : null))
                .ToList();

            var breached = ThresholdRule.Breached(
                samples, rule.ThresholdPercent!.Value,
                TimeSpan.FromMinutes(Math.Max(0, rule.SustainedMinutes)), now);

            if (!breached)
            {
                // Cleared: the next breach is news again rather than waiting out the repeat window.
                if (rule.ThresholdFiredAt is not null) rule.ThresholdFiredAt = null;
                continue;
            }

            if (!ThresholdRule.MayRepeat(rule.ThresholdFiredAt, now)) continue;

            rule.ThresholdFiredAt = now;

            var unit = rule.Metric == AlertMetric.CpuPercent ? "CPU" : "memory";
            // Through this rule specifically. Broadcasting a threshold to every channel in the
            // workspace would tell people who never asked about this app.
            await notifications.NotifyRuleAsync(rule.Id, ThresholdRule.Severity,
                $"{app.Name}: {unit} above {rule.ThresholdPercent:0}%",
                $"{app.Name} has held above {rule.ThresholdPercent:0}% of its {unit} allocation " +
                $"for {rule.SustainedMinutes} minute(s).", ct);
        }

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

            server.Architecture = ReportedFact.Keep(server.Architecture, host.Architecture);

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
