using System.Diagnostics;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Common;
using Harbora.Web.Models;
using Harbora.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Web.Controllers;

[Authorize]
public sealed class HomeController(
    HarboraDbContext db,
    IDockerEngine docker,
    ICurrentUser currentUser,
    ILogger<HomeController> logger) : Controller
{
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var workspaceId = currentUser.WorkspaceId ?? Guid.Empty;

        var vm = new DashboardViewModel
        {
            Apps = await db.Apps.Where(a => a.WorkspaceId == workspaceId)
                .OrderByDescending(a => a.UpdatedAt).Take(8).ToListAsync(ct),
            RecentDeployments = await db.Deployments
                .Include(d => d.App)
                .Where(d => d.App!.WorkspaceId == workspaceId)
                .OrderByDescending(d => d.CreatedAt).Take(6).ToListAsync(ct)
        };

        vm.AppCount = await db.Apps.CountAsync(a => a.WorkspaceId == workspaceId, ct);
        vm.RunningCount = await db.Apps.CountAsync(a => a.WorkspaceId == workspaceId && a.Status == AppStatus.Running, ct);
        vm.DeploymentsTotal = await db.Deployments.CountAsync(d => d.App!.WorkspaceId == workspaceId, ct);
        vm.FailedDeployments = await db.Deployments
            .CountAsync(d => d.App!.WorkspaceId == workspaceId && d.Status == DeploymentStatus.Failed, ct);

        // Platform health strip: servers, domains/SSL.
        vm.ServersTotal = await db.Servers.CountAsync(ct);
        vm.ServersOnline = await db.Servers.CountAsync(s => s.Status == ServerStatus.Online, ct);
        vm.DomainsTotal = await db.Domains.CountAsync(d => d.App!.WorkspaceId == workspaceId, ct);
        vm.DomainsSsl = await db.Domains.CountAsync(d => d.App!.WorkspaceId == workspaceId && d.SslEnabled, ct);

        // Latest aggregate CPU sample from the collector (survives Docker being briefly down).
        vm.CpuPercent = await db.MonitoringMetrics
            .Where(m => m.Name == "cpu.percent" && m.ResourceRef == null)
            .OrderByDescending(m => m.Timestamp).Select(m => m.Value).FirstOrDefaultAsync(ct);

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

        return View(vm);
    }

    [AllowAnonymous]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error() =>
        View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
}
