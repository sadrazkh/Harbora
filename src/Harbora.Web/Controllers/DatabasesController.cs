using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Apps;
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
    ICurrentUser currentUser) : Controller
{
    private Guid WorkspaceId => currentUser.WorkspaceId ?? Guid.Empty;

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        ViewData["Title"] = "Databases";
        var services = await db.ManagedServices.Where(s => s.WorkspaceId == WorkspaceId)
            .OrderByDescending(s => s.CreatedAt).ToListAsync(ct);
        return View(services);
    }

    [HttpGet("create")]
    public IActionResult Create()
    {
        ViewData["Title"] = "New service";
        ViewBag.Catalog = engine.Catalog;
        return View(new CreateServiceViewModel());
    }

    [HttpPost("create")]
    [ValidateAntiForgeryToken]
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
        var service = new ManagedService
        {
            WorkspaceId = WorkspaceId,
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
            EncryptedPassword = protector.Protect(GeneratePassword())
        };
        db.ManagedServices.Add(service);
        await db.SaveChangesAsync(ct);

        await engine.QueueProvisionAsync(service.Id, ct);
        return RedirectToAction(nameof(Details), new { id = service.Id });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Details(Guid id, bool reveal = false, CancellationToken ct = default)
    {
        var service = await db.ManagedServices.FirstOrDefaultAsync(s => s.Id == id && s.WorkspaceId == WorkspaceId, ct);
        if (service is null) return NotFound();

        ViewData["Title"] = service.Name;
        var conn = await engine.GetConnectionInfoAsync(id, ct);
        ViewBag.Connection = reveal ? conn.ConnectionString : conn.ConnectionStringMasked;
        ViewBag.Reveal = reveal;
        ViewBag.Apps = await db.Apps.Where(a => a.WorkspaceId == WorkspaceId)
            .Select(a => new { a.Id, a.Name }).ToListAsync(ct);
        return View(service);
    }

    [HttpPost("{id:guid}/start")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Start(Guid id, CancellationToken ct)
    { await Guard(id, ct); await engine.StartAsync(id, ct); return RedirectToAction(nameof(Details), new { id }); }

    [HttpPost("{id:guid}/stop")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Stop(Guid id, CancellationToken ct)
    { await Guard(id, ct); await engine.StopAsync(id, ct); return RedirectToAction(nameof(Details), new { id }); }

    [HttpPost("{id:guid}/remove")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Remove(Guid id, CancellationToken ct)
    { await Guard(id, ct); await engine.RemoveAsync(id, deleteData: false, ct); return RedirectToAction(nameof(Index)); }

    /// <summary>Injects the service's connection env into an app (secret, encrypted). Applies on next deploy.</summary>
    [HttpPost("{id:guid}/attach")]
    [ValidateAntiForgeryToken]
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

    private async Task Guard(Guid id, CancellationToken ct)
    {
        var owns = await db.ManagedServices.AnyAsync(s => s.Id == id && s.WorkspaceId == WorkspaceId, ct);
        if (!owns) throw new UnauthorizedAccessException();
    }

    private static string GeneratePassword() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)).Replace("+", "").Replace("/", "").Replace("=", "");

    private static string Slugify(string name)
    {
        var slug = NonSlug().Replace(name.Trim().ToLowerInvariant(), "-").Trim('-');
        return string.IsNullOrWhiteSpace(slug) ? "svc-" + Guid.NewGuid().ToString("N")[..6] : slug;
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonSlug();
}
