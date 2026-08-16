using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Common;
using Harbora.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Harbora.Web.Controllers;

/// <summary>
/// Monitoring dashboard: host resources, per-app health, recent/failed deploys, disk + backup
/// warnings, SSL/domains, and alert rules. Charts read the time series via <see cref="Metrics"/>.
/// </summary>
[Authorize]
[Route("monitoring")]
public sealed class MonitoringController(
    HarboraDbContext db,
    IDockerEngine docker,
    ICurrentUser currentUser,
    Harbora.Infrastructure.Security.ProjectAccessService access,
    Harbora.Infrastructure.Maintenance.DiskCleanupService cleanup,
    IAuditLogger audit,
    IOptions<Harbora.Infrastructure.Monitoring.MonitoringOptions> monitoringOptions,
    ILogger<MonitoringController> logger) : Controller
{
    private Guid WorkspaceId => currentUser.WorkspaceId ?? Guid.Empty;

    private static bool IsFa =>
        System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fa";

    /// <summary>
    /// Remove Harbora's own leftover images: orphans of deleted apps, and anything past each
    /// living app's rollback window. The figures reported are the disk's own before/after, because
    /// summed image sizes overstate shared layers.
    /// </summary>
    [HttpPost("cleanup")]
    [Authorize(Policy = Harbora.Domain.Authorization.Capabilities.ServersManage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Cleanup(CancellationToken ct)
    {
        var result = await cleanup.RunAsync(ct);

        await audit.LogAsync("maintenance.disk_cleanup", "server", "local",
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            metadataJson: System.Text.Json.JsonSerializer.Serialize(new
            {
                orphans = result.OrphanRemoved,
                superseded = result.RetentionRemoved,
                refused = result.Failed,
                freedBytes = result.FreedBytes,
                servers = result.Servers
            }), ct: ct);

        var removed = result.OrphanRemoved + result.RetentionRemoved;
        var freed = result.FreedBytes is { } f
            ? Harbora.Infrastructure.Tenancy.ByteSize.Measured(f)
            : (IsFa ? "نامشخص" : "unknown");

        // A server the sweep could not examine is named. Silence would leave it inside the totals as
        // a machine that turned out to be clean, which is the reading somebody acts on.
        var skipped = result.Servers.Where(s => s.Skipped is not null).Select(s => s.ServerName).ToList();
        var note = skipped.Count == 0
            ? string.Empty
            : IsFa
                ? $" این سرورها بررسی نشدند: {string.Join("، ", skipped)}."
                : $" Not examined: {string.Join(", ", skipped)}.";

        TempData["Message"] = (IsFa
            ? $"پاک‌سازی: {removed} ایمیج حذف شد ({result.OrphanRemoved} یتیم، {result.RetentionRemoved} قدیمی)، {result.Failed} در حال استفاده ماند؛ فضای آزادشده: {freed}."
            : $"Cleanup removed {removed} image(s) ({result.OrphanRemoved} orphaned, {result.RetentionRemoved} superseded), {result.Failed} in use and kept; freed: {freed}.") + note;

        return RedirectToAction(nameof(Index));
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        ViewData["Title"] = "Monitoring";
        var options = monitoringOptions.Value;
        var vm = new MonitoringDashboardViewModel { DiskWarnRatio = options.DiskWarnRatio };

        // Container states, keyed by app slug (best-effort; Docker may be down).
        var containerState = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var host = await docker.GetHostInfoAsync(ct);
            vm.DockerAvailable = true;
            vm.DockerVersion = host.DockerVersion;
            vm.DiskTotal = host.TotalDiskBytes;
            vm.DiskUsed = host.TotalDiskBytes - host.FreeDiskBytes;
            vm.MemTotal = host.TotalMemoryBytes;
            vm.ContainersRunning = host.ContainersRunning;

            foreach (var c in await docker.ListContainersAsync("harbora.app", ct))
                if (c.Labels.TryGetValue("harbora.app", out var slug)) containerState[slug] = c.State;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Docker unavailable for monitoring.");
        }

        // Latest aggregate CPU sample, if the collector has run.
        vm.CpuPercent = await db.MonitoringMetrics
            .Where(m => m.Name == "cpu.percent" && m.ResourceRef == null)
            .OrderByDescending(m => m.Timestamp).Select(m => m.Value).FirstOrDefaultAsync(ct);

        var apps = await db.Apps.Where(a => a.WorkspaceId == WorkspaceId).ToListAsync(ct);
        foreach (var app in apps)
        {
            var lastDeploy = await db.Deployments.Where(d => d.AppId == app.Id)
                .OrderByDescending(d => d.Number).Select(d => d.Status.ToString()).FirstOrDefaultAsync(ct);
            vm.Apps.Add(new AppHealth(app.Name, app.Slug, app.Status.ToString(), lastDeploy,
                containerState.GetValueOrDefault(app.Slug, "unknown")) { Id = app.Id });
        }

        vm.RecentDeploys = await db.Deployments.Include(d => d.App)
            .Where(d => d.App!.WorkspaceId == WorkspaceId)
            .OrderByDescending(d => d.CreatedAt).Take(8).ToListAsync(ct);
        vm.FailedDeploys = await db.Deployments
            .CountAsync(d => d.App!.WorkspaceId == WorkspaceId && d.Status == DeploymentStatus.Failed, ct);

        // Backup warning: most recent backup failed, or none within the configured staleness window
        // (MonitoringOptions.BackupStalenessHours — the dashboard's own figure; VerificationSchedule
        // and StorageMeasurer each carry a different backup-staleness number for a different question
        // and are deliberately untouched by this setting).
        var lastBackup = await db.Backups.Where(b => b.WorkspaceId == WorkspaceId)
            .OrderByDescending(b => b.CreatedAt).FirstOrDefaultAsync(ct);
        if (lastBackup is null)
        {
            vm.BackupWarning = true;
            vm.BackupWarningText = IsFa ? "هنوز پشتیبانی گرفته نشده است." : "No backups yet.";
        }
        else if (lastBackup.Status == BackupStatus.Failed)
        {
            vm.BackupWarning = true;
            vm.BackupWarningText = IsFa ? "آخرین پشتیبان‌گیری ناموفق بود." : "Most recent backup failed.";
        }
        else if (lastBackup.FinishedAt is { } finished && DateTimeOffset.UtcNow - finished > options.BackupStaleness)
        {
            vm.BackupWarning = true;
            var hours = (int)Math.Round(options.BackupStaleness.TotalHours);
            vm.BackupWarningText = IsFa
                ? $"در {hours} ساعت گذشته پشتیبان‌گیری موفقی انجام نشده است."
                : $"No successful backup in the last {hours} hours.";
        }

        vm.Domains = await db.Domains.Where(d => d.App!.WorkspaceId == WorkspaceId).ToListAsync(ct);
        vm.Alerts = await db.Alerts.Where(a => a.WorkspaceId == WorkspaceId).ToListAsync(ct);
        return View(vm);
    }

    /// <summary>
    /// Time-series points for a metric, oldest→newest.
    ///
    /// Reads raw samples for a recent window and summaries beyond it — raw points only exist for a
    /// day, and asking for a month of them would return tens of thousands of rows to draw a few
    /// hundred pixels. A summarised point carries its range as well as its average, because a chart
    /// of averages hides exactly the spike someone is looking for.
    /// </summary>
    [HttpGet("metrics")]
    public async Task<IActionResult> Metrics(
        string name, Guid? appId, Guid? serviceId, int minutes = 60, CancellationToken ct = default)
    {
        // Which server, and which container on it.
        //
        // This used to take the container name straight from the query string and filter on it,
        // with nothing but [Authorize] in front. That was survivable only because the one caller
        // never passed a resource — the moment a per-application chart does, a container name
        // becomes the key to another tenant's CPU and memory series. So the caller names a resource
        // it already has the right to see, and the container name is derived here.
        //
        // It also read the *local* server unconditionally, so an application placed on a node
        // charted nothing at all and looked idle rather than unmeasured.
        Guid server;
        string? resource;

        if (appId is { } app)
        {
            if (!await access.CanSeeAppAsync(app, ct)) return Forbid();

            var (appServer, appContainer) = await ContainerForAppAsync(app, ct);
            if (appServer == Guid.Empty) return NotFound();

            server = appServer;
            resource = appContainer;

            // No active deployment yet. Nothing has run, so there is nothing to have measured —
            // and an empty series says that better than the host's numbers would.
            if (resource is null) return Json(Array.Empty<object>());
        }
        else if (serviceId is { } service)
        {
            if (!await access.CanSeeServiceAsync(service, ct)) return Forbid();

            var row = await db.ManagedServices.AsNoTracking()
                .Where(s => s.Id == service)
                .Select(s => new { s.ServerId, s.ContainerName })
                .FirstOrDefaultAsync(ct);
            if (row is null) return NotFound();

            server = row.ServerId;
            resource = row.ContainerName;
        }
        else
        {
            // The host's own series, which is what the monitoring page has always drawn.
            server = await db.Servers.Where(s => s.IsLocal).Select(s => s.Id).FirstOrDefaultAsync(ct);
            resource = null;
        }

        // A year, so "is this a trend" is answerable at all.
        var window = TimeSpan.FromMinutes(Math.Clamp(minutes, 5, 60 * 24 * 365));
        var since = DateTimeOffset.UtcNow - window;

        if (Harbora.Infrastructure.Monitoring.MetricRollups.BestSourceFor(window) is { } period)
        {
            var summarised = await db.MetricRollups
                .Where(r => r.ServerId == server && r.Name == name && r.ResourceRef == resource
                            && r.Period == period && r.PeriodStart >= since)
                .OrderBy(r => r.PeriodStart)
                .Select(r => new
                {
                    t = r.PeriodStart.ToUnixTimeSeconds(),
                    v = r.Average,
                    lo = r.Minimum,
                    hi = r.Maximum
                })
                .ToListAsync(ct);

            return Json(summarised);
        }

        var points = await db.MonitoringMetrics
            .Where(m => m.ServerId == server && m.Name == name && m.ResourceRef == resource && m.Timestamp >= since)
            .OrderBy(m => m.Timestamp)
            .Select(m => new { t = m.Timestamp.ToUnixTimeSeconds(), v = m.Value })
            .ToListAsync(ct);

        return Json(points);
    }

    /// <summary>
    /// The machine an application runs on and the container its metrics are recorded against, or a
    /// null container when it has never been deployed.
    ///
    /// The name is derived from the active deployment rather than stored, which is why it cannot
    /// simply be looked up: it changes with every release, and the metric rows carry whichever one
    /// was running at the time.
    /// </summary>
    private async Task<(Guid Server, string? Container)> ContainerForAppAsync(Guid appId, CancellationToken ct)
    {
        var app = await db.Apps.AsNoTracking()
            .Where(a => a.Id == appId)
            .Select(a => new { a.ServerId, a.WorkspaceId, a.Slug, a.ActiveDeploymentId })
            .FirstOrDefaultAsync(ct);

        if (app is null) return (Guid.Empty, null);
        if (app.ActiveDeploymentId is not { } deploymentId) return (app.ServerId, null);

        var number = await db.Deployments.AsNoTracking()
            .Where(d => d.Id == deploymentId).Select(d => (int?)d.Number).FirstOrDefaultAsync(ct);

        return (app.ServerId, number is { } n
            ? Harbora.Infrastructure.Deployments.DeploymentPlanning.ContainerName(app.WorkspaceId, app.Slug, n)
            : null);
    }
}
