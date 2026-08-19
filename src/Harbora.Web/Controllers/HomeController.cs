using System.Diagnostics;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Common;
using Harbora.Web.Models;
using Harbora.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Web.Controllers;

[Authorize]
public sealed class HomeController(
    HarboraDbContext db,
    IDockerEngine docker,
    Harbora.Infrastructure.Dashboard.AttentionService attention,
    ICurrentUser currentUser,
    ILogger<HomeController> logger) : Controller
{
    /// <summary>
    /// The root URL serves two audiences: a visitor who has never heard of this install gets the
    /// public site, a signed-in user gets their dashboard. Previously anonymous visitors were bounced
    /// straight to a login form, which tells them nothing about what they'd be logging into.
    /// </summary>
    [AllowAnonymous]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        if (!currentUser.IsAuthenticated)
            return await LandingAsync(ct);

        var workspaceId = currentUser.WorkspaceId ?? Guid.Empty;

        // The list the page opens with: only findings someone can act on.
        var vm = new DashboardViewModel
        {
            Attention = await attention.BuildAsync(workspaceId, ct,
                isOperator: User.IsInRole("Owner") || User.IsInRole("Admin")),
            RecentDeployments = await db.Deployments
                .Include(d => d.App)
                .Where(d => d.App!.WorkspaceId == workspaceId)
                .OrderByDescending(d => d.CreatedAt).Take(6).ToListAsync(ct)
        };

        vm.AppCount = await db.Apps.CountAsync(a => a.WorkspaceId == workspaceId, ct);
        vm.ProjectCount = await db.Projects.CountAsync(p => p.WorkspaceId == workspaceId, ct);
        vm.DatabaseCount = await db.ManagedServices.CountAsync(s => s.WorkspaceId == workspaceId, ct);
        vm.HealthyDatabaseCount = await db.ManagedServices
            .CountAsync(s => s.WorkspaceId == workspaceId && s.Status == ServiceStatus.Running, ct);
        vm.RunningCount = await db.Apps.CountAsync(a => a.WorkspaceId == workspaceId && a.Status == AppStatus.Running, ct);
        vm.DeploymentsTotal = await db.Deployments.CountAsync(d => d.App!.WorkspaceId == workspaceId, ct);
        vm.FailedDeployments = await db.Deployments
            .CountAsync(d => d.App!.WorkspaceId == workspaceId && d.Status == DeploymentStatus.Failed, ct);

        // Platform health strip: servers, domains/SSL — the fifth cell of the redesigned stat bar.
        vm.ServersTotal = await db.Servers.CountAsync(ct);
        vm.ServersOnline = await db.Servers.CountAsync(s => s.Status == ServerStatus.Online, ct);
        vm.DomainsTotal = await db.Domains.CountAsync(d => d.App!.WorkspaceId == workspaceId, ct);

        vm.BackupSchedulesEnabled = await db.BackupSchedules
            .CountAsync(b => b.WorkspaceId == workspaceId && b.IsEnabled, ct);

        // ---- Resources in Production (3a redesign): apps and databases, one table -------------
        // Reuses the exact row shape /apps and /databases already build from — same metric join,
        // same "no metrics" honesty — rather than a second, dashboard-only formatting of the same
        // facts drifting out of step with the pages that own them.
        var resourceApps = await db.Apps.AsNoTracking()
            .Where(a => a.WorkspaceId == workspaceId)
            .Include(a => a.Domains.Where(d => d.IsPrimary))
            .Include(a => a.Deployments.OrderByDescending(d => d.Number).Take(1))
            .OrderByDescending(a => a.UpdatedAt)
            .Take(4)
            .ToListAsync(ct);

        var activeDeploymentIds = resourceApps
            .Where(a => a.ActiveDeploymentId is not null && a.SourceType != AppSourceType.DockerCompose)
            .Select(a => a.ActiveDeploymentId!.Value)
            .ToList();
        var activeNumbers = activeDeploymentIds.Count == 0
            ? new Dictionary<Guid, int>()
            : await db.Deployments.AsNoTracking()
                .Where(d => activeDeploymentIds.Contains(d.Id))
                .ToDictionaryAsync(d => d.Id, d => d.Number, ct);
        var appMetricRefs = resourceApps
            .Where(a => a.ActiveDeploymentId is { } id && activeNumbers.ContainsKey(id))
            .ToDictionary(
                a => a.Id,
                a => Harbora.Infrastructure.Deployments.DeploymentPlanning.ContainerName(
                    a.WorkspaceId, a.Slug, activeNumbers[a.ActiveDeploymentId!.Value]));
        var appResourceRefs = appMetricRefs.Values.Distinct().ToList();
        var appMetrics = appResourceRefs.Count == 0
            ? new List<Harbora.Domain.Monitoring.MonitoringMetric>()
            : await db.MonitoringMetrics.AsNoTracking()
                .Where(m => m.ResourceRef != null && appResourceRefs.Contains(m.ResourceRef)
                            && (m.Name == "cpu.percent" || m.Name == "mem.used"))
                .OrderByDescending(m => m.Timestamp).Take(500).ToListAsync(ct);

        vm.ResourceApps = resourceApps.Select(a =>
        {
            var deployment = a.Deployments.OrderByDescending(d => d.Number).FirstOrDefault();
            appMetricRefs.TryGetValue(a.Id, out var metricRef);
            var cpu = metricRef is null ? null : appMetrics
                .FirstOrDefault(m => m.ResourceRef == metricRef && m.Name == "cpu.percent")?.Value;
            var memory = metricRef is null ? null : appMetrics
                .FirstOrDefault(m => m.ResourceRef == metricRef && m.Name == "mem.used")?.Value;
            return new ApplicationRowViewModel(
                a.Id, a.Name, a.Slug, a.SourceType, a.Kind, a.Status,
                "—", "—",
                a.Domains.FirstOrDefault(d => d.IsPrimary)?.Host,
                a.InstanceSizeKey, deployment?.Status, deployment?.Number,
                deployment?.FinishedAt ?? deployment?.CreatedAt, null,
                CanOperate: true, cpu, memory is null ? null : (long?)memory.Value, a.MemoryLimitBytes);
        }).ToList();

        var resourceDbs = await db.ManagedServices.AsNoTracking()
            .Where(s => s.WorkspaceId == workspaceId)
            .OrderByDescending(s => s.CreatedAt)
            .Take(4)
            .ToListAsync(ct);
        var dbTargetRefs = resourceDbs.Select(s => s.Id.ToString()).ToList();
        var dbLastBackups = dbTargetRefs.Count == 0
            ? new List<Harbora.Domain.Backups.Backup>()
            : await db.Backups.AsNoTracking()
                .Where(b => dbTargetRefs.Contains(b.TargetRef))
                .OrderByDescending(b => b.CreatedAt)
                .GroupBy(b => b.TargetRef)
                .Select(g => g.OrderByDescending(b => b.CreatedAt).First())
                .ToListAsync(ct);

        vm.ResourceDatabases = resourceDbs.Select(s =>
        {
            var lastBackup = dbLastBackups.FirstOrDefault(b => b.TargetRef == s.Id.ToString());
            return new DatabaseRowViewModel(
                s.Id, s.Name, s.Type, s.Version, s.Status, "—", "—",
                s.ContainerName, s.InternalPort, s.Username, s.DatabaseName, s.VolumeName,
                s.StorageBytes, s.StorageMeasuredAt, null, null, LinkedApps: 0,
                lastBackup?.FinishedAt ?? lastBackup?.CreatedAt, lastBackup?.Status,
                s.MemoryLimitBytes, s.ErrorMessage);
        }).ToList();

        // Live Docker reachability — the in-page banner below, and nothing else on this page depends
        // on it: everything the old monitoring/backups/team summary panels showed is one click away
        // on /monitoring, /backups and /users, which is where that detail actually lives.
        try
        {
            await docker.GetHostInfoAsync(ct);
            vm.DockerAvailable = true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Docker host info unavailable.");
            vm.DockerAvailable = false;
        }

        return View(vm);
    }

    /// <summary>
    /// Public marketing page. Plans are read from the database so the page describes what this
    /// installation actually offers rather than a hard-coded price list.
    /// </summary>
    private async Task<IActionResult> LandingAsync(CancellationToken ct)
    {
        var plans = await db.Plans.Where(p => p.IsEnabled)
            .OrderBy(p => p.MonthlyPrice)
            .AsNoTracking()
            .ToListAsync(ct);

        return View("Landing", new LandingViewModel { Plans = plans });
    }

    /// <summary>Unhandled exception page. Rendered in the app's own shell, not a bare stack of text.</summary>
    [AllowAnonymous]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        Response.StatusCode = StatusCodes.Status500InternalServerError;
        return View("Error", new ErrorViewModel
        {
            RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier,
            StatusCode = StatusCodes.Status500InternalServerError,
            OriginalPath = HttpContext.Features
                .Get<IExceptionHandlerPathFeature>()?.Path
        });
    }

    /// <summary>
    /// Status codes that never reached an action (404 above all). Re-executed by
    /// <c>UseStatusCodePagesWithReExecute</c>, so the response keeps its real status code while the
    /// body is the themed page — a bare "404" gives the user nothing to act on.
    /// </summary>
    [AllowAnonymous]
    [Route("/error/{code:int}")]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult HttpStatus(int code)
    {
        Response.StatusCode = code;
        return View("Error", new ErrorViewModel
        {
            RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier,
            StatusCode = code,
            OriginalPath = HttpContext.Features
                .Get<IStatusCodeReExecuteFeature>()?.OriginalPath
        });
    }
}
