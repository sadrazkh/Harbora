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
                .OrderByDescending(a => a.UpdatedAt).Take(12).ToListAsync(ct),
            RecentDeployments = await db.Deployments
                .Include(d => d.App)
                .Where(d => d.App!.WorkspaceId == workspaceId)
                .OrderByDescending(d => d.CreatedAt).Take(8).ToListAsync(ct)
        };
        vm.AppCount = await db.Apps.CountAsync(a => a.WorkspaceId == workspaceId, ct);
        vm.RunningCount = await db.Apps.CountAsync(a => a.WorkspaceId == workspaceId && a.Status == AppStatus.Running, ct);
        vm.FailedDeployments = vm.RecentDeployments.Count(d => d.Status == DeploymentStatus.Failed);

        try
        {
            var host = await docker.GetHostInfoAsync(ct);
            vm.DockerAvailable = true;
            vm.DockerVersion = host.DockerVersion;
            vm.MemoryTotal = host.TotalMemoryBytes;
            vm.DiskTotal = host.TotalDiskBytes;
            vm.DiskUsed = host.TotalDiskBytes - host.FreeDiskBytes;
        }
        catch (Exception ex)
        {
            // Docker not reachable (e.g. local dev without Docker) — show a friendly state, don't crash.
            logger.LogWarning(ex, "Docker host info unavailable.");
            vm.DockerAvailable = false;
        }

        return View(vm);
    }

    [AllowAnonymous]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error() =>
        View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
}
