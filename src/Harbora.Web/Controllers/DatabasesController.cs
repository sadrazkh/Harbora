using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Authorization;
using Harbora.Domain.Backups;
using Harbora.Domain.Common;
using Harbora.Domain.Monitoring;
using Harbora.Domain.Services;
using Harbora.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
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
    public async Task<IActionResult> Index(Guid? selected, bool reveal = false, CancellationToken ct = default)
    {
        ViewData["Title"] = "Databases";

        var query = db.ManagedServices.Where(s => s.WorkspaceId == WorkspaceId);
        var visibleProjectIds = await access.VisibleProjectIdsAsync(ct);
        if (visibleProjectIds is { } visible)
            query = query.Where(s => s.EnvironmentId != null && visible.Contains(s.Environment!.ProjectId));

        var services = await query.AsNoTracking()
            .Include(s => s.Environment).ThenInclude(e => e!.Project)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync(ct);

        var serviceRefs = services.Select(s => s.Id.ToString()).ToList();
        var containerNames = services.Select(s => s.ContainerName).ToList();
        var metrics = containerNames.Count == 0
            ? new List<MonitoringMetric>()
            : await db.MonitoringMetrics.AsNoTracking()
                .Where(m => m.ResourceRef != null && containerNames.Contains(m.ResourceRef)
                            && (m.Name == "cpu.percent" || m.Name == "mem.used"))
                .OrderByDescending(m => m.Timestamp).Take(2_000).ToListAsync(ct);

        var backups = serviceRefs.Count == 0
            ? new List<Backup>()
            : await db.Backups.AsNoTracking()
                .Where(b => serviceRefs.Contains(b.TargetRef)
                            && (b.Type == BackupType.Database || b.Type == BackupType.Service))
                .OrderByDescending(b => b.CreatedAt).ToListAsync(ct);

        var appsQuery = db.Apps.AsNoTracking()
            .Include(a => a.Environment).ThenInclude(e => e!.Project)
            .Include(a => a.EnvironmentVariables)
            .Where(a => a.WorkspaceId == WorkspaceId);
        if (visibleProjectIds is { } appProjects)
            appsQuery = appsQuery.Where(a => a.EnvironmentId != null && appProjects.Contains(a.Environment!.ProjectId));

        var apps = await appsQuery
            .OrderBy(a => a.Name).ToListAsync(ct);
        var connections = usage.ConnectionsFor(apps, containerNames);

        var rows = services.Select(s =>
        {
            var latestBackup = backups.FirstOrDefault(b => b.TargetRef == s.Id.ToString());
            var cpu = metrics.FirstOrDefault(m => m.ResourceRef == s.ContainerName && m.Name == "cpu.percent")?.Value;
            var memory = metrics.FirstOrDefault(m => m.ResourceRef == s.ContainerName && m.Name == "mem.used")?.Value;
            var linked = connections.Count(c => c.Value.Contains(s.ContainerName));
            return new DatabaseRowViewModel(
                s.Id, s.Name, s.Type, s.Version, s.Status,
                s.Environment?.Project?.Name ?? "—", s.Environment?.Name ?? "—",
                s.ContainerName, s.InternalPort, s.Username, s.DatabaseName, s.VolumeName,
                s.StorageBytes, s.StorageMeasuredAt, cpu,
                memory is null ? null : (long?)memory.Value,
                linked, latestBackup?.FinishedAt ?? latestBackup?.CreatedAt, latestBackup?.Status);
        }).ToList();

        DatabaseOverviewViewModel? overview = null;
        var selectedRow = rows.FirstOrDefault(r => r.Id == selected) ?? rows.FirstOrDefault();
        if (selectedRow is not null)
        {
            var service = services.First(s => s.Id == selectedRow.Id);
            var canManage = await access.CanTouchServiceAsync(service.Id, Capabilities.DatabasesManage, ct);
            var conn = await engine.GetConnectionInfoAsync(service.Id, ct);
            var network = service.EnvironmentId is { } environmentId
                ? await db.Environments.AsNoTracking().Where(e => e.Id == environmentId)
                    .Select(e => Harbora.Infrastructure.Networking.EnvironmentNetwork.For(e.Project!.Slug, e.Slug, e.Id))
                    .FirstOrDefaultAsync(ct)
                : null;
            var usingApps = connections.Where(c => c.Value.Contains(service.ContainerName))
                .Select(c => apps.First(a => a.Id == c.Key).Name).Order().ToList();
            var selectedBackups = backups.Where(b => b.TargetRef == service.Id.ToString()).Take(6)
                .Select(b => new BackupEventViewModel(
                    b.Id, b.Status, b.SizeBytes, b.FinishedAt ?? b.StartedAt ?? b.CreatedAt,
                    b.IsScheduled, b.VerifiedRestorable)).ToList();
            var schedule = await db.BackupSchedules.AsNoTracking()
                .Where(s => s.TargetRef == service.Id.ToString() && s.IsEnabled)
                .OrderBy(s => s.NextRunAt).FirstOrDefaultAsync(ct);

            overview = new DatabaseOverviewViewModel
            {
                Database = selectedRow,
                Connection = reveal && canManage ? conn.ConnectionString : conn.ConnectionStringMasked,
                Reveal = reveal && canManage,
                CanManage = canManage,
                Network = network,
                UsedBy = usingApps,
                Apps = apps.Select(a => new ResourceOptionViewModel(
                    a.Id, a.Name,
                    $"{a.Environment?.Project?.Name ?? "—"} · {a.Environment?.Name ?? "—"}",
                    a.EnvironmentId == service.EnvironmentId)).ToList(),
                Backups = selectedBackups,
                NextBackupAt = schedule?.NextRunAt,
                BackupIntervalHours = schedule?.IntervalHours
            };
        }

        return View(new DatabasesPageViewModel
        {
            Databases = rows,
            Catalog = engine.Catalog,
            Selected = overview
        });
    }

    [HttpGet("create")]
    [Authorize(Policy = Capabilities.DatabasesManage)]
    public async Task<IActionResult> Create(ManagedServiceType? type, Guid? environmentId, CancellationToken ct)
    {
        ViewData["Title"] = "New service";
        await projects.EnsureDefaultEnvironmentAsync(WorkspaceId, ct);
        await PopulateCreateAsync(ct);

        // Preselected when the caller named an engine we actually run. An unknown one falls back to
        // the default rather than being honoured, so a stale link cannot produce a service of a type
        // this installation has no definition for.
        var model = new CreateServiceViewModel { EnvironmentId = environmentId };
        if (type is { } chosen && engine.Catalog.Any(c => c.Type == chosen)) model.Type = chosen;

        return View(model);
    }

    [HttpPost("create")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.DatabasesManage)]
    public async Task<IActionResult> Create(CreateServiceViewModel model, CancellationToken ct)
    {
        var entry = engine.Catalog.FirstOrDefault(c => c.Type == model.Type);
        if (entry is null) ModelState.AddModelError(nameof(model.Type), "Unknown service type.");
        else if (!string.IsNullOrWhiteSpace(model.Version) &&
                 !entry.Versions.Contains(model.Version, StringComparer.OrdinalIgnoreCase))
        {
            ModelState.AddModelError(nameof(model.Version), "The selected database version is not supported.");
        }

        var check = await quota.CanAddServiceAsync(WorkspaceId, ct);
        if (!check.Allowed) ModelState.AddModelError(string.Empty, check.Reason ?? "Plan quota exceeded.");

        var environment = await projects.ResolveEnvironmentAsync(WorkspaceId, model.EnvironmentId, ct);
        if (!await access.AllowsAsync(
                new ResourcePlacement(environment.ProjectId, environment.Id), Capabilities.DatabasesManage, ct))
        {
            ModelState.AddModelError(nameof(model.EnvironmentId),
                "You do not have permission to create a database in this environment.");
        }

        if (!ModelState.IsValid) { await PopulateCreateAsync(ct); return View(model); }

        var slug = Slugify(model.Name);
        if (await db.ManagedServices.AnyAsync(s => s.WorkspaceId == WorkspaceId && s.ContainerName == $"harbora-svc-{slug}", ct))
        {
            ModelState.AddModelError(nameof(model.Name), "A service with this name already exists.");
            await PopulateCreateAsync(ct);
            return View(model);
        }

        var serverId = await db.Servers.Where(s => s.IsLocal).Select(s => s.Id).FirstAsync(ct);
        var service = new ManagedService
        {
            WorkspaceId = WorkspaceId,
            EnvironmentId = environment.Id,
            ServerId = serverId,
            Name = model.Name,
            Type = model.Type,
            Version = string.IsNullOrWhiteSpace(model.Version)
                ? entry!.Versions[0]
                : entry!.Versions.First(v => string.Equals(v, model.Version, StringComparison.OrdinalIgnoreCase)),
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
        if (!await access.CanSeeServiceAsync(id, ct)) return NotFound();
        return RedirectToAction(nameof(Index), new { selected = id, reveal });
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
        if (!await access.CanTouchServiceAsync(id, Capabilities.DatabasesManage, ct)) return NotFound();
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
        if (!await access.CanTouchAppAsync(appId, Capabilities.AppsEnv, ct)) return NotFound();
        var app = await db.Apps.Include(a => a.EnvironmentVariables)
            .FirstOrDefaultAsync(a => a.Id == appId && a.WorkspaceId == WorkspaceId, ct);
        if (app is null) return NotFound();

        // An environment is a private network, so it is also the wiring boundary. Without this the
        // attach succeeded and wrote a hostname resolvable only on the other network — the service
        // then started, looked healthy, and could not reach its database.
        var service = await db.ManagedServices.FirstOrDefaultAsync(s => s.Id == id, ct);
        var verdict = Harbora.Infrastructure.Networking.NetworkWiring.CanAttach(
            app.EnvironmentId, service?.EnvironmentId);
        if (!verdict.Allowed)
        {
            TempData["Error"] = verdict.Reason;
            return RedirectToAction(nameof(Details), new { id });
        }

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
    /// Removes the variables an attach wrote. Only those keys, and only when they still hold this
    /// database's values — a key somebody edited by hand is theirs, not ours to delete.
    /// </summary>
    [HttpPost("{id:guid}/detach")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.DatabasesManage)]
    public async Task<IActionResult> Detach(Guid id, Guid appId, CancellationToken ct)
    {
        await Guard(id, ct);
        if (!await access.CanTouchAppAsync(appId, Capabilities.AppsEnv, ct)) return NotFound();
        var app = await db.Apps.Include(a => a.EnvironmentVariables)
            .FirstOrDefaultAsync(a => a.Id == appId && a.WorkspaceId == WorkspaceId, ct);
        if (app is null) return NotFound();

        var env = await engine.BuildAttachEnvAsync(id, ct);
        var removed = 0;
        foreach (var (key, _) in env)
        {
            var existing = app.EnvironmentVariables.FirstOrDefault(e => e.Key == key);
            if (existing is null) continue;

            app.EnvironmentVariables.Remove(existing);
            removed++;
        }

        await db.SaveChangesAsync(ct);
        TempData["Message"] = removed == 0
            ? "Nothing to remove — this app was not wired to this database."
            : $"Removed {removed} variable(s) from {app.Name}. Redeploy the app to apply the change.";
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

    private async Task PopulateCreateAsync(CancellationToken ct)
    {
        ViewBag.Catalog = engine.Catalog;
        var environmentQuery = db.Environments.AsNoTracking().Where(e => e.WorkspaceId == WorkspaceId);
        if (await access.VisibleProjectIdsAsync(ct) is { } visible)
            environmentQuery = environmentQuery.Where(e => visible.Contains(e.ProjectId));

        ViewBag.Environments = await environmentQuery
            .OrderBy(e => e.Project!.Name).ThenByDescending(e => e.IsDefault).ThenBy(e => e.Name)
            .Select(e => new SelectListItem($"{e.Project!.Name} · {e.Name}", e.Id.ToString()))
            .ToListAsync(ct);
    }


    private static string Slugify(string name)
    {
        var slug = NonSlug().Replace(name.Trim().ToLowerInvariant(), "-").Trim('-');
        return string.IsNullOrWhiteSpace(slug) ? "svc-" + Guid.NewGuid().ToString("N")[..6] : slug;
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonSlug();
}
