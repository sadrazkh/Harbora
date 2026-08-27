using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Common;
using Harbora.Domain.Monitoring;
using Harbora.Domain.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Harbora.Infrastructure.Monitoring;

/// <summary>
/// Samples host + per-container metrics into the time series, watches for crashed apps and low
/// disk, and fires the matching alerts. Old samples are trimmed each pass to bound table growth.
/// </summary>
public sealed class MetricsCollector(
    HarboraDbContext db,
    IServerEngineFactory engineFactory,
    INotificationService notifications,
    IEventPublisher events,
    IncidentService incidents,
    AlertDedup dedup,
    ISystemClock clock,
    MetricsRollupService rollups,
    IOptions<MonitoringOptions> options,
    IQuotaService quota,
    ILogger<MetricsCollector> logger) : IMetricsCollector
{
    private readonly MonitoringOptions _options = options.Value;

    /// <summary>How long raw samples are kept. Beyond this, the summaries answer instead.</summary>
    private static readonly TimeSpan Retention = MetricRollups.RawRetention;

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

        // C2 (2026-08-27 "the outage nobody sees coming"): reads Volume.StorageBytes, a periodic
        // measurement, not the mem.used/cpu.percent samples this tick just wrote — but lives here for
        // the same reason as its sibling above: this is where "is a per-app line crossed" already runs.
        try { await EvaluateDiskThresholdsAsync(now, ct); }
        catch (Exception ex) { logger.LogWarning(ex, "Evaluating per-application disk thresholds failed."); }

        // C1 (2026-08-27 "warn before the refusal"): does not read the samples just written at all —
        // it reads IQuotaService's own committed-capacity snapshot — but lives on this same tick
        // because that is where every other "is a workspace approaching a line" check already runs.
        try { await EvaluateQuotaWarningsAsync(now, ct); }
        catch (Exception ex) { logger.LogWarning(ex, "Evaluating quota warnings failed."); }

        // The bounded backstop for every incident kind, not only the ones this collector opens: a
        // deploy or backup failure nobody acknowledges has no other way to close, and this pass is
        // the one piece of the platform already running on a timer with nothing else asking it to.
        try { await incidents.ExpireStaleAsync(now, _options.IncidentAutoExpireAfter, ct); }
        catch (Exception ex) { logger.LogWarning(ex, "Expiring stale incidents failed."); }

        // Summarise BEFORE pruning, never the other way round. Getting that order wrong loses the
        // history silently: the charts keep working, on data quietly missing a week.
        try { await rollups.RunAsync(ct); }
        catch (Exception ex) { logger.LogError(ex, "Rolling up metrics failed; raw samples were kept."); return; }

        // ExecuteDeleteAsync is one statement that never loads a row, which matters here: this table
        // is every sample of every metric. The InMemory provider the unit tests use does not
        // implement it (DataRetentionSweeper.DeleteAsync hit the same gap and documents the same
        // fix), so there is a fallback for a non-relational provider — which rows is the identical
        // predicate on both paths, so there is no behaviour a test can pass that the real provider
        // would fail.
        var cutoff = now - Retention;
        var doomed = db.MonitoringMetrics.Where(m => m.Timestamp < cutoff);
        if (db.Database.IsRelational())
        {
            await doomed.ExecuteDeleteAsync(ct);
        }
        else
        {
            var expired = await doomed.ToListAsync(ct);
            if (expired.Count > 0) db.MonitoringMetrics.RemoveRange(expired);
        }
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
    ///
    /// <see cref="AlertMetric.DiskPercent"/> is deliberately excluded from this query and evaluated by
    /// <see cref="EvaluateDiskThresholdsAsync"/> instead: every rule reaching the loop below is
    /// required to have a running container (the line just past the app lookup skips one that does
    /// not), which is right for a live <c>cpu.percent</c>/<c>mem.used</c> sample but wrong for a
    /// volume, whose bytes sit on disk whether or not the app is between deployments right now.
    /// </summary>
    private async Task EvaluateThresholdsAsync(DateTimeOffset now, CancellationToken ct)
    {
        var rules = await db.Alerts.IgnoreQueryFilters()
            .Where(a => a.IsEnabled && a.AppId != null && a.Metric != null
                        && a.Metric != AlertMetric.DiskPercent && a.ThresholdPercent > 0)
            .ToListAsync(ct);

        if (rules.Count == 0) return;

        var appIds = rules.Select(a => a.AppId!.Value).Distinct().ToList();
        var rows = await db.Apps.IgnoreQueryFilters()
            .Where(a => appIds.Contains(a.Id))
            .Select(a => new { a.Id, a.Name, a.Slug, a.WorkspaceId, a.ActiveDeploymentId, a.MemoryLimitBytes, a.CpuLimit })
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
                ? Deployments.DeploymentPlanning.ContainerName(r.WorkspaceId, r.Slug, n)
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

            bool breached;
            string subject, body;
            NotificationEventData evt;

            if (rule.Metric == AlertMetric.RestartRate)
            {
                // Not a percentage of anything, and not a "held for the whole window" question
                // either — ThresholdRule.Breached exists for a value sustained across a window, and a
                // restart count is a total accumulated across one, so it does not apply here. See the
                // doc comment on AlertMetric.RestartRate for the field reuse this reads.
                var window = TimeSpan.FromMinutes(Math.Max(1, rule.SustainedMinutes));
                var restarts = raw
                    .Where(m => m.ResourceRef == app.ContainerName && m.Name == "app.restarts"
                                && m.Timestamp > now - window && m.Timestamp <= now)
                    .Sum(m => m.Value);

                breached = restarts >= rule.ThresholdPercent!.Value;
                subject = $"{app.Name}: {restarts:0} restart(s) in {rule.SustainedMinutes} minute(s)";
                body = $"{app.Name} has restarted {restarts:0} time(s) in the last {rule.SustainedMinutes} " +
                       $"minute(s) — at or above the configured {rule.ThresholdPercent:0}.";
                // N4 (2026-08-16 notification-system spec): what happened (restarted N times) and what
                // it happened to (this app), not the sentence above — that sentence still exists only
                // for the incident timeline, which N4 leaves in English (see the class-wide note where
                // NotifyRuleAsync is called).
                evt = NotificationEventData.Create(AlertEvent.ThresholdBreached,
                    ("AppName", app.Name), ("Metric", rule.Metric.ToString()),
                    ("Observed", restarts.ToString("0")), ("SustainedMinutes", rule.SustainedMinutes.ToString()),
                    ("Threshold", rule.ThresholdPercent!.Value.ToString("0")));
            }
            else
            {
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

                breached = ThresholdRule.Breached(
                    samples, rule.ThresholdPercent!.Value,
                    TimeSpan.FromMinutes(Math.Max(0, rule.SustainedMinutes)), now);

                var unit = rule.Metric == AlertMetric.CpuPercent ? "CPU" : "memory";
                subject = $"{app.Name}: {unit} above {rule.ThresholdPercent:0}%";
                body = $"{app.Name} has held above {rule.ThresholdPercent:0}% of its {unit} allocation " +
                       $"for {rule.SustainedMinutes} minute(s).";
                evt = NotificationEventData.Create(AlertEvent.ThresholdBreached,
                    ("AppName", app.Name), ("Metric", rule.Metric.ToString()),
                    ("Threshold", rule.ThresholdPercent!.Value.ToString("0")),
                    ("SustainedMinutes", rule.SustainedMinutes.ToString()));
            }

            if (!breached)
            {
                // Cleared: the next breach is news again rather than waiting out the repeat window.
                if (rule.ThresholdFiredAt is not null) rule.ThresholdFiredAt = null;
                // The free close (2026-08-16 spec §M4): this line already recognised a cleared
                // threshold and, until now, discarded the fact. Wired here rather than reimplemented —
                // the incident for this rule closes the moment the same evaluation that used to just
                // null ThresholdFiredAt runs. Subject is the rule's own id: one Alert row is already
                // exactly one (app, metric) pair, so it doubles as the condition's identity.
                await incidents.ResolveAsync(rule.WorkspaceId, AlertEvent.ThresholdBreached, rule.Id.ToString(), now, ct);
                continue;
            }

            // Opened (or refreshed, if already open) every tick the condition holds, independent of
            // the repeat window below: that window governs how often a channel is pinged, not how
            // long the incident stays open.
            await incidents.OpenAsync(rule.WorkspaceId, AlertEvent.ThresholdBreached, rule.Id.ToString(),
                ThresholdRule.Severity, subject, body, now, ct);

            if (!ThresholdRule.MayRepeat(rule.ThresholdFiredAt, now, _options.ThresholdRepeatAfter)) continue;

            rule.ThresholdFiredAt = now;

            // Through this rule specifically. Broadcasting a threshold to every channel in the
            // workspace would tell people who never asked about this app.
            await notifications.NotifyRuleAsync(rule.Id, evt, ThresholdRule.Severity, ct);
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// C2 (2026-08-27 "the outage nobody sees coming"): "tell me when this app's own volumes hold
    /// above 90% of the size limit set on them" — <see cref="AlertMetric.DiskPercent"/>, split out of
    /// <see cref="EvaluateThresholdsAsync"/> because the figure comes from <c>Volume.StorageBytes</c>
    /// (a periodic measurement written by <c>StorageMeasurer</c>) rather than a live per-tick sample,
    /// so there is no sample window for <see cref="ThresholdRule.Breached"/> to hold a line across —
    /// the latest measurement decides the tick, the same way a restart count decides its own tick
    /// rather than being sustained across one. <see cref="Alert.SustainedMinutes"/> plays no part here.
    ///
    /// Only volumes with <see cref="Domain.Apps.Volume.SizeLimitBytes"/> set count towards either half
    /// of the reading: an uncapped volume has no ceiling for its bytes to be a share of, the same rule
    /// <see cref="AllocationReading"/> already applies to CPU/memory. A capped volume nothing has
    /// measured yet makes the whole reading "not measured" rather than a false 0% — silence, not a
    /// clean bill of health, the same principle <see cref="ThresholdRule"/>'s own doc states for a gap
    /// in a live sample.
    /// </summary>
    private async Task EvaluateDiskThresholdsAsync(DateTimeOffset now, CancellationToken ct)
    {
        var rules = await db.Alerts.IgnoreQueryFilters()
            .Where(a => a.IsEnabled && a.AppId != null && a.Metric == AlertMetric.DiskPercent && a.ThresholdPercent > 0)
            .ToListAsync(ct);
        if (rules.Count == 0) return;

        var appIds = rules.Select(r => r.AppId!.Value).Distinct().ToList();
        var appNames = await db.Apps.IgnoreQueryFilters()
            .Where(a => appIds.Contains(a.Id))
            .Select(a => new { a.Id, a.Name })
            .ToDictionaryAsync(a => a.Id, a => a.Name, ct);
        var volumesByApp = await db.Volumes.AsNoTracking()
            .Where(v => appIds.Contains(v.AppId) && v.SizeLimitBytes > 0)
            .Select(v => new { v.AppId, v.SizeLimitBytes, v.StorageBytes })
            .ToListAsync(ct);

        foreach (var rule in rules)
        {
            if (!appNames.TryGetValue(rule.AppId!.Value, out var appName)) continue;

            var volumes = volumesByApp.Where(v => v.AppId == rule.AppId!.Value).ToList();
            var allocation = volumes.Sum(v => v.SizeLimitBytes!.Value);
            // No capped volume at all: there is no ceiling for anything to be a percentage of, the
            // same as an app with no CPU/memory limit above.
            if (allocation <= 0) continue;
            // A capped volume nothing has measured yet is a gap, not a zero — skip this tick entirely
            // rather than under-report how full the app's disk really is.
            if (volumes.Any(v => v.StorageBytes is null)) continue;

            var used = volumes.Sum(v => v.StorageBytes!.Value);
            var reading = AllocationReading.Of(used, allocation);
            var breached = reading.Kind == AllocationKind.Known && reading.Percent >= rule.ThresholdPercent!.Value;

            var subject = $"{appName}: disk above {rule.ThresholdPercent:0}%";
            var body = $"{appName}'s volumes have held above {rule.ThresholdPercent:0}% of their configured size limit.";
            var evt = NotificationEventData.Create(AlertEvent.ThresholdBreached,
                ("AppName", appName), ("Metric", nameof(AlertMetric.DiskPercent)),
                ("Threshold", rule.ThresholdPercent!.Value.ToString("0")), ("SustainedMinutes", rule.SustainedMinutes.ToString()));

            if (!breached)
            {
                if (rule.ThresholdFiredAt is not null) rule.ThresholdFiredAt = null;
                await incidents.ResolveAsync(rule.WorkspaceId, AlertEvent.ThresholdBreached, rule.Id.ToString(), now, ct);
                continue;
            }

            await incidents.OpenAsync(rule.WorkspaceId, AlertEvent.ThresholdBreached, rule.Id.ToString(),
                ThresholdRule.Severity, subject, body, now, ct);

            if (!ThresholdRule.MayRepeat(rule.ThresholdFiredAt, now, _options.ThresholdRepeatAfter)) continue;

            rule.ThresholdFiredAt = now;
            await notifications.NotifyRuleAsync(rule.Id, evt, ThresholdRule.Severity, ct);
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// C1 (2026-08-27 "warn before the refusal"): tells a workspace it is close to a plan cap before
    /// an action is refused for hitting it. <see cref="QuotaWarningRule.Breaches"/> is where the
    /// figures come from — <see cref="IQuotaService.GetUsageAsync"/>, the same computation a refusal
    /// itself reads, never a second one — and everything below it is the same
    /// open/resolve-plus-repeat-throttle shape every other condition in this class already uses.
    ///
    /// One incident per workspace, not per resource (<c>AlertEvent.QuotaWarning</c>, no subject ref):
    /// a workspace close to two caps at once is one fact — "you are close to your plan" — and a
    /// customer acknowledging or reading it should see every cap currently close, not chase a
    /// separate row per resource the way <see cref="AlertEvent.ThresholdBreached"/>'s per-app rules do.
    /// </summary>
    private async Task EvaluateQuotaWarningsAsync(DateTimeOffset now, CancellationToken ct)
    {
        var workspaceIds = await db.Alerts.IgnoreQueryFilters()
            .Where(a => a.IsEnabled && a.OnQuotaWarning)
            .Select(a => a.WorkspaceId).Distinct().ToListAsync(ct);
        if (workspaceIds.Count == 0) return;

        foreach (var workspaceId in workspaceIds)
        {
            var usage = await quota.GetUsageAsync(workspaceId, ct);
            var breaches = QuotaWarningRule.Breaches(usage, _options.QuotaWarnRatio);

            if (breaches.Count == 0)
            {
                // Cleared, or this workspace never had a cap to be close to at all (no plan, or every
                // watched cap unlimited) — a no-op in the ordinary case where nothing was ever open.
                await incidents.ResolveAsync(workspaceId, AlertEvent.QuotaWarning, null, now, ct);
                continue;
            }

            var subject = $"Workspace nearing its plan limit: {breaches.Max(b => b.Percent)}%";
            var body = "Close to its plan's cap on " +
                       string.Join(", ", breaches.Select(b => $"{b.ResourceEn} ({b.Percent}%, {b.Detail})")) + ".";
            await incidents.OpenAsync(workspaceId, AlertEvent.QuotaWarning, null,
                AlertSeverity.Warning, subject, body, now, ct);

            // Once per interval per workspace — the same reasoning as MaybeDiskAlert below, at the
            // workspace's own grain instead of a server's: a workspace sitting above the line does not
            // need the same fact every collector tick.
            var interval = _options.QuotaAlertInterval;
            if (interval > TimeSpan.Zero)
            {
                var key = $"quota:{workspaceId}:{AlertDedupWindow.Bucket(now, interval)}";
                if (!await dedup.ShouldFireAsync(key, now, ct)) continue;
            }

            var evt = NotificationEventData.Create(AlertEvent.QuotaWarning,
                ("Summary", string.Join(", ", breaches.Select(b => $"{b.ResourceEn} at {b.Percent}% ({b.Detail})"))),
                ("SummaryFa", string.Join("، ", breaches.Select(b => $"{b.ResourceFa} در {b.Percent}٪ ({b.Detail})"))),
                ("Percent", breaches.Max(b => b.Percent).ToString("0")));
            // Every alert in this workspace with OnQuotaWarning set, the same fan-out DiskWarning uses
            // at its own (server) grain — this condition genuinely has no single app or rule to be
            // "through", unlike NotifyRuleAsync's per-app-threshold callers above.
            await notifications.NotifyAsync(workspaceId, evt, AlertSeverity.Warning, ct);
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

            if (host.TotalDiskBytes > 0)
                await UpdateDiskIncidentsAsync(server, diskUsed, host.TotalDiskBytes, now, ct);
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

            // This server's restart cursors, loaded once rather than per container — see
            // ContainerLifecycleCursor for why this bookkeeping exists at all rather than the raw
            // counter becoming a sample.
            var cursors = await db.ContainerLifecycleCursors
                .Where(x => x.ServerId == server.Id)
                .ToDictionaryAsync(x => x.ResourceRef, ct);

            double totalCpu = 0;
            long totalRx = 0, totalTx = 0, totalMemory = 0;
            // Whether anything was actually read this tick. Docker's stats call fails intermittently,
            // and without this the loop falls through with its totals still at zero and records them
            // as a measurement — which reads as "no traffic" and, on the next tick, as a spike back
            // up to the real counter. Unknown is not zero, here least of all.
            var measured = 0;
            foreach (var c in containers.Where(c => c.State.Equals("running", StringComparison.OrdinalIgnoreCase)))
            {
                // Known running from the listing itself, independent of whether the stats probe or
                // the lifecycle call below succeed — an app is not "unmeasured" for uptime purposes
                // just because one of those two timed out this tick.
                samples.Add(Metric(server.Id, "app.up", c.Name, 1, now));
                await RecordRestartDeltaAsync(server.Id, c.Name, docker, cursors, samples, now, ct);

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

            // A container this platform manages but did not just find running: stopped, exited,
            // crash-looping between restarts. Known, not unmeasured — the listing answered, and this
            // is what it said — so it is exactly as real an "app.up" observation as a running one,
            // and skipping it would silently bias uptime upward by only ever recording the good half.
            foreach (var c in containers.Where(c => !c.State.Equals("running", StringComparison.OrdinalIgnoreCase)))
                samples.Add(Metric(server.Id, "app.up", c.Name, 0, now));

            await ReconcileAppStatusesAsync(containers, now, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Container metrics unavailable for {Server}.", server.Name);
        }

        db.MonitoringMetrics.AddRange(samples);
    }

    /// <summary>
    /// Turns this tick's restart count into a sample, via the cursor that remembers last tick's —
    /// see <see cref="ContainerLifecycleCursor"/> and <see cref="RestartDelta"/> for why neither the
    /// raw counter nor a naive subtraction is what gets written.
    /// </summary>
    private async Task RecordRestartDeltaAsync(
        Guid serverId, string containerName, IDockerEngine docker,
        Dictionary<string, ContainerLifecycleCursor> cursors, List<MonitoringMetric> samples,
        DateTimeOffset now, CancellationToken ct)
    {
        ContainerLifecycle? lifecycle;
        try { lifecycle = await docker.GetLifecycleAsync(containerName, ct); }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Restart lifecycle unavailable for {Container}.", containerName);
            return;
        }

        // The engine answered but declined this specific figure (an older node agent, say) — unknown
        // stays unknown rather than becoming a fabricated zero-restart tick.
        if (lifecycle?.RestartCount is not { } restarts) return;

        if (cursors.TryGetValue(containerName, out var cursor))
        {
            var delta = RestartDelta.Between(cursor.LastRestartCount, restarts);
            samples.Add(Metric(serverId, "app.restarts", containerName, delta, now));
            cursor.LastRestartCount = restarts;
            cursor.ObservedAt = now;
            cursor.UpdatedAt = now;
        }
        else
        {
            // First time this container has been seen: there is no baseline to attribute a delta
            // against yet, so none is written — only the baseline itself, for the next tick to diff
            // against. Writing a zero here would claim "zero restarts since we started watching",
            // which is a different fact from "we just started watching".
            var fresh = new ContainerLifecycleCursor
            {
                ServerId = serverId, ResourceRef = containerName, LastRestartCount = restarts, ObservedAt = now
            };
            db.ContainerLifecycleCursors.Add(fresh);
            cursors[containerName] = fresh;
        }
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
                // The incident's own text stays this exact English sentence — the M4 timeline is not
                // in N4's scope (see NotificationTemplateCatalog's class doc). "Reason" below is a
                // machine key ("CrashLooping"/"Exited"), not this phrase: the notification's own
                // template chooses the words per reader, in their own language.
                var how = observed == ObservedAppState.CrashLooping
                    ? "keeps crashing and being restarted"
                    : "exited unexpectedly";
                await incidents.OpenAsync(app.WorkspaceId, AlertEvent.AppCrashed, app.Id.ToString(),
                    AlertSeverity.Critical, $"App crashed: {app.Name}", $"The container for '{app.Name}' {how}.", now, ct);
                var reasonKey = observed == ObservedAppState.CrashLooping ? "CrashLooping" : "Exited";
                await notifications.NotifyAsync(app.WorkspaceId,
                    NotificationEventData.Create(AlertEvent.AppCrashed, ("AppName", app.Name), ("Reason", reasonKey)),
                    AlertSeverity.Critical, ct);
                // P6 (2026-08-20 platform-options plan): the same seam, for a workspace's own event
                // subscriptions rather than its Alert rules. Enqueue only, never throws on its own.
                await events.PublishAsync(app.WorkspaceId, EventKind.AppCrashed,
                    new Dictionary<string, string> { ["app"] = app.Name, ["reason"] = reasonKey }, ct);
            }
            else if (wasCrashed)
            {
                logger.LogInformation("App {Slug} recovered; status returned to Running.", app.Slug);
                // The free close (2026-08-16 spec §M4): this line already recognised a recovered app
                // and, until now, only logged the fact to nobody. Wired here rather than reimplemented.
                await incidents.ResolveAsync(app.WorkspaceId, AlertEvent.AppCrashed, app.Id.ToString(), now, ct);
            }
        }
    }

    /// <summary>
    /// Opens or resolves a disk-warning incident for every workspace whose rule watches this node's
    /// disk — the same audience <see cref="MaybeDiskAlert"/> has always notified (see its own note:
    /// that audience is wider than it should be, and M4 does not change it) — independent of the
    /// hourly notification throttle inside <see cref="MaybeDiskAlert"/>: the throttle governs how
    /// often a channel is pinged, not how long the incident stays open. Disk is grouped with the
    /// per-app threshold and app-crash conditions as one this collector re-evaluates and therefore
    /// already sees clear, every 30 seconds, on its own.
    /// </summary>
    private async Task UpdateDiskIncidentsAsync(
        Domain.Servers.Server server, long used, long total, DateTimeOffset now, CancellationToken ct)
    {
        var workspaceIds = await db.Alerts.Where(a => a.IsEnabled && a.OnDiskWarning)
            .Select(a => a.WorkspaceId).Distinct().ToListAsync(ct);

        var ratio = (double)used / total;
        if (ratio >= _options.DiskWarnRatio)
        {
            var pct = (int)(ratio * 100);
            foreach (var wsId in workspaceIds)
                await incidents.OpenAsync(wsId, AlertEvent.DiskWarning, server.Id.ToString(),
                    AlertSeverity.Warning, "Low disk space", $"Disk usage on {server.Name} is at {pct}%.", now, ct);

            await MaybeDiskAlert(server, workspaceIds, pct, now, ct);
        }
        else
        {
            foreach (var wsId in workspaceIds)
                await incidents.ResolveAsync(wsId, AlertEvent.DiskWarning, server.Id.ToString(), now, ct);
        }
    }

    private async Task MaybeDiskAlert(
        Domain.Servers.Server server, IReadOnlyList<Guid> workspaceIds, int pct, DateTimeOffset now, CancellationToken ct)
    {
        // Once per interval per node (an hour by default), so a full disk doesn't spam every tick —
        // and so one node filling up doesn't silence the warning for every other node. N2 (2026-08-16
        // notification-system spec): the window is baked into the dedup key itself via
        // AlertDedupWindow.Bucket, so a restart between two ticks no longer re-fires — the defect
        // AlertThrottle's own doc comment admitted. Zero (or less) means "never throttle", the same
        // "0 disables the limit" reading every other knob in this platform gives a zero, and a bucket
        // of zero width has no meaning, so that case skips the dedup check entirely rather than asking
        // AlertDedupWindow for one.
        var interval = _options.DiskAlertInterval;
        if (interval > TimeSpan.Zero)
        {
            var key = $"disk:{server.Id}:{AlertDedupWindow.Bucket(now, interval)}";
            if (!await dedup.ShouldFireAsync(key, now, ct)) return;
        }

        var evt = NotificationEventData.Create(AlertEvent.DiskWarning,
            ("ServerName", server.Name), ("Percent", pct.ToString()));
        foreach (var wsId in workspaceIds)
            await notifications.NotifyAsync(wsId, evt, AlertSeverity.Warning, ct);
    }

    private static MonitoringMetric Metric(Guid serverId, string name, string? resource, double value, DateTimeOffset ts) =>
        new() { ServerId = serverId, Name = name, ResourceRef = resource, Value = value, Timestamp = ts };
}
