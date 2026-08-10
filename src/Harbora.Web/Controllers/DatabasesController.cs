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
using Harbora.Infrastructure.Services;
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
    ISchedulerService scheduler,
    ISecretProtector protector,
    Harbora.Infrastructure.Projects.ProjectService projects,
    Harbora.Infrastructure.Services.ServiceUsageService usage,
    Harbora.Infrastructure.Security.ProjectAccessService access,
    Harbora.Infrastructure.Services.DatabaseAccessService databaseAccess,
    Harbora.Infrastructure.Services.AdminerService adminer,
    IAuditLogger audit,
    INodeAgentClient node,
    ICurrentUser currentUser) : Controller
{
    private Guid WorkspaceId => currentUser.WorkspaceId ?? Guid.Empty;

    private bool IsFa => System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fa";

    /// <summary>
    /// Opens a throwaway web admin tool onto this database for an hour.
    ///
    /// The tool runs on the database's own private network, is published behind basic-auth with a
    /// password generated here and shown once, and stops itself. See AdminerService for why each of
    /// those is not optional.
    /// </summary>
    [HttpPost("{id:guid}/admin-tool")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.DatabasesManage)]
    public async Task<IActionResult> OpenAdminTool(Guid id, CancellationToken ct)
    {
        if (!await access.CanSeeServiceAsync(id, ct)) return NotFound();

        var result = await adminer.OpenAsync(id, ct);
        if (!result.Ok)
        {
            TempData["Error"] = result.Refusal;
            return RedirectToAction(nameof(Details), new { id });
        }

        await audit.LogAsync("service.admin_tool_opened", "service", id.ToString(),
            HttpContext.Connection.RemoteIpAddress?.ToString(), ct: ct);

        // Shown once, on the next render, like every other generated credential in this panel.
        TempData["AdminToolUrl"] = result.Url;
        TempData["AdminToolUser"] = result.User;
        TempData["AdminToolPassword"] = result.Password;
        return RedirectToAction(nameof(Details), new { id });
    }

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

        var rows = services.Select(s => Row(s, metrics, connections, backups)).ToList();

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
            Catalog = await ServiceCatalogReader.EffectiveAsync(db, engine, ct),
            Selected = overview
        });
    }

    /// <summary>
    /// Everything one database's own page shows.
    ///
    /// Split out of the list action because a database now has a page rather than a strip beside a
    /// table. A rail can only ever be as wide as what is left over, so the settings that belong to a
    /// resource ended up compressed next to a list of its siblings.
    /// </summary>
    private async Task<DatabaseOverviewViewModel?> BuildOverviewAsync(Guid id, bool reveal, CancellationToken ct)
    {
        var service = await db.ManagedServices.AsNoTracking()
            .Include(s => s.Environment).ThenInclude(e => e!.Project)
            .FirstOrDefaultAsync(s => s.Id == id && s.WorkspaceId == WorkspaceId, ct);
        if (service is null) return null;

        var canManage = await access.CanTouchServiceAsync(service.Id, Capabilities.DatabasesManage, ct);
        var conn = await engine.GetConnectionInfoAsync(service.Id, ct);

        var metrics = await db.MonitoringMetrics.AsNoTracking()
            .Where(m => m.ResourceRef == service.ContainerName
                        && (m.Name == "cpu.percent" || m.Name == "mem.used"))
            .OrderByDescending(m => m.Timestamp).Take(200).ToListAsync(ct);

        var apps = await db.Apps.AsNoTracking()
            .Include(a => a.Environment).ThenInclude(e => e!.Project)
            .Include(a => a.EnvironmentVariables)
            .Where(a => a.WorkspaceId == WorkspaceId).OrderBy(a => a.Name).ToListAsync(ct);

        var connections = usage.ConnectionsFor(apps, [service.ContainerName]);
        var usingApps = connections.Where(c => c.Value.Contains(service.ContainerName))
            .Select(c => apps.First(a => a.Id == c.Key).Name).Order().ToList();

        var network = service.EnvironmentId is { } environmentId
            ? await db.Environments.AsNoTracking().Where(e => e.Id == environmentId)
                .Select(e => Harbora.Infrastructure.Networking.EnvironmentNetwork.For(e.Project!.Slug, e.Slug, e.Id))
                .FirstOrDefaultAsync(ct)
            : null;

        var backups = await db.Backups.AsNoTracking()
            .Where(b => b.TargetRef == service.Id.ToString())
            .OrderByDescending(b => b.CreatedAt).Take(6)
            .Select(b => new BackupEventViewModel(
                b.Id, b.Status, b.SizeBytes, b.FinishedAt ?? b.StartedAt ?? b.CreatedAt,
                b.IsScheduled, b.VerifiedRestorable)).ToListAsync(ct);

        var schedule = await db.BackupSchedules.AsNoTracking()
            .Where(s => s.TargetRef == service.Id.ToString() && s.IsEnabled)
            .OrderBy(s => s.NextRunAt).FirstOrDefaultAsync(ct);

        return new DatabaseOverviewViewModel
        {
            Database = Row(service, metrics, connections),
            Connection = reveal && canManage ? conn.ConnectionString : conn.ConnectionStringMasked,
            Reveal = reveal && canManage,
            CanManage = canManage,
            Network = network,
            UsedBy = usingApps,
            Apps = apps.Select(a => new ResourceOptionViewModel(
                a.Id, a.Name,
                $"{a.Environment?.Project?.Name ?? "—"} · {a.Environment?.Name ?? "—"}",
                a.EnvironmentId == service.EnvironmentId)).ToList(),
            Backups = backups,
            NextBackupAt = schedule?.NextRunAt,
            BackupIntervalHours = schedule?.IntervalHours,
            Sizes = await SizeChoicesAsync(service.InstanceSizeKey, ct),
            RunningImage = service.RunningImage,
            InstanceSizeKey = service.InstanceSizeKey,
            MemoryLimitBytes = service.MemoryLimitBytes,
            DiskLimitBytes = service.DiskLimitBytes,
            CpuLimit = service.CpuLimit,
            TlsEnabled = service.TlsEnabled
        };
    }

    /// <summary>The sizes this workspace's plan allows, with the current one preselected.</summary>
    private async Task<List<SelectListItem>> SizeChoicesAsync(string? current, CancellationToken ct)
    {
        var plan = await db.Workspaces.Where(w => w.Id == WorkspaceId)
                .Select(w => w.PlanId).FirstOrDefaultAsync(ct) is { } planId
            ? await db.Plans.FirstOrDefaultAsync(p => p.Id == planId, ct)
            : await db.Plans.FirstOrDefaultAsync(p => p.IsDefault, ct);

        var allowed = plan is null || string.IsNullOrWhiteSpace(plan.AllowedSizeKeys)
            ? null
            : plan.AllowedSizeKeys.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return (await db.InstanceSizes.Where(s => s.IsEnabled).OrderBy(s => s.SortOrder).ToListAsync(ct))
            .Where(s => allowed is null || allowed.Contains(s.Key))
            .Select(s => new SelectListItem(
                Harbora.Infrastructure.Tenancy.InstanceSizeLabel.For(
                    s.Name, s.CpuCores, s.MemoryBytes, s.DiskBytes), s.Key,
                string.Equals(s.Key, current, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    /// <summary>One row, built the same way for the list and for a database's own page.</summary>
    private static DatabaseRowViewModel Row(
        ManagedService s,
        IReadOnlyList<MonitoringMetric> metrics,
        IReadOnlyDictionary<Guid, IReadOnlyList<string>> connections,
        IReadOnlyList<Backup>? backups = null)
    {
        var latestBackup = backups?.FirstOrDefault(b => b.TargetRef == s.Id.ToString());
        var cpu = metrics.FirstOrDefault(m => m.ResourceRef == s.ContainerName && m.Name == "cpu.percent")?.Value;
        var memory = metrics.FirstOrDefault(m => m.ResourceRef == s.ContainerName && m.Name == "mem.used")?.Value;

        return new DatabaseRowViewModel(
            s.Id, s.Name, s.Type, s.Version, s.Status,
            s.Environment?.Project?.Name ?? "—", s.Environment?.Name ?? "—",
            s.ContainerName, s.InternalPort, s.Username, s.DatabaseName, s.VolumeName,
            s.StorageBytes, s.StorageMeasuredAt, cpu,
            memory is null ? null : (long?)memory.Value,
            connections.Count(c => c.Value.Contains(s.ContainerName)),
            latestBackup?.FinishedAt ?? latestBackup?.CreatedAt, latestBackup?.Status,
            s.MemoryLimitBytes);
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
        // The operator's list, not the shipped one — the form offered theirs, and validating
        // against a different list refuses the option the page just showed.
        var catalog = await ServiceCatalogReader.EffectiveAsync(db, engine, ct);
        var entry = catalog.FirstOrDefault(c => c.Type == model.Type);
        if (entry is null) ModelState.AddModelError(nameof(model.Type), "Unknown service type.");
        else if (!string.IsNullOrWhiteSpace(model.Version) &&
                 !entry.Versions.Contains(model.Version, StringComparer.OrdinalIgnoreCase))
        {
            ModelState.AddModelError(nameof(model.Version), "The selected database version is not supported.");
        }

        var check = await quota.CanAddServiceAsync(WorkspaceId, model.InstanceSizeKey, ct);
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

        // The chosen tier, resolved before placement because placement needs to know how much this
        // database is asking for.
        var size = string.IsNullOrWhiteSpace(model.InstanceSizeKey)
            ? null
            : await db.InstanceSizes.FirstOrDefaultAsync(s => s.Key == model.InstanceSizeKey, ct);

        // Placed the way an application is. This read `IsLocal` and nothing else, so a fleet could
        // have a dozen nodes and every database still landed on the control plane's own machine —
        // the panel's host filled up while the nodes it was scheduling applications onto sat empty,
        // and there was no way to say otherwise.
        var placement = model.ServerId is { } chosenServer && await db.Servers.AnyAsync(s => s.Id == chosenServer, ct)
            ? await scheduler.CheckAsync(chosenServer, size?.MemoryBytes ?? 0, size?.CpuCores ?? 0, ct)
            : await scheduler.PlaceAsync(size?.MemoryBytes ?? 0, size?.CpuCores ?? 0, null, ct);

        if (!placement.Ok)
        {
            ModelState.AddModelError(string.Empty, placement.Reason ?? "No server has capacity for this database.");
            await PopulateCreateAsync(ct);
            return View(model);
        }

        var serverId = placement.ServerId!.Value;
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

        // The chosen plan becomes the container's ceiling. Recorded on the row rather than read at
        // provision time so it keeps what was agreed, and a tier later withdrawn from the catalogue
        // does not silently un-limit a running database.
        if (size is not null)
        {
            service.InstanceSizeKey = size.Key;
            service.MemoryLimitBytes = size.MemoryBytes;
            service.DiskLimitBytes = size.DiskBytes;
            service.CpuLimit = size.CpuCores;
        }

        db.ManagedServices.Add(service);
        await db.SaveChangesAsync(ct);

        await engine.QueueProvisionAsync(service.Id, ct);
        return RedirectToAction(nameof(Details), new { id = service.Id });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Details(Guid id, bool reveal = false, CancellationToken ct = default)
    {
        if (!await access.CanSeeServiceAsync(id, ct)) return NotFound();

        var overview = await BuildOverviewAsync(id, reveal, ct);
        if (overview is null) return NotFound();

        ViewData["Title"] = overview.Database.Name;
        return View(overview);
    }

    [HttpPost("{id:guid}/start")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.DatabasesManage)]
    public async Task<IActionResult> Start(Guid id, CancellationToken ct)
    {
        await Guard(id, ct);
        // The billing gate refusing a workspace with no balance. Surfaced where this controller
        // already surfaces a quota refusal, rather than as a 500 for a deliberate decision — and in
        // the reader's own language, since this is a customer already locked out of their database.
        try { await engine.StartAsync(id, ct); }
        catch (QuotaRefusedException ex) { TempData["Error"] = (IsFa ? ex.ReasonFa : null) ?? ex.Message; }
        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>
    /// Recreates the container from the current definition, keeping the data volume.
    ///
    /// It exists because a setting that only applies at creation — encryption is the first — is
    /// otherwise unreachable for every database that already exists. Without this, TLS would have
    /// shipped for new databases and been permanently out of reach for the ones that need it.
    /// </summary>
    /// <summary>
    /// Moves a database to a different resource plan.
    ///
    /// Stored, then applied by rebuilding the container — which this does not do on its own. A
    /// database is the one thing on the platform where an unrequested restart is never a small
    /// thing, so the rebuild stays a separate, deliberate press.
    /// </summary>
    [HttpPost("{id:guid}/size")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.DatabasesManage)]
    public async Task<IActionResult> Resize(Guid id, string? instanceSizeKey, CancellationToken ct)
    {
        await Guard(id, ct);

        var svc = await db.ManagedServices.FirstOrDefaultAsync(s => s.Id == id && s.WorkspaceId == WorkspaceId, ct);
        if (svc is null) return NotFound();

        // Its current allocation is taken off the total first, or moving from small to small would
        // be measured as asking for a second database's worth and refused.
        var check = await quota.CanAddServiceAsync(WorkspaceId, instanceSizeKey, ct);
        if (!check.Allowed && (svc.MemoryLimitBytes > 0 || svc.CpuLimit > 0))
        {
            var freed = new { svc.MemoryLimitBytes, svc.CpuLimit };
            svc.MemoryLimitBytes = 0;
            svc.CpuLimit = 0;
            check = await quota.CanAddServiceAsync(WorkspaceId, instanceSizeKey, ct);

            if (!check.Allowed)
            {
                svc.MemoryLimitBytes = freed.MemoryLimitBytes;
                svc.CpuLimit = freed.CpuLimit;
            }
        }

        if (!check.Allowed)
        {
            TempData["Error"] = check.Reason;
            return RedirectToAction(nameof(Details), new { id });
        }

        var size = string.IsNullOrWhiteSpace(instanceSizeKey)
            ? null
            : await db.InstanceSizes.FirstOrDefaultAsync(s => s.Key == instanceSizeKey, ct);

        // The same refusal an app gets. A database is the resource most likely to be over a smaller
        // tier's disk, and moving it there without saying so would leave a figure on the page that
        // its own data already exceeds.
        if (size is { DiskBytes: > 0 })
        {
            var stored = new Harbora.Infrastructure.Tenancy.DiskUsage(
                svc.StorageBytes ?? 0, svc.StorageBytes is null ? 1 : 0);

            if (svc.StorageBytes is not null
                && Harbora.Infrastructure.Tenancy.InstanceDisk.Explain(size.DiskBytes, stored) is { } tooSmall)
            {
                TempData["Error"] = tooSmall;
                return RedirectToAction(nameof(Details), new { id });
            }
        }

        svc.InstanceSizeKey = size?.Key;
        svc.MemoryLimitBytes = size?.MemoryBytes ?? 0;
        svc.CpuLimit = size?.CpuCores ?? 0;
        svc.DiskLimitBytes = size?.DiskBytes ?? 0;
        await db.SaveChangesAsync(ct);

        TempData["Message"] = IsFa
            ? $"اندازه روی «{size?.Name ?? "بدون سقف"}» تنظیم شد. برای اعمال، کانتینر را بازسازی کنید."
            : $"Size set to {size?.Name ?? "no limit"}. Rebuild the container to apply it.";

        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost("{id:guid}/reprovision")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.DatabasesManage)]
    public async Task<IActionResult> Reprovision(Guid id, CancellationToken ct)
    {
        await Guard(id, ct);
        var svc = await db.ManagedServices.FirstOrDefaultAsync(s => s.Id == id && s.WorkspaceId == WorkspaceId, ct);
        if (svc is null) return NotFound();

        await engine.QueueProvisionAsync(id, ct);

        // Says what it costs. The container is replaced, so open connections drop — the data volume
        // is kept, which is the part people actually worry about.
        TempData["Message"] = IsFa
            ? $"{svc.Name} در حال بازسازی است. داده‌ها حفظ می‌شوند؛ اتصال‌های باز قطع می‌شوند."
            : $"{svc.Name} is being rebuilt. The data is kept; open connections will drop.";

        return RedirectToAction(nameof(Details), new { id });
    }

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
    public async Task<IActionResult> Attach(Guid id, Guid appId, string? returnUrl, CancellationToken ct)
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

        // What the application already holds, so a second database of the same engine cannot take
        // the first one's names. Decrypted here because the decision is "does this value belong to
        // me", and an unreadable one is treated as somebody else's.
        var current = app.EnvironmentVariables.ToDictionary(
            e => e.Key,
            e => { try { return protector.Unprotect(e.Value); } catch { return (string?)null; } },
            StringComparer.Ordinal);

        var prefixedOnly = Harbora.Infrastructure.Services.AttachKeys.IsPrefixedOnly(env, current);
        var final = Harbora.Infrastructure.Services.AttachKeys.For(env, current, service!.Name);

        foreach (var (key, value) in final)
        {
            var existing = app.EnvironmentVariables.FirstOrDefault(e => e.Key == key);
            if (existing is null)
                app.EnvironmentVariables.Add(new EnvironmentVariable { Key = key, Value = protector.Protect(value), IsSecret = true });
            else { existing.Value = protector.Protect(value); existing.IsSecret = true; }
        }
        await db.SaveChangesAsync(ct);

        // Said plainly when it matters. Somebody attaching a second database and then reading
        // DATABASE_URL in their code would get the first one, and nothing on the screen would have
        // suggested it.
        var prefix = Harbora.Infrastructure.Services.AttachKeys.PrefixFor(service.Name);
        TempData["Message"] = prefixedOnly
            ? (IsFa
                ? $"به {app.Name} وصل شد. چون این اپ از قبل دیتابیس دیگری با همین نام‌ها داشت، متغیرهای این یکی با پیشوند {prefix} نوشته شدند. برای اعمال، اپ را دوباره دیپلوی کنید."
                : $"Attached to {app.Name}. This application already had another database under the usual names, so this one's variables are written with the {prefix} prefix. Redeploy the app to apply them.")
            : (IsFa
                ? $"به {app.Name} وصل شد. برای اعمال متغیرها اپ را دوباره دیپلوی کنید."
                : $"Attached to {app.Name}. Redeploy the app to apply the new variables.");

        // Back where the person was. Attaching from an application's page and landing on the
        // database's is a jump that loses their place — and the app page is where they go next to
        // attach the second one.
        return string.IsNullOrWhiteSpace(returnUrl)
            ? RedirectToAction(nameof(Details), new { id })
            : LocalRedirect(returnUrl);
    }

    /// <summary>
    /// Removes the variables an attach wrote. Only those keys, and only when they still hold this
    /// database's values — a key somebody edited by hand is theirs, not ours to delete.
    /// </summary>
    [HttpPost("{id:guid}/detach")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.DatabasesManage)]
    public async Task<IActionResult> Detach(Guid id, Guid appId, string? returnUrl, CancellationToken ct)
    {
        await Guard(id, ct);
        if (!await access.CanTouchAppAsync(appId, Capabilities.AppsEnv, ct)) return NotFound();
        var app = await db.Apps.Include(a => a.EnvironmentVariables)
            .FirstOrDefaultAsync(a => a.Id == appId && a.WorkspaceId == WorkspaceId, ct);
        if (app is null) return NotFound();

        var env = await engine.BuildAttachEnvAsync(id, ct);
        var service = await db.ManagedServices.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
        var prefix = Harbora.Infrastructure.Services.AttachKeys.PrefixFor(service?.Name ?? string.Empty);

        var removed = 0;
        foreach (var (key, value) in env)
        {
            // Both names this database may have written under. Missing the prefixed one would leave
            // a detached database's connection string in the application forever.
            foreach (var candidate in new[] { key, prefix + key })
            {
                var existing = app.EnvironmentVariables.FirstOrDefault(e => e.Key == candidate);
                if (existing is null) continue;

                // The value has to still be this database's. The comment above has always said so
                // and the code did not check: removing by key alone means detaching one database
                // strips the variables belonging to another that holds the same name — which is
                // exactly the situation prefixed keys exist to create.
                string? current;
                try { current = protector.Unprotect(existing.Value); } catch { current = null; }
                if (current != value) continue;

                app.EnvironmentVariables.Remove(existing);
                removed++;
            }
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
        ViewBag.Catalog = await ServiceCatalogReader.EffectiveAsync(db, engine, ct);

        // The same list the application form offers. Only shown when there is a choice to make.
        ViewBag.Servers = await db.Servers.OrderByDescending(s => s.IsLocal).ThenBy(s => s.Name)
            .Select(s => new SelectListItem(s.IsLocal ? s.Name + " (local)" : s.Name, s.Id.ToString()))
            .ToListAsync(ct);
        var environmentQuery = db.Environments.AsNoTracking().Where(e => e.WorkspaceId == WorkspaceId);
        if (await access.VisibleProjectIdsAsync(ct) is { } visible)
            environmentQuery = environmentQuery.Where(e => visible.Contains(e.ProjectId));

        ViewBag.Environments = await environmentQuery
            .OrderBy(e => e.Project!.Name).ThenByDescending(e => e.IsDefault).ThenBy(e => e.Name)
            .Select(e => new SelectListItem($"{e.Project!.Name} · {e.Name}", e.Id.ToString()))
            .ToListAsync(ct);

        // The same list the application form offers, filtered by the same plan. A database that can
        // be any size while the app beside it is capped is not a plan, it is a suggestion.
        var plan = await db.Workspaces.Where(w => w.Id == WorkspaceId)
                .Select(w => w.PlanId).FirstOrDefaultAsync(ct) is { } planId
            ? await db.Plans.FirstOrDefaultAsync(p => p.Id == planId, ct)
            : await db.Plans.FirstOrDefaultAsync(p => p.IsDefault, ct);

        var allowed = plan is null || string.IsNullOrWhiteSpace(plan.AllowedSizeKeys)
            ? null
            : plan.AllowedSizeKeys.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var defaultSize = await db.Settings.IgnoreQueryFilters()
            .Where(s => s.Key == Harbora.Domain.Settings.SettingKeys.DefaultInstanceSize)
            .Select(s => s.Value).FirstOrDefaultAsync(ct);

        ViewBag.Sizes = (await db.InstanceSizes.Where(s => s.IsEnabled).OrderBy(s => s.SortOrder).ToListAsync(ct))
            .Where(s => allowed is null || allowed.Contains(s.Key))
            .Select(s => new SelectListItem(
                Harbora.Infrastructure.Tenancy.InstanceSizeLabel.For(
                    s.Name, s.CpuCores, s.MemoryBytes, s.DiskBytes), s.Key,
                string.Equals(s.Key, defaultSize, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }


    private static string Slugify(string name)
    {
        var slug = NonSlug().Replace(name.Trim().ToLowerInvariant(), "-").Trim('-');
        return string.IsNullOrWhiteSpace(slug) ? "svc-" + Guid.NewGuid().ToString("N")[..6] : slug;
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonSlug();
}
