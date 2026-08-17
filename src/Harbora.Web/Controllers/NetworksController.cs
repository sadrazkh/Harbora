using Harbora.Application.Abstractions;
using Harbora.Domain.Authorization;
using Harbora.Data;
using Harbora.Infrastructure.Deployments;
using Harbora.Infrastructure.Networking;
using Harbora.Infrastructure.Services;
using Harbora.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Web.Controllers;

/// <summary>
/// The private networks a workspace runs on, and what sits on each.
///
/// Every environment gets a network of its own, which is the boundary that stops staging reaching
/// production's database by name. That boundary is invisible until something goes wrong, so this
/// page draws it: which network exists, what is attached, and the internal address each service
/// answers on — the one fact people need when wiring two services together and the one they
/// otherwise get by guessing.
/// </summary>
[Authorize]
public sealed class NetworksController(
    HarboraDbContext db,
    ServiceUsageService usage,
    ICurrentUser currentUser) : Controller
{
    private Guid WorkspaceId => currentUser.WorkspaceId ?? Guid.Empty;

    public async Task<IActionResult> Index(Guid? environmentId, CancellationToken ct)
    {
        ViewData["Title"] = "Networks";

        var environments = await db.Environments
            .Include(e => e.Project)
            .Where(e => e.WorkspaceId == WorkspaceId)
            .OrderBy(e => e.Project!.Name).ThenBy(e => e.Name)
            .ToListAsync(ct);

        var selected = environments.FirstOrDefault(e => e.Id == environmentId)
                       ?? environments.FirstOrDefault(e => e.IsDefault)
                       ?? environments.FirstOrDefault();

        var vm = new NetworksViewModel { Environments = environments, Selected = selected };

        if (selected is null) return View(vm);

        vm.Services = await db.Apps
            .Where(a => a.EnvironmentId == selected.Id)
            .Include(a => a.EnvironmentVariables)
            .Include(a => a.Domains)
            .OrderBy(a => a.Name)
            .ToListAsync(ct);

        vm.Databases = await db.ManagedServices
            .Where(s => s.EnvironmentId == selected.Id)
            .OrderBy(s => s.Name)
            .ToListAsync(ct);

        // The Docker network this environment's containers share. Derived from the same rule the
        // deploy engine uses, so the name shown is the name that exists.
        vm.NetworkName = EnvironmentNetwork.For(selected.Project?.Slug, selected.Slug, selected.Id);

        // Connections are worked out here because the values they come from are encrypted.
        vm.Connections = usage.ConnectionsFor(vm.Services, vm.Databases.Select(d => d.ContainerName));
        vm.Picture = ArchitectureGraph.Build(vm.Services, vm.Databases, vm.Connections);

        return View(vm);
    }

    /// <summary>
    /// What moving this service would cost, before anything is changed. Its own screen rather than a
    /// dialog, because the answer is a list of things that will break and a list is not a sentence.
    /// </summary>
    [HttpGet("/networks/move")]
    [Authorize(Policy = Capabilities.AppsDeploy)]
    public async Task<IActionResult> ConfirmMove(Guid appId, Guid targetEnvironmentId, CancellationToken ct)
    {
        var vm = await BuildMoveAsync(appId, targetEnvironmentId, ct);
        if (vm is null) return NotFound();

        ViewData["Title"] = "Move service";
        return View(vm);
    }

    /// <summary>
    /// Applies the move. The verdict is asked for a second time here: the confirmation screen was
    /// rendered from the state of a moment ago, and the thing it warned about may have changed.
    /// </summary>
    [HttpPost("/networks/move")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.AppsDeploy)]
    public async Task<IActionResult> Move(Guid appId, Guid targetEnvironmentId, CancellationToken ct)
    {
        var vm = await BuildMoveAsync(appId, targetEnvironmentId, ct);
        if (vm is null) return NotFound();

        if (!vm.Verdict.Allowed)
        {
            TempData["Error"] = vm.Verdict.Reason;
            return RedirectToAction(nameof(Index));
        }

        vm.Service.EnvironmentId = targetEnvironmentId;
        await db.SaveChangesAsync(ct);

        // Said plainly, because the container is still running on the old network until it is not.
        TempData["Message"] =
            $"{vm.Service.Name} now belongs to {vm.Target.Name}. It keeps running on the old network until you redeploy it.";

        return RedirectToAction(nameof(Index), new { environmentId = targetEnvironmentId });
    }

    /// <summary>
    /// Gathers what the move would break: the databases the service is wired to, and the services
    /// that reach it by name. Both are read from real configuration, never assumed.
    /// </summary>
    private async Task<MoveServiceViewModel?> BuildMoveAsync(Guid appId, Guid targetEnvironmentId, CancellationToken ct)
    {
        var service = await db.Apps
            .Include(a => a.EnvironmentVariables)
            .FirstOrDefaultAsync(a => a.Id == appId && a.WorkspaceId == WorkspaceId, ct);
        if (service is null) return null;

        var target = await db.Environments.Include(e => e.Project)
            .FirstOrDefaultAsync(e => e.Id == targetEnvironmentId && e.WorkspaceId == WorkspaceId, ct);
        if (target is null) return null;

        var current = await db.Environments.Include(e => e.Project)
            .FirstOrDefaultAsync(e => e.Id == service.EnvironmentId, ct);

        // Databases it currently holds a connection to — worked out where the protector is.
        var siblings = await db.ManagedServices
            .Where(s => s.EnvironmentId == service.EnvironmentId).ToListAsync(ct);
        var attached = usage.ConnectionsFor([service], siblings.Select(s => s.ContainerName))
            .TryGetValue(service.Id, out var hosts) ? hosts : [];

        // Services in the old environment that name this one in their own configuration.
        var neighbours = await db.Apps
            .Where(a => a.EnvironmentId == service.EnvironmentId && a.Id != service.Id)
            .Include(a => a.EnvironmentVariables)
            .ToListAsync(ct);
        var dependents = usage.ConnectionsFor(neighbours, [service.Slug])
            .Where(pair => pair.Value.Count > 0)
            .Select(pair => neighbours.First(n => n.Id == pair.Key).Name)
            .ToList();

        return new MoveServiceViewModel
        {
            Service = service,
            Current = current,
            Target = target,
            Verdict = NetworkWiring.CanMove(service.EnvironmentId, targetEnvironmentId, [.. attached], dependents)
        };
    }
}
