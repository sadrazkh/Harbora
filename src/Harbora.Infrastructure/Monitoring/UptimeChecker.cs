using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Monitoring;
using Harbora.Domain.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Harbora.Infrastructure.Monitoring;

/// <summary>
/// 2.1 (2026-09 market-gaps round two): the periodic outside-in HTTP check. The only HTTP probe of a
/// customer's app that existed before this was <c>HealthDiagnosis</c>, and it only ever runs once, at
/// the end of a deploy — nothing afterwards ever asked the app whether it was still answering. This is
/// that missing second half, wired into exactly the alert/incident/notification machinery every other
/// condition in this namespace already uses (see <see cref="AlertEvent.UptimeCheckFailed"/>'s own doc)
/// rather than a second alerting system beside it.
///
/// <para>
/// Structured the same way as <see cref="CertificateWatcher"/>: a <see cref="BackgroundService"/> whose
/// whole per-tick behaviour is a public/internal method, so "did this pass open/close the right
/// incident" is a test that runs in milliseconds rather than something only observable a day later.
/// </para>
/// </summary>
public sealed class UptimeChecker(
    IServiceScopeFactory scopeFactory,
    ILogger<UptimeChecker> logger) : BackgroundService
{
    /// <summary>How often the checker looks for due work. Individual checks run on their own
    /// <see cref="UptimeCheck.IntervalSeconds"/>, decided by <see cref="CheckDueAsync"/> below — this is
    /// only the poll granularity, the same relationship <see cref="Backups.BackupScheduler"/>'s own
    /// five-minute tick has to each schedule's independent interval.</summary>
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How long raw <see cref="UptimeCheckResult"/> rows are kept. Bounded here, on every tick, the same
    /// way <see cref="MetricsCollector.CollectAsync"/> prunes <c>MonitoringMetrics</c> at the end of its
    /// own pass — a table this sub-project adds must not be the next one <c>DataRetentionSweeper</c>'s
    /// own doc describes needing a knob nobody wrote yet.
    /// </summary>
    private static readonly TimeSpan ResultRetention = TimeSpan.FromDays(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(StartupDelay, stoppingToken); } catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(Tick);
        do
        {
            try { await CheckDueAsync(stoppingToken); }
            catch (Exception ex) { logger.LogError(ex, "Uptime check tick failed."); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>
    /// Internal rather than private — the same reasoning <see cref="CertificateWatcher.CheckAllAsync"/>
    /// gives for its own visibility.
    /// </summary>
    internal async Task CheckDueAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();
        var probe = scope.ServiceProvider.GetRequiredService<IUptimeProbe>();
        var notifications = scope.ServiceProvider.GetRequiredService<INotificationService>();
        var incidents = scope.ServiceProvider.GetRequiredService<IncidentService>();
        var clock = scope.ServiceProvider.GetRequiredService<ISystemClock>();
        var dedup = scope.ServiceProvider.GetRequiredService<AlertDedup>();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<MonitoringOptions>>().Value;

        var now = clock.UtcNow;

        // Sessionless background path over every tenant at once, so IgnoreQueryFilters() here is
        // correct rather than merely tolerated — there is no one workspace to scope this particular read
        // to, the identical shape MetricsCollector.EvaluateThresholdsAsync's own unscoped db.Alerts read
        // already uses. Everything this method writes afterwards is scoped explicitly by
        // check.WorkspaceId (never the ambient, always-Guid.Empty filter) — see RunOneAsync.
        var due = await db.UptimeChecks.IgnoreQueryFilters()
            .Where(c => c.IsEnabled && (c.NextCheckAt == null || c.NextCheckAt <= now))
            .ToListAsync(ct);

        if (due.Count > 0)
        {
            var appIds = due.Select(c => c.AppId).Distinct().ToList();
            var apps = await db.Apps.IgnoreQueryFilters()
                .Where(a => appIds.Contains(a.Id))
                .ToDictionaryAsync(a => a.Id, ct);

            foreach (var check in due)
                await RunOneAsync(db, probe, incidents, notifications, dedup, options, check, apps, now, ct);

            await db.SaveChangesAsync(ct);
        }

        await PruneAsync(db, now, ct);
    }

    private async Task RunOneAsync(
        HarboraDbContext db, IUptimeProbe probe, IncidentService incidents, INotificationService notifications,
        AlertDedup dedup, MonitoringOptions options, UptimeCheck check, IReadOnlyDictionary<Guid, App> apps,
        DateTimeOffset now, CancellationToken ct)
    {
        check.LastCheckedAt = now;
        check.NextCheckAt = now.AddSeconds(Math.Max(5, check.IntervalSeconds));

        // The app was deleted after this check was configured, or — belt and braces alongside every
        // IgnoreQueryFilters() read above — a check somehow points at an app outside its own workspace.
        // Either way nothing below is safe to act on in this check's name: no probe, no result row, no
        // incident, no notification.
        if (!apps.TryGetValue(check.AppId, out var app) || app.WorkspaceId != check.WorkspaceId)
        {
            logger.LogWarning(
                "Uptime check {CheckId} points at an app that no longer exists (or is not its own workspace's); skipping.",
                check.Id);
            return;
        }

        // Explicit WorkspaceId ==, on top of the IgnoreQueryFilters() above — DomainName carries no
        // workspace column of its own (it belongs to an App, which was just proven to be check's own
        // workspace), so scoping here is "this app's domains", not "every domain in the database".
        var domain = await db.Domains.IgnoreQueryFilters()
            .Where(d => d.AppId == app.Id)
            .OrderByDescending(d => d.IsPrimary)
            .FirstOrDefaultAsync(ct);

        var result = domain is null
            ? new UptimeProbeResult(ProbeOutcome.CouldNotRun, null, null,
                $"{app.Name} has no public domain configured, so there is nothing to probe.")
            : await probe.ProbeAsync(
                BuildUrl(domain, check.Path), check.ExpectedStatus, check.BodyContains,
                TimeSpan.FromSeconds(Math.Max(1, check.TimeoutSeconds)), ct);

        var outcome = ToOutcome(result.Outcome);

        check.LastOutcome = outcome;
        check.LastHttpStatus = result.HttpStatus;
        check.LastLatencyMs = result.LatencyMs;
        check.LastDetail = result.Detail;

        db.UptimeCheckResults.Add(new UptimeCheckResult
        {
            WorkspaceId = check.WorkspaceId,
            AppId = app.Id,
            UptimeCheckId = check.Id,
            CheckedAt = now,
            Outcome = outcome,
            HttpStatus = result.HttpStatus,
            LatencyMs = result.LatencyMs,
            Detail = result.Detail
        });

        // CouldNotRun raises and resolves nothing — see UptimeCheckOutcome's own doc and
        // AlertEvent.UptimeCheckFailed's: a probe that never got to ask the question is neither proof
        // the app answered wrongly nor proof a standing failure just recovered.
        if (outcome == UptimeCheckOutcome.CouldNotRun) return;

        if (outcome == UptimeCheckOutcome.Up)
        {
            // The free close (2026-08-16 spec §M4's own pattern, reused rather than reinvented): this
            // tick already knows the probe passed, and that is the same fact a resolve needs.
            await incidents.ResolveAsync(check.WorkspaceId, AlertEvent.UptimeCheckFailed, app.Id.ToString(), now, ct);
            return;
        }

        await incidents.OpenAsync(check.WorkspaceId, AlertEvent.UptimeCheckFailed, app.Id.ToString(),
            AlertSeverity.Critical, $"Uptime check failing: {app.Name}", result.Detail, now, ct);

        // Opened (or refreshed) every tick the condition holds, the same as every other collector-driven
        // incident — this dedup window only governs how often the channel itself is pinged.
        var interval = options.UptimeAlertInterval;
        if (interval > TimeSpan.Zero)
        {
            var key = $"uptime:{app.Id}:{AlertDedupWindow.Bucket(now, interval)}";
            if (!await dedup.ShouldFireAsync(key, now, ct)) return;
        }

        var evt = NotificationEventData.Create(AlertEvent.UptimeCheckFailed,
            ("AppName", app.Name), ("Detail", result.Detail));
        await notifications.NotifyAsync(check.WorkspaceId, evt, AlertSeverity.Critical, ct);
    }

    private static Uri BuildUrl(Harbora.Domain.Networking.DomainName domain, string path)
    {
        var scheme = domain.SslEnabled ? "https" : "http";
        var normalized = string.IsNullOrWhiteSpace(path) ? "/" : path;
        if (!normalized.StartsWith('/')) normalized = "/" + normalized;
        return new Uri($"{scheme}://{domain.Host}{normalized}");
    }

    private static UptimeCheckOutcome ToOutcome(ProbeOutcome outcome) => outcome switch
    {
        ProbeOutcome.Up => UptimeCheckOutcome.Up,
        ProbeOutcome.Down => UptimeCheckOutcome.Down,
        _ => UptimeCheckOutcome.CouldNotRun
    };

    private static async Task PruneAsync(HarboraDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        var cutoff = now - ResultRetention;
        // IgnoreQueryFilters(): this sessionless pass has no one workspace to scope a prune to either —
        // every tenant's old rows are equally due, the same reasoning MetricsCollector.CollectAsync's
        // own MonitoringMetrics prune already applies.
        var doomed = db.UptimeCheckResults.IgnoreQueryFilters().Where(r => r.CheckedAt < cutoff);

        // ExecuteDeleteAsync is one statement that never loads a row; the InMemory provider the unit
        // tests use does not implement it, so there is a fallback — see MetricsCollector.CollectAsync's
        // own identical comment for why the predicate, not the deletion mechanism, is what is tested.
        if (db.Database.IsRelational())
        {
            await doomed.ExecuteDeleteAsync(ct);
        }
        else
        {
            var expired = await doomed.ToListAsync(ct);
            if (expired.Count > 0) db.UptimeCheckResults.RemoveRange(expired);
        }
        await db.SaveChangesAsync(ct);
    }
}
