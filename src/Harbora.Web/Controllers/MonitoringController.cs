using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Common;
using Harbora.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

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
    ILogger<MonitoringController> logger) : Controller
{
    private Guid WorkspaceId => currentUser.WorkspaceId ?? Guid.Empty;

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        ViewData["Title"] = "Monitoring";
        var vm = new MonitoringDashboardViewModel();

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
                containerState.GetValueOrDefault(app.Slug, "unknown")));
        }

        vm.RecentDeploys = await db.Deployments.Include(d => d.App)
            .Where(d => d.App!.WorkspaceId == WorkspaceId)
            .OrderByDescending(d => d.CreatedAt).Take(8).ToListAsync(ct);
        vm.FailedDeploys = await db.Deployments
            .CountAsync(d => d.App!.WorkspaceId == WorkspaceId && d.Status == DeploymentStatus.Failed, ct);

        // Backup warning: most recent backup failed, or none in the last 48h.
        var lastBackup = await db.Backups.Where(b => b.WorkspaceId == WorkspaceId)
            .OrderByDescending(b => b.CreatedAt).FirstOrDefaultAsync(ct);
        if (lastBackup is null)
        {
            vm.BackupWarning = true; vm.BackupWarningText = "No backups yet.";
        }
        else if (lastBackup.Status == BackupStatus.Failed)
        {
            vm.BackupWarning = true; vm.BackupWarningText = "Most recent backup failed.";
        }
        else if (lastBackup.FinishedAt is { } finished && DateTimeOffset.UtcNow - finished > TimeSpan.FromHours(48))
        {
            vm.BackupWarning = true; vm.BackupWarningText = "No successful backup in the last 48 hours.";
        }

        vm.Domains = await db.Domains.Where(d => d.App!.WorkspaceId == WorkspaceId).ToListAsync(ct);
        vm.Alerts = await db.Alerts.Where(a => a.WorkspaceId == WorkspaceId).ToListAsync(ct);
        return View(vm);
    }

    /// <summary>Time-series points for a metric, oldest→newest, for the dashboard charts.</summary>
    [HttpGet("metrics")]
    public async Task<IActionResult> Metrics(string name, string? resource, int minutes = 60, CancellationToken ct = default)
    {
        var server = await db.Servers.Where(s => s.IsLocal).Select(s => s.Id).FirstOrDefaultAsync(ct);
        var since = DateTimeOffset.UtcNow.AddMinutes(-Math.Clamp(minutes, 5, 1440));

        var points = await db.MonitoringMetrics
            .Where(m => m.ServerId == server && m.Name == name && m.ResourceRef == resource && m.Timestamp >= since)
            .OrderBy(m => m.Timestamp)
            .Select(m => new { t = m.Timestamp.ToUnixTimeSeconds(), v = m.Value })
            .ToListAsync(ct);

        return Json(points);
    }
}
