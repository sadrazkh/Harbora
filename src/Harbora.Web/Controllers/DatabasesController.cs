using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Authorization;
using Harbora.Domain.Common;
using Harbora.Domain.Services;
using Harbora.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Web.Controllers;

/// <summary>
/// Managed backing services (databases/caches). Harbora generates credentials, provisions the
/// container on the shared network, and can inject connection env into apps on attach.
/// </summary>
[Authorize]
[Route("databases")]
public sealed partial class DatabasesController(
    HarboraDbContext db,
    IManagedServiceEngine engine,
    IQuotaService quota,
    ISecretProtector protector,
    Harbora.Infrastructure.Projects.ProjectService projects,
    Harbora.Infrastructure.Services.ServiceUsageService usage,
    Harbora.Infrastructure.Security.ProjectAccessService access,
    ICurrentUser currentUser) : Controller
{
    private Guid WorkspaceId => currentUser.WorkspaceId ?? Guid.Empty;

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        ViewData["Title"] = "Databases";

        var query = db.ManagedServices.Where(s => s.WorkspaceId == WorkspaceId);
        if (await access.VisibleProjectIdsAsync(ct) is { } visible)
            query = query.Where(s => s.EnvironmentId != null && visible.Contains(s.Environment!.ProjectId));

        var services = await query.OrderByDescending(s => s.CreatedAt).ToListAsync(ct);
        return View(services);
    }

    [HttpGet("create")]
    [Authorize(Policy = Capabilities.DatabasesManage)]
    public IActionResult Create()
    {
        ViewData["Title"] = "New service";
        ViewBag.Catalog = engine.Catalog;
        return View(new CreateServiceViewModel());
    }

    [HttpPost("create")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.DatabasesManage)]
    public async Task<IActionResult> Create(CreateServiceViewModel model, CancellationToken ct)
    {
        var entry = engine.Catalog.FirstOrDefault(c => c.Type == model.Type);
        if (entry is null) ModelState.AddModelError(nameof(model.Type), "Unknown service type.");

        var check = await quota.CanAddServiceAsync(WorkspaceId, ct);
        if (!check.Allowed) ModelState.AddModelError(string.Empty, check.Reason ?? "Plan quota exceeded.");

        if (!ModelState.IsValid) { ViewBag.Catalog = engine.Catalog; return View(model); }

        var slug = Slugify(model.Name);
        if (await db.ManagedServices.AnyAsync(s => s.WorkspaceId == WorkspaceId && s.ContainerName == $"harbora-svc-{slug}", ct))
        {
            ModelState.AddModelError(nameof(model.Name), "A service with this name already exists.");
            ViewBag.Catalog = engine.Catalog;
            return View(model);
        }

        var serverId = await db.Servers.Where(s => s.IsLocal).Select(s => s.Id).FirstAsync(ct);
        var environment = await projects.ResolveEnvironmentAsync(WorkspaceId, model.EnvironmentId, ct);
        var service = new ManagedService
        {
            WorkspaceId = WorkspaceId,
            EnvironmentId = environment.Id,
            ServerId = serverId,
            Name = model.Name,
            Type = model.Type,
            Version = string.IsNullOrWhiteSpace(model.Version) ? entry!.Versions[0] : model.Version,
            Status = ServiceStatus.Provisioning,
            ContainerName = $"harbora-svc-{slug}",
            VolumeName = $"harbora-svc-{slug}-data",
            InternalPort = entry!.InternalPort,
            Username = "harbora",
            DatabaseName = entry.HasDatabaseName ? slug.Replace('-', '_') : string.Empty,
            EncryptedPassword = protector.Protect(Harbora.Infrastructure.Services.ServiceCredentials.Generate())
        };
        db.ManagedServices.Add(service);
        await db.SaveChangesAsync(ct);

        await engine.QueueProvisionAsync(service.Id, ct);
        return RedirectToAction(nameof(Details), new { id = service.Id });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Details(Guid id, bool reveal = false, CancellationToken ct = default)
    {
        // Visibility rather than a management capability: a viewer on this project may look.
        if (!await access.CanSeeServiceAsync(id, ct)) return NotFound();

        var service = await db.ManagedServices.FirstOrDefaultAsync(s => s.Id == id && s.WorkspaceId == WorkspaceId, ct);
        if (service is null) return NotFound();

        ViewData["Title"] = service.Name;
        var conn = await engine.GetConnectionInfoAsync(id, ct);
        ViewBag.Connection = reveal ? conn.ConnectionString : conn.ConnectionStringMasked;
        ViewBag.Reveal = reveal;
        ViewBag.Apps = await db.Apps.Where(a => a.WorkspaceId == WorkspaceId)
            .Select(a => new { a.Id, a.Name }).ToListAsync(ct);

        // Which private network this address only works on. Saying it plainly is the difference
        // between "the host is wrong" and "the service is somewhere else".
        ViewBag.Network = service.EnvironmentId is { } environmentId
            ? await db.Environments.Where(e => e.Id == environmentId)
                .Select(e => Harbora.Infrastructure.Networking.EnvironmentNetwork.For(e.Project!.Slug, e.Slug, e.Id))
                .FirstOrDefaultAsync(ct)
            : null;

        // Which services actually hold this database's address — from their real environment, not
        // from a list of who once pressed Attach.
        ViewBag.UsedBy = (await usage.AppsUsingAsync(id, ct)).Select(a => a.Name).ToList();
        return View(service);
    }

    [HttpPost("{id:guid}/start")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.DatabasesManage)]
    public async Task<IActionResult> Start(Guid id, CancellationToken ct)
    { await Guard(id, ct); await engine.StartAsync(id, ct); return RedirectToAction(nameof(Details), new { id }); }

    [HttpPost("{id:guid}/stop")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.DatabasesManage)]
    public async Task<IActionResult> Stop(Guid id, CancellationToken ct)
    { await Guard(id, ct); await engine.StopAsync(id, ct); return RedirectToAction(nameof(Details), new { id }); }

    [HttpPost("{id:guid}/remove")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.DatabasesManage)]
    public async Task<IActionResult> Remove(Guid id, bool deleteData, string? confirmName, CancellationToken ct)
    {
        await Guard(id, ct);
        var svc = await db.ManagedServices.FirstOrDefaultAsync(s => s.Id == id && s.WorkspaceId == WorkspaceId, ct);
        if (svc is null) return NotFound();

        // Typing the name is asked for only when the data goes with it — see ServiceRemovalPlan.
        if (!Harbora.Infrastructure.Services.ServiceRemovalPlan.IsConfirmed(deleteData, confirmName, svc.Name))
        {
            TempData["Error"] = $"To delete the data, type the name exactly: {svc.Name}";
            return RedirectToAction(nameof(ConfirmRemove), new { id, deleteData });
        }

        await engine.RemoveAsync(id, deleteData, ct);
        TempData["Message"] = deleteData
            ? $"{svc.Name} and its data were deleted."
            : $"{svc.Name} was removed. Its data is still on the server in volume \"{svc.VolumeName}\".";
        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// What removing this database will actually do — which apps stop working, and what becomes of
    /// the data. It replaced a browser dialog that asked "Remove service?" and said nothing else.
    /// </summary>
    [HttpGet("{id:guid}/remove")]
    [Authorize(Policy = Capabilities.DatabasesManage)]
    public async Task<IActionResult> ConfirmRemove(Guid id, bool deleteData, CancellationToken ct)
    {
        var svc = await db.ManagedServices.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id && s.WorkspaceId == WorkspaceId, ct);
        if (svc is null) return NotFound();

        var users = await usage.AppsUsingAsync(id, ct);
        ViewBag.Service = svc;
        ViewBag.Plan = Harbora.Infrastructure.Services.ServiceRemovalPlan.Describe(
            deleteData, svc.VolumeName, users.Select(a => a.Name).ToList());
        ViewData["Title"] = $"Remove {svc.Name}";
        return View();
    }

    /// <summary>Injects the service's connection env into an app (secret, encrypted). Applies on next deploy.</summary>
    /// <summary>
    /// Connects to the database from its own private network. Everything else on the page is
    /// configuration; this is the only part that can say whether it works.
    /// </summary>
    [HttpPost("{id:guid}/test")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.DatabasesManage)]
    public async Task<IActionResult> TestConnection(Guid id, CancellationToken ct)
    {
        await Guard(id, ct);
        var failure = await engine.TestConnectionAsync(id, ct);
        TempData[failure is null ? "Message" : "Error"] =
            failure ?? "Connected. Credentials accepted and the database answered.";
        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>Measures the data volume now. Explicit, because it walks the whole directory.</summary>
    [HttpPost("{id:guid}/measure")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.DatabasesManage)]
    public async Task<IActionResult> Measure(Guid id, CancellationToken ct)
    {
        await Guard(id, ct);
        var bytes = await engine.MeasureStorageAsync(id, ct);
        TempData[bytes is null ? "Error" : "Message"] = bytes is null
            ? "The size could not be measured. The data volume may not exist yet."
            : $"Data size: {Harbora.Infrastructure.Services.StorageMeasurement.Describe(bytes)}.";
        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>
    /// Replaces the password and rewrites it into every service that was attached to this database.
    /// Those services keep the old value until they are redeployed, which is what the message says.
    /// </summary>
    [HttpPost("{id:guid}/rotate")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.DatabasesManage)]
    public async Task<IActionResult> Rotate(Guid id, CancellationToken ct)
    {
        await Guard(id, ct);
        try
        {
            var updated = await engine.RotatePasswordAsync(id, ct);
            TempData["Message"] = updated.Count == 0
                ? "The password was changed. No service had it stored."
                : $"The password was changed and written into: {string.Join(", ", updated)}. " +
                  "Redeploy them to pick it up — until then they are still using the old one.";
        }
        catch (InvalidOperationException ex)
        {
            TempData["Error"] = ex.Message;
        }
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost("{id:guid}/attach")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.DatabasesManage)]
    public async Task<IActionResult> Attach(Guid id, Guid appId, CancellationToken ct)
    {
        await Guard(id, ct);
        var app = await db.Apps.Include(a => a.EnvironmentVariables)
            .FirstOrDefaultAsync(a => a.Id == appId && a.WorkspaceId == WorkspaceId, ct);
        if (app is null) return NotFound();

        var env = await engine.BuildAttachEnvAsync(id, ct);
        foreach (var (key, value) in env)
        {
            var existing = app.EnvironmentVariables.FirstOrDefault(e => e.Key == key);
            if (existing is null)
                app.EnvironmentVariables.Add(new EnvironmentVariable { Key = key, Value = protector.Protect(value), IsSecret = true });
            else { existing.Value = protector.Protect(value); existing.IsSecret = true; }
        }
        await db.SaveChangesAsync(ct);
        TempData["Message"] = $"Attached to {app.Name}. Redeploy the app to apply the new variables.";
        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>
    /// Ownership is not the whole question: a member scoped to projects belongs to the tenant that
    /// owns this database and still must not touch a project nobody put them on.
    /// </summary>
    private async Task Guard(Guid id, CancellationToken ct)
    {
        if (!await access.CanTouchServiceAsync(id, Capabilities.DatabasesManage, ct))
            throw new UnauthorizedAccessException();
    }


    private static string Slugify(string name)
    {
        var slug = NonSlug().Replace(name.Trim().ToLowerInvariant(), "-").Trim('-');
        return string.IsNullOrWhiteSpace(slug) ? "svc-" + Guid.NewGuid().ToString("N")[..6] : slug;
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonSlug();
}
