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
    Harbora.Infrastructure.Monitoring.NetworkHistory network,
    IManagedServiceEngine engine,
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
            Attention = await attention.BuildAsync(workspaceId, ct),
            Projects = await db.Projects
                .Where(p => p.WorkspaceId == workspaceId)
                .OrderBy(p => p.Name)
                .Select(p => new ProjectSummary
                {
                    Id = p.Id, Name = p.Name, Slug = p.Slug,
                    Environments = p.Environments.Count,
                    Services = db.Apps.Count(a => a.Environment!.ProjectId == p.Id),
                    Databases = db.ManagedServices.Count(s => s.Environment!.ProjectId == p.Id),
                    Unhealthy = db.Apps.Count(a => a.Environment!.ProjectId == p.Id
                                                   && (a.Status == AppStatus.Crashed || a.Status == AppStatus.Failed))
                })
                .Take(6)
                .ToListAsync(ct),
            Apps = await db.Apps.Where(a => a.WorkspaceId == workspaceId)
                .OrderByDescending(a => a.UpdatedAt).Take(8).ToListAsync(ct),
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

        // Platform health strip: servers, domains/SSL.
        vm.ServersTotal = await db.Servers.CountAsync(ct);
        vm.ServersOnline = await db.Servers.CountAsync(s => s.Status == ServerStatus.Online, ct);
        vm.DomainsTotal = await db.Domains.CountAsync(d => d.App!.WorkspaceId == workspaceId, ct);
        vm.DomainsSsl = await db.Domains.CountAsync(d => d.App!.WorkspaceId == workspaceId && d.SslEnabled, ct);

        var monthStart = new DateTimeOffset(DateTimeOffset.UtcNow.Year, DateTimeOffset.UtcNow.Month, 1, 0, 0, 0, TimeSpan.Zero);
        vm.BackupsThisMonth = await db.Backups.CountAsync(b => b.WorkspaceId == workspaceId && b.CreatedAt >= monthStart, ct);
        vm.BackupSchedulesEnabled = await db.BackupSchedules
            .CountAsync(b => b.WorkspaceId == workspaceId && b.IsEnabled, ct);
        vm.LatestBackupAt = await db.Backups
            .Where(b => b.WorkspaceId == workspaceId && b.Status == BackupStatus.Completed)
            .OrderByDescending(b => b.FinishedAt ?? b.CreatedAt)
            .Select(b => (DateTimeOffset?)(b.FinishedAt ?? b.CreatedAt))
            .FirstOrDefaultAsync(ct);

        vm.RecentDomains = await db.Domains.AsNoTracking()
            .Where(d => d.App!.WorkspaceId == workspaceId)
            .OrderByDescending(d => d.CreatedAt).Take(4)
            .Select(d => new DashboardDomain(d.Host, d.SslEnabled, d.App!.Name, d.AppId))
            .ToListAsync(ct);

        vm.TeamMembers = await db.WorkspaceMembers.AsNoTracking()
            .Where(m => m.WorkspaceId == workspaceId && m.User != null)
            .OrderBy(m => m.Role).ThenBy(m => m.User!.DisplayName).Take(4)
            .Select(m => new DashboardTeamMember(
                string.IsNullOrWhiteSpace(m.User!.DisplayName) ? m.User.Email : m.User.DisplayName,
                m.User.Email, m.Role.ToString()))
            .ToListAsync(ct);

        // Latest aggregate samples from the collector (they survive Docker being briefly down).
        // Cast to a nullable before the default kicks in: on a double, FirstOrDefaultAsync answers
        // "0" for "there are no rows", and the dashboard printed that as a measured 0%.
        vm.CpuPercent = await db.MonitoringMetrics
            .Where(m => m.Name == "cpu.percent" && m.ResourceRef == null)
            .OrderByDescending(m => m.Timestamp).Select(m => (double?)m.Value).FirstOrDefaultAsync(ct);

        vm.MemoryUsed = await db.MonitoringMetrics
            .Where(m => m.Name == "mem.used" && m.ResourceRef == null)
            .OrderByDescending(m => m.Timestamp).Select(m => (long?)m.Value).FirstOrDefaultAsync(ct);

        // The two things the reference dashboard leads with. Read from the templates that are
        // actually installed and enabled — a tile for something that is not there deploys nothing.
        var templates = await db.AppTemplates.AsNoTracking()
            .Where(t => t.IsEnabled)
            .OrderBy(t => t.Name)
            .Select(t => new StarterTemplate(t.Id, t.Key, t.Name, t.NameFa, t.Category, t.IconUrl))
            .ToListAsync(ct);

        vm.StarterApps = templates.Where(t => t.Category != "database").Take(8).ToList();

        // Databases are provisioned from the engine catalog, not from app templates. Reading the
        // catalog means a tile exists only for an engine this installation can really start.
        vm.StarterDatabases = engine.Catalog
            .Take(4)
            .Select(c => new StarterDatabase(c.Type.ToString(), c.DisplayName, c.DisplayNameFa))
            .ToList();

        // Recent errors: failed deploys (with reason) + crashed apps.
        var failed = await db.Deployments.Include(d => d.App)
            .Where(d => d.App!.WorkspaceId == workspaceId && d.Status == DeploymentStatus.Failed)
            .OrderByDescending(d => d.CreatedAt).Take(4).ToListAsync(ct);
        foreach (var d in failed)
            vm.RecentErrors.Add(new DashboardError(
                $"Deploy failed · {d.App?.Name} #{d.Number}",
                d.ErrorMessage ?? "—", d.FinishedAt ?? d.CreatedAt, $"/deployments/details/{d.Id}"));
        var crashed = await db.Apps
            .Where(a => a.WorkspaceId == workspaceId && a.Status == AppStatus.Crashed)
            .OrderByDescending(a => a.UpdatedAt).Take(3).ToListAsync(ct);
        foreach (var a in crashed)
            vm.RecentErrors.Add(new DashboardError($"App crashed · {a.Name}", "Container exited unexpectedly.", a.UpdatedAt, $"/apps/details/{a.Id}"));
        vm.RecentErrors = vm.RecentErrors.OrderByDescending(e => e.At).Take(5).ToList();

        // Live host + Traefik state (best-effort; never crash the dashboard).
        try
        {
            var host = await docker.GetHostInfoAsync(ct);
            vm.DockerAvailable = true;
            vm.DockerVersion = host.DockerVersion;
            vm.MemoryTotal = host.TotalMemoryBytes;
            vm.DiskTotal = host.TotalDiskBytes;
            vm.DiskUsed = host.TotalDiskBytes - host.FreeDiskBytes;
            vm.ContainersRunning = host.ContainersRunning;

            var containers = await docker.ListContainersAsync(null, ct);
            vm.TraefikRunning = containers.Any(c =>
                c.Name.Contains("traefik", StringComparison.OrdinalIgnoreCase) &&
                c.State.Equals("running", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Docker host info unavailable.");
            vm.DockerAvailable = false;
            vm.TraefikRunning = null;
        }

        // Throughput comes from stored counters, not from the live daemon, so it survives Docker
        // being briefly unreachable — and stays null rather than zero when there is nothing to work
        // it out from.
        try
        {
            var server = await db.Servers.IgnoreQueryFilters()
                .Where(s => s.IsLocal).Select(s => (Guid?)s.Id).FirstOrDefaultAsync(ct);

            if (server is { } serverId)
            {
                var since = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(30);
                vm.NetworkInPerSecond = Harbora.Infrastructure.Monitoring.NetworkHistory.Latest(
                    await network.ForAsync(serverId, "net.rx", since, null, ct));
                vm.NetworkOutPerSecond = Harbora.Infrastructure.Monitoring.NetworkHistory.Latest(
                    await network.ForAsync(serverId, "net.tx", since, null, ct));
            }
        }
        catch (Exception ex)
        {
            // A missing rate is a blank panel, never a wrong number.
            logger.LogWarning(ex, "Network throughput unavailable.");
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
