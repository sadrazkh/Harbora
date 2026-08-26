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
// A note on the split: DatabasesController.Tabs.cs (Task 6) and DatabaseAccessActions.cs hold the
// routes that live under this same "databases" prefix. Kept partial rather than split by controller,
// same reasoning AppsController used: every route below is still /databases/{id}/…, and a second
// controller class sends the next reader hunting for which one owns a given path.
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
    Harbora.Infrastructure.Services.LogicalDatabaseService logicalDatabases,
    Harbora.Infrastructure.Services.AdminerService adminer,
    IAuditLogger audit,
    INodeAgentClient node,
    ICurrentUser currentUser,
    Harbora.Infrastructure.Billing.ResourceCreationBilling creationBilling,
    IDeploymentEngine deploymentEngine,
    IBackupEngine backupEngine,
    Harbora.Infrastructure.Backups.BackupDownloadTokens downloadTokens,
    IServerEngineFactory engines) : Controller
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
            query = query.Where(s => visible.Contains(s.Environment!.ProjectId));

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
            // C1 (2026-08-22 config-delivery plan): ServiceUsageService.Uses also checks this join.
            .Include(a => a.ManagedServices).ThenInclude(ms => ms.ManagedService)
            .Where(a => a.WorkspaceId == WorkspaceId);
        if (visibleProjectIds is { } appProjects)
            appsQuery = appsQuery.Where(a => appProjects.Contains(a.Environment!.ProjectId));

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
            var canExport = await access.CanTouchServiceAsync(service.Id, Capabilities.BackupsRun, ct);
            var canImport = await access.CanTouchServiceAsync(service.Id, Capabilities.BackupsRestore, ct);
            var conn = await engine.GetConnectionInfoAsync(service.Id, ct);
            var network = await db.Environments.AsNoTracking().Where(e => e.Id == service.EnvironmentId)
                .Select(e => Harbora.Infrastructure.Networking.EnvironmentNetwork.For(e.Project!.Slug, e.Slug, e.Id))
                .FirstOrDefaultAsync(ct);
            var usingApps = connections.Where(c => c.Value.Contains(service.ContainerName))
                .Select(c => apps.First(a => a.Id == c.Key).Name).Order().ToList();
            var selectedBackups = backups.Where(b => b.TargetRef == service.Id.ToString()).Take(6)
                .Select(b => new BackupEventViewModel(
                    b.Id, b.Status, b.SizeBytes, b.FinishedAt ?? b.StartedAt ?? b.CreatedAt,
                    b.IsScheduled, b.VerifiedRestorable, b.ExpiresAt)).ToList();
            var schedule = await db.BackupSchedules.AsNoTracking()
                .Where(s => s.TargetRef == service.Id.ToString() && s.IsEnabled)
                .OrderBy(s => s.NextRunAt).FirstOrDefaultAsync(ct);

            var logicalSupported = Harbora.Infrastructure.Services.DatabaseGrantSql.Supports(service.Type);

            overview = new DatabaseOverviewViewModel
            {
                Id = selectedRow.Id, Name = selectedRow.Name, Type = selectedRow.Type,
                Version = selectedRow.Version, Status = selectedRow.Status,
                Project = selectedRow.Project, Environment = selectedRow.Environment,
                CanManage = canManage,
                CanExport = canExport,
                CanImport = canImport,
                CurrentTab = "overview",
                Database = selectedRow,
                Connection = reveal && canManage ? conn.ConnectionString : conn.ConnectionStringMasked,
                Reveal = reveal && canManage,
                Network = network,
                UsedBy = usingApps,
                Apps = apps.Select(a => new ResourceOptionViewModel(
                    a.Id, a.Name,
                    $"{a.Environment?.Project?.Name ?? "—"} · {a.Environment?.Name ?? "—"}",
                    a.EnvironmentId == service.EnvironmentId)).ToList(),
                Backups = selectedBackups,
                NextBackupAt = schedule?.NextRunAt,
                BackupIntervalHours = schedule?.IntervalHours,
                LogicalDatabases = logicalSupported ? await BuildLogicalDatabaseRowsAsync(service, ct) : [],
                LogicalDatabasesSupported = logicalSupported,
                LogicalDatabasesUnsupportedReason = logicalSupported
                    ? null : Harbora.Infrastructure.Services.DatabaseGrantSql.UnsupportedReason(service.Type),
                CanManageLogicalDatabasesLocally = logicalDatabases.CanCreateLocally
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
        var canExport = await access.CanTouchServiceAsync(service.Id, Capabilities.BackupsRun, ct);
        var canImport = await access.CanTouchServiceAsync(service.Id, Capabilities.BackupsRestore, ct);
        var conn = await engine.GetConnectionInfoAsync(service.Id, ct);

        var metrics = await db.MonitoringMetrics.AsNoTracking()
            .Where(m => m.ResourceRef == service.ContainerName
                        && (m.Name == "cpu.percent" || m.Name == "mem.used"))
            .OrderByDescending(m => m.Timestamp).Take(200).ToListAsync(ct);

        var apps = await db.Apps.AsNoTracking()
            .Include(a => a.Environment).ThenInclude(e => e!.Project)
            .Include(a => a.EnvironmentVariables)
            // C1 (2026-08-22 config-delivery plan): ServiceUsageService.Uses also checks this join.
            .Include(a => a.ManagedServices).ThenInclude(ms => ms.ManagedService)
            .Where(a => a.WorkspaceId == WorkspaceId).OrderBy(a => a.Name).ToListAsync(ct);

        var connections = usage.ConnectionsFor(apps, [service.ContainerName]);
        var usingApps = connections.Where(c => c.Value.Contains(service.ContainerName))
            .Select(c => apps.First(a => a.Id == c.Key).Name).Order().ToList();

        var network = await db.Environments.AsNoTracking().Where(e => e.Id == service.EnvironmentId)
            .Select(e => Harbora.Infrastructure.Networking.EnvironmentNetwork.For(e.Project!.Slug, e.Slug, e.Id))
            .FirstOrDefaultAsync(ct);

        var backups = await db.Backups.AsNoTracking()
            .Where(b => b.TargetRef == service.Id.ToString())
            .OrderByDescending(b => b.CreatedAt).Take(6)
            .Select(b => new BackupEventViewModel(
                b.Id, b.Status, b.SizeBytes, b.FinishedAt ?? b.StartedAt ?? b.CreatedAt,
                b.IsScheduled, b.VerifiedRestorable, b.ExpiresAt)).ToListAsync(ct);

        var schedule = await db.BackupSchedules.AsNoTracking()
            .Where(s => s.TargetRef == service.Id.ToString() && s.IsEnabled)
            .OrderBy(s => s.NextRunAt).FirstOrDefaultAsync(ct);

        var row = Row(service, metrics, connections);

        var logicalSupported = Harbora.Infrastructure.Services.DatabaseGrantSql.Supports(service.Type);

        return new DatabaseOverviewViewModel
        {
            Id = row.Id, Name = row.Name, Type = row.Type, Version = row.Version, Status = row.Status,
            Project = row.Project, Environment = row.Environment,
            CanManage = canManage,
            CanExport = canExport,
            CanImport = canImport,
            CurrentTab = "overview",
            Database = row,
            Connection = reveal && canManage ? conn.ConnectionString : conn.ConnectionStringMasked,
            Reveal = reveal && canManage,
            Network = network,
            UsedBy = usingApps,
            Apps = apps.Select(a => new ResourceOptionViewModel(
                a.Id, a.Name,
                $"{a.Environment?.Project?.Name ?? "—"} · {a.Environment?.Name ?? "—"}",
                a.EnvironmentId == service.EnvironmentId)).ToList(),
            Backups = backups,
            NextBackupAt = schedule?.NextRunAt,
            BackupIntervalHours = schedule?.IntervalHours,
            RunningImage = service.RunningImage,
            InstanceSizeKey = service.InstanceSizeKey,
            ServerId = service.ServerId,
            MemoryLimitBytes = service.MemoryLimitBytes,
            DiskLimitBytes = service.DiskLimitBytes,
            CpuLimit = service.CpuLimit,
            TlsEnabled = service.TlsEnabled,
            LogicalDatabases = logicalSupported ? await BuildLogicalDatabaseRowsAsync(service, ct) : [],
            LogicalDatabasesSupported = logicalSupported,
            LogicalDatabasesUnsupportedReason = logicalSupported
                ? null : Harbora.Infrastructure.Services.DatabaseGrantSql.UnsupportedReason(service.Type),
            CanManageLogicalDatabasesLocally = logicalDatabases.CanCreateLocally
        };
    }

    /// <summary>
    /// The logical databases inside this instance, and what to legitimately show for each (D3,
    /// 2026-08-25 shared-databases plan) — the panel D1 shipped the machinery for but no UI, per its
    /// own report. Called only when <see cref="Harbora.Infrastructure.Services.DatabaseGrantSql.Supports"/>
    /// already said this engine has a logical-database story at all.
    /// </summary>
    private async Task<IReadOnlyList<LogicalDatabaseRowViewModel>> BuildLogicalDatabaseRowsAsync(
        ManagedService service, CancellationToken ct)
    {
        var databases = await db.ManagedServiceDatabases.AsNoTracking()
            .Where(d => d.ManagedServiceId == service.Id)
            .OrderByDescending(d => d.IsDefault).ThenBy(d => d.Name)
            .ToListAsync(ct);
        if (databases.Count == 0) return [];

        var databaseIds = databases.Select(d => d.Id).ToHashSet();
        var hasDefault = databases.Any(d => d.IsDefault);

        // Explicit joins only — the same authoritative-but-not-exhaustive scope
        // ConfirmRemoveDatabase/LogicalDatabaseService.DeleteAsync already use for "who is attached":
        // an app still wired the pre-2026-08-22 way (a materialized EnvironmentVariable copy, no join
        // row at all) is not caught here either. A null ManagedServiceDatabaseId on this instance can
        // only mean the default — every other engine's fallback resolves there (DatabasesController.Attach).
        var attachments = await db.AppManagedServices.AsNoTracking()
            .Where(a => a.ManagedServiceId == service.Id
                        && (a.ManagedServiceDatabaseId == null || databaseIds.Contains(a.ManagedServiceDatabaseId.Value)))
            .Select(a => new { a.ManagedServiceDatabaseId, AppName = a.App!.Name })
            .ToListAsync(ct);

        // D2 (in flight alongside this task) has not shipped per-logical-database backups yet. Every
        // backup this instance ever took, before D1 existed, was definitionally a backup of the one
        // database it had — so the instance-wide history already answers "when was this last backed
        // up" for the default row alone. Anything created after D1 has no backup path of its own yet.
        var latestBackup = hasDefault
            ? await db.Backups.AsNoTracking()
                .Where(b => b.TargetRef == service.Id.ToString()
                            && (b.Type == BackupType.Database || b.Type == BackupType.Service))
                .OrderByDescending(b => b.CreatedAt)
                .FirstOrDefaultAsync(ct)
            : null;

        return databases.Select(d =>
        {
            var attachedApps = attachments
                .Where(a => a.ManagedServiceDatabaseId == d.Id || (d.IsDefault && a.ManagedServiceDatabaseId == null))
                .Select(a => a.AppName).Order().ToList();

            return new LogicalDatabaseRowViewModel(
                d.Id, d.Name, d.IsDefault, d.Username, attachedApps,
                SizeBytes: null,
                BackupTrackingAvailable: d.IsDefault,
                LastBackupAt: d.IsDefault ? (latestBackup?.FinishedAt ?? latestBackup?.CreatedAt) : null,
                LastBackupStatus: d.IsDefault ? latestBackup?.Status : null,
                CanRename: !d.IsDefault && Harbora.Infrastructure.Services.DatabaseGrantSql.SupportsRename(service.Type),
                CanDelete: !d.IsDefault);
        }).ToList();
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
            s.MemoryLimitBytes, s.ErrorMessage);
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

        await using var quotaReservation = await quota.AcquireCreationLockAsync(WorkspaceId, ct);
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

        // D1 (2026-08-25 shared-databases plan): the instance's own admin database, materialised as
        // its first logical database so the very first attachment already has one to point at.
        if (Harbora.Domain.Services.ManagedServiceDatabase.DefaultFor(service) is { } defaultDatabase)
            db.ManagedServiceDatabases.Add(defaultDatabase);

        try
        {
            await creationBilling.SaveAsync(WorkspaceId,
                [new(Harbora.Domain.Billing.BilledResourceType.Service,
                    service.Id, service.Name, service.InstanceSizeKey, service.ServerId)], ct);
        }
        catch (Harbora.Infrastructure.Billing.CreationPaymentRequiredException ex)
        {
            db.ChangeTracker.Clear();
            ModelState.AddModelError(string.Empty, IsFa ? ex.ReasonFa : ex.Message);
            await PopulateCreateAsync(ct);
            return View(model);
        }

        await quotaReservation.CommitAsync(ct);

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

    /// <summary>
    /// Recreates the container from the current definition. Two names for one action: when the
    /// service is <see cref="ServiceStatus.Failed"/> this is the retry P4 (2026-08-17
    /// app-environment-management design) asked for — the audit that found it called it "a working
    /// retry mislabelled 'Rebuild container' and never gated on Failed" — and the fix is to
    /// re-present it, not to write a second action beside it that would queue the exact same
    /// <see cref="IManagedServiceEngine.QueueProvisionAsync"/>.
    /// </summary>
    [HttpPost("{id:guid}/reprovision")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.DatabasesManage)]
    public async Task<IActionResult> Reprovision(Guid id, CancellationToken ct)
    {
        await Guard(id, ct);
        var svc = await db.ManagedServices.FirstOrDefaultAsync(s => s.Id == id && s.WorkspaceId == WorkspaceId, ct);
        if (svc is null) return NotFound();

        var wasFailed = svc.Status == ServiceStatus.Failed;

        await engine.QueueProvisionAsync(id, ct);

        // Says what it costs. The container is replaced, so open connections drop — the data volume
        // is kept, which is the part people actually worry about.
        TempData["Message"] = wasFailed
            ? (IsFa
                ? $"در حال تلاش دوباره برای راه‌اندازی {svc.Name}."
                : $"Retrying the provision of {svc.Name}.")
            : (IsFa
                ? $"{svc.Name} در حال بازسازی است. داده‌ها حفظ می‌شوند؛ اتصال‌های باز قطع می‌شوند."
                : $"{svc.Name} is being rebuilt. The data is kept; open connections will drop.");

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

        // C1 (2026-08-22 config-delivery plan): the AppStorageBucket/StorageController.Delete idiom
        // — a database with apps still explicitly attached is refused by name, before EF's own
        // DeleteBehavior.Restrict on AppManagedService.ManagedServiceId would ever turn this into a
        // raw constraint violation. Apps still wired the pre-2026-08-22 way (a materialized
        // EnvironmentVariable copy, no join row) are not caught here — ConfirmRemove's advisory list
        // still names those, but this hard refusal is authoritative only for what it can enforce at
        // the database level.
        var attachedTo = await db.AppManagedServices.AsNoTracking()
            .Where(ms => ms.ManagedServiceId == id)
            .Select(ms => ms.App!.Name)
            .ToListAsync(ct);

        if (attachedTo.Count > 0)
        {
            TempData["Error"] = IsFa
                ? $"این دیتابیس هنوز به {NamedList(attachedTo)} متصل است. برای حذف، ابتدا آن را از همه‌ی اپ‌ها جدا کنید."
                : $"This database is still attached to {NamedList(attachedTo)}. Detach it from every app first, then delete it.";
            return RedirectToAction(nameof(ConfirmRemove), new { id, deleteData });
        }

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
    ///
    /// <para>
    /// P4 (2026-08-17 app-environment-management design): used to end here, on a one-line flash
    /// message telling the person to go and redeploy the affected apps by hand. It now ends on
    /// <see cref="RotateConfirm"/> instead, which lists exactly what was rewritten and can queue
    /// every one of those redeploys in a single press — <c>DeploymentEngine.QueueDeploymentAsync</c>
    /// already coalesces onto an app's own in-flight deployment (<c>DeploymentEngine.cs:101</c>), so
    /// looping over the list there is safe even if one of them is already mid-deploy.
    /// </para>
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
            if (updated.Count == 0)
            {
                TempData["Message"] = "The password was changed. No service had it stored.";
                return RedirectToAction(nameof(Details), new { id });
            }

            TempData["RotatedAppIds"] = string.Join(",", updated.Select(a => a.AppId));
            return RedirectToAction(nameof(RotateConfirm), new { id });
        }
        catch (InvalidOperationException ex)
        {
            TempData["Error"] = ex.Message;
            return RedirectToAction(nameof(Details), new { id });
        }
    }

    /// <summary>
    /// What a rotation just rewrote, and the one press that finishes it. <c>ConfirmRemove</c> is the
    /// page pattern — a browser <c>confirm()</c> was judged not good enough for destructive work —
    /// applied here to the mirror-image case: this confirms something that already happened rather
    /// than asking permission before it does, because rotating the password is not itself reversible
    /// to ask permission for; queuing redeploys is the part worth a deliberate press.
    /// </summary>
    [HttpGet("{id:guid}/rotate/confirm")]
    [Authorize(Policy = Capabilities.DatabasesManage)]
    public async Task<IActionResult> RotateConfirm(Guid id, CancellationToken ct)
    {
        if (!await access.CanTouchServiceAsync(id, Capabilities.DatabasesManage, ct)) return NotFound();
        var svc = await db.ManagedServices.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id && s.WorkspaceId == WorkspaceId, ct);
        if (svc is null) return NotFound();

        // TempData is consumed on read — a direct hit on this URL (a bookmark, a refresh) finds
        // nothing to confirm and goes back to the database's own page rather than rendering an
        // empty list that looks like a rotation touched nothing.
        //
        // Read with ToString() rather than an `is string` pattern: ASP.NET Core's default
        // CookieTempDataProvider round-trips a stored string through a type-sniffing serializer
        // that hands a bare Guid back as System.Guid, not System.String, whenever the joined list
        // happens to be exactly one id (nothing to join, so no comma survives to prove it was ever
        // a list). A rotation touching exactly one app is the ordinary case, not an edge one, so
        // this cannot be pattern-matched away.
        var idsRaw = TempData["RotatedAppIds"]?.ToString();
        if (string.IsNullOrWhiteSpace(idsRaw))
            return RedirectToAction(nameof(Details), new { id });

        var ids = idsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(Guid.Parse).ToList();
        var apps = await db.Apps.AsNoTracking()
            .Where(a => ids.Contains(a.Id) && a.WorkspaceId == WorkspaceId)
            .Select(a => new RotatedAppRowViewModel(a.Id, a.Name))
            .ToListAsync(ct);

        ViewBag.Service = svc;
        ViewBag.Apps = apps;
        ViewData["Title"] = $"Rotated {svc.Name}";
        return View();
    }

    /// <summary>Queues a redeploy for every app <see cref="RotateConfirm"/> listed.</summary>
    [HttpPost("{id:guid}/rotate/confirm")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.DatabasesManage)]
    public async Task<IActionResult> RotateQueueRedeploys(Guid id, string? appIds, CancellationToken ct)
    {
        await Guard(id, ct);

        var ids = (appIds ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries).Select(Guid.Parse).Distinct().ToList();

        var queued = 0;
        foreach (var appId in ids)
        {
            // Rotation touches every app in the workspace it finds a matching variable on; queuing
            // the redeploy is still gated per app, the same as Attach/Detach already are, because
            // managing a database does not imply the right to deploy every app in the workspace.
            if (!await access.CanTouchAppAsync(appId, Capabilities.AppsDeploy, ct)) continue;
            var exists = await db.Apps.AsNoTracking().AnyAsync(a => a.Id == appId && a.WorkspaceId == WorkspaceId, ct);
            if (!exists) continue;

            try
            {
                await deploymentEngine.QueueDeploymentAsync(
                    new DeploymentRequest(appId, DeploymentTrigger.Manual, currentUser.UserId ?? Guid.Empty), ct);
                queued++;
            }
            catch (InvalidOperationException)
            {
                // Already mid-deploy, or refused for this one app alone — the loop's job is to queue
                // what it safely can, not to let one app's refusal stop the rest from picking up the
                // password they are also waiting on.
            }
        }

        TempData["Message"] = queued == 0
            ? (IsFa ? "هیچ دیپلویی صف نشد." : "Nothing was queued.")
            : (IsFa ? $"{queued} دیپلوی صف شد." : $"Queued {queued} redeploy(s).");

        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>
    /// Attaches a database to an app at the back of its precedence order — the same
    /// <c>AppStorageBucket</c> shape F5 gave buckets: current max <c>AttachOrder</c> + 1, never
    /// reused, and starts <c>HasUnpublishedChanges</c> true because nothing here is live until the
    /// app's own next deploy assembles its environment (C1, 2026-08-22 config-delivery plan).
    ///
    /// <para>
    /// This is deliberately additive to, not a replacement for, the per-app <c>EnvironmentVariable</c>
    /// copies this same action wrote before this plan (2026-08-16) — see
    /// <see cref="Harbora.Domain.Services.AppManagedService"/>'s own doc for why the two do not
    /// conflict. What changed here: an attach no longer materializes anything into
    /// <c>EnvironmentVariable</c> — the connection string is computed live from this join every time
    /// <c>ConfigGroupMerge</c> runs, which is what makes real provenance ("from database 'X'") and a
    /// real <c>DeleteBehavior.Restrict</c> backstop possible. An app attached the old way keeps
    /// working exactly as it did; <see cref="ManagedServiceEngine.RotatePasswordAsync"/> still rewrites
    /// its stored copies in place, untouched by this change.
    /// </para>
    /// </summary>
    [HttpPost("{id:guid}/attach")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.DatabasesManage)]
    public async Task<IActionResult> Attach(
        Guid id, Guid appId, string? returnUrl, string? alias, Guid? databaseId, CancellationToken ct)
    {
        await Guard(id, ct);
        if (!await access.CanTouchAppAsync(appId, Capabilities.AppsEnv, ct)) return NotFound();
        var app = await db.Apps.FirstOrDefaultAsync(a => a.Id == appId && a.WorkspaceId == WorkspaceId, ct);
        if (app is null) return NotFound();

        // An environment is a private network, so it is also the wiring boundary. Without this the
        // attach succeeded and wrote a hostname resolvable only on the other network — the service
        // then started, looked healthy, and could not reach its database.
        var service = await db.ManagedServices.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (service is null) return NotFound();

        var verdict = Harbora.Infrastructure.Networking.NetworkWiring.CanAttach(
            app.EnvironmentId, service.EnvironmentId);
        if (!verdict.Allowed)
        {
            TempData["Error"] = verdict.Reason;
            return RedirectToAction(nameof(Details), new { id });
        }

        // D1 (2026-08-25 shared-databases plan): which logical database this attachment actually
        // points at. A caller that names one gets exactly that database (refused if it does not
        // belong to this instance); a caller that does not — every attach form this platform shipped
        // before D3 builds a picker — gets the instance's own default database when it has one, which
        // is what makes an attachment made today behave exactly as it always did. An instance with no
        // logical database at all (Redis/RabbitMQ/NATS, or a Postgres/MySQL/MariaDB row a migration
        // has not reached) resolves to null, the same fallback every such attachment has always used.
        Guid? resolvedDatabaseId;
        if (databaseId is { } requestedDatabaseId)
        {
            var requested = await db.ManagedServiceDatabases.AsNoTracking()
                .FirstOrDefaultAsync(d => d.Id == requestedDatabaseId && d.ManagedServiceId == id, ct);
            if (requested is null) return NotFound();
            resolvedDatabaseId = requested.Id;
        }
        else
        {
            resolvedDatabaseId = await db.ManagedServiceDatabases.AsNoTracking()
                .Where(d => d.ManagedServiceId == id && d.IsDefault)
                .Select(d => (Guid?)d.Id)
                .FirstOrDefaultAsync(ct);
        }

        // The refusal this always gave — the same database attached to the same app twice — restated
        // per logical database rather than per instance, so two different logical databases on the
        // same instance may both be attached to one app (see the two partial indexes on
        // AppManagedService in HarboraDbContext for the constraint this mirrors at the schema level).
        var alreadyAttached = resolvedDatabaseId is { } attachedDatabaseId
            ? await db.AppManagedServices.AnyAsync(x => x.AppId == appId && x.ManagedServiceDatabaseId == attachedDatabaseId, ct)
            : await db.AppManagedServices.AnyAsync(
                x => x.AppId == appId && x.ManagedServiceId == id && x.ManagedServiceDatabaseId == null, ct);

        if (alreadyAttached)
            return BackTo(returnUrl, IsFa ? "این دیتابیس از قبل به این اپ متصل است." : "This database is already attached.", error: true);

        var existingAliases = await db.AppManagedServices
            .Where(x => x.AppId == appId).Select(x => x.Alias).ToListAsync(ct);
        var resolvedAlias = Harbora.Domain.Services.AppManagedServiceAlias.Resolve(alias, service.Name, existingAliases);

        var maxOrder = await db.AppManagedServices
            .Where(x => x.AppId == appId).Select(x => (int?)x.AttachOrder).MaxAsync(ct) ?? 0;

        db.AppManagedServices.Add(new Harbora.Domain.Services.AppManagedService
        {
            AppId = appId, ManagedServiceId = id, ManagedServiceDatabaseId = resolvedDatabaseId, Alias = resolvedAlias,
            AttachOrder = maxOrder + 1, HasUnpublishedChanges = true
        });
        await db.SaveChangesAsync(ct);
        await audit.LogAsync("database.attached", "service", $"{id}:{appId}", HttpContext.Connection.RemoteIpAddress?.ToString(), ct: ct);

        return BackTo(returnUrl, IsFa
            ? $"«{service.Name}» به {app.Name} وصل شد و با نام {resolvedAlias}_* در دسترس است. متغیرهایش با استقرار بعدی این اپ اعمال می‌شوند."
            : $"Attached '{service.Name}' to {app.Name}, reachable under {resolvedAlias}_*. Its variables apply on this app's next deploy.");
    }

    /// <summary>
    /// Removes the join row. The running container keeps the connection string until the app's own
    /// next deploy — same as detaching a config group or a bucket.
    /// </summary>
    [HttpPost("{id:guid}/detach")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.DatabasesManage)]
    public async Task<IActionResult> Detach(Guid id, Guid appId, string? returnUrl, Guid? databaseId, CancellationToken ct)
    {
        await Guard(id, ct);
        if (!await access.CanTouchAppAsync(appId, Capabilities.AppsEnv, ct)) return NotFound();

        // D1 (2026-08-25 shared-databases plan): naming which logical database disambiguates an app
        // attached to two of them on the same instance. Every caller before D3 builds a picker still
        // omits it and gets the pre-D1 behaviour verbatim — the one attachment this app has on this
        // instance, whichever logical database it happens to point at.
        var query = db.AppManagedServices.Where(x => x.AppId == appId && x.ManagedServiceId == id);
        if (databaseId is { } requestedDatabaseId) query = query.Where(x => x.ManagedServiceDatabaseId == requestedDatabaseId);

        var join = await query.FirstOrDefaultAsync(ct);
        if (join is null)
            return BackTo(returnUrl, IsFa ? "این اپ به این دیتابیس وصل نیست." : "This app is not attached to this database.", error: true);

        db.AppManagedServices.Remove(join);
        await db.SaveChangesAsync(ct);
        await audit.LogAsync("database.detached", "service", $"{id}:{appId}", HttpContext.Connection.RemoteIpAddress?.ToString(), ct: ct);

        return BackTo(returnUrl, IsFa
            ? "دیتابیس جدا شد. تا استقرار بعدی، کانتینر در حال اجرا هنوز رشتهٔ اتصال آن را دارد."
            : "Detached. Until the next deploy, the running container still has its connection string.");
    }

    /// <summary>
    /// Creates a new logical database inside this instance (D1, 2026-08-25 shared-databases plan) —
    /// a real operation against the running engine, not a row Harbora invents on its own. A refusal
    /// names which engine declined and why (unsupported engine, unreachable instance, or the engine's
    /// own error), and leaves nothing behind: <see cref="Harbora.Infrastructure.Services.LogicalDatabaseService.CreateAsync"/>
    /// only ever writes the row once the engine has confirmed the database exists.
    /// </summary>
    [HttpPost("{id:guid}/logical-databases")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.DatabasesManage)]
    public async Task<IActionResult> CreateDatabase(Guid id, string? name, CancellationToken ct)
    {
        await Guard(id, ct);

        var (created, error) = await logicalDatabases.CreateAsync(id, name, ct);
        if (error is not null)
        {
            TempData["Error"] = error;
            return RedirectToAction(nameof(Details), new { id });
        }

        await audit.LogAsync("database.logical_database_created", "service", $"{id}:{created!.Id}",
            HttpContext.Connection.RemoteIpAddress?.ToString(), ct: ct);
        TempData["Message"] = IsFa ? $"پایگاه‌داده «{created.Name}» ساخته شد." : $"Created database \"{created.Name}\".";
        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>
    /// What removing this logical database will do — which apps stop working. The same
    /// <c>ConfirmRemove</c>/<c>ServiceRemovalPlan</c> idiom this controller already uses for a whole
    /// instance, one level down, except a logical database's deletion is always destructive to its
    /// own data — unlike an instance, there is no "keep the container's volume" option below it — so
    /// the typed-name confirmation is never optional here the way <c>ConfirmRemove</c>'s checkbox
    /// makes it.
    /// </summary>
    [HttpGet("{id:guid}/logical-databases/{databaseId:guid}/remove")]
    [Authorize(Policy = Capabilities.DatabasesManage)]
    public async Task<IActionResult> ConfirmRemoveDatabase(Guid id, Guid databaseId, CancellationToken ct)
    {
        if (!await access.CanTouchServiceAsync(id, Capabilities.DatabasesManage, ct)) return NotFound();

        var service = await db.ManagedServices.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id && s.WorkspaceId == WorkspaceId, ct);
        if (service is null) return NotFound();

        var logical = await db.ManagedServiceDatabases.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == databaseId && d.ManagedServiceId == id, ct);
        if (logical is null) return NotFound();

        var attachedTo = await db.AppManagedServices.AsNoTracking()
            .Where(a => a.ManagedServiceDatabaseId == databaseId)
            .Select(a => a.App!.Name)
            .ToListAsync(ct);

        ViewBag.Service = service;
        ViewBag.Database = logical;
        ViewBag.AttachedApps = attachedTo;
        ViewData["Title"] = $"Remove {logical.Name}";
        return View();
    }

    [HttpPost("{id:guid}/logical-databases/{databaseId:guid}/remove")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.DatabasesManage)]
    public async Task<IActionResult> RemoveDatabase(Guid id, Guid databaseId, string? confirmName, CancellationToken ct)
    {
        await Guard(id, ct);

        var logical = await db.ManagedServiceDatabases.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == databaseId && d.ManagedServiceId == id, ct);
        if (logical is null) return NotFound();

        if (!Harbora.Infrastructure.Services.ServiceRemovalPlan.IsConfirmed(true, confirmName, logical.Name))
        {
            TempData["Error"] = IsFa
                ? $"برای حذف، نام را دقیقاً بنویسید: {logical.Name}"
                : $"To delete this database, type its name exactly: {logical.Name}";
            return RedirectToAction(nameof(ConfirmRemoveDatabase), new { id, databaseId });
        }

        var error = await logicalDatabases.DeleteAsync(databaseId, ct);
        if (error is not null)
        {
            TempData["Error"] = error;
            return RedirectToAction(nameof(ConfirmRemoveDatabase), new { id, databaseId });
        }

        await audit.LogAsync("database.logical_database_removed", "service", $"{id}:{databaseId}",
            HttpContext.Connection.RemoteIpAddress?.ToString(), ct: ct);
        TempData["Message"] = IsFa ? $"«{logical.Name}» حذف شد." : $"{logical.Name} was deleted.";
        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>
    /// Renames a logical database in place (D3, 2026-08-25 shared-databases plan) — offered only
    /// where <see cref="Harbora.Infrastructure.Services.DatabaseGrantSql.SupportsRename"/> says the
    /// engine can do it losslessly (PostgreSQL) and never for the instance's own default database.
    /// Not gated on a typed-name confirmation the way removal is: nothing here is destroyed, only
    /// renamed, and every app attached is marked stale exactly as a password rotation already is.
    /// </summary>
    [HttpPost("{id:guid}/logical-databases/{databaseId:guid}/rename")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.DatabasesManage)]
    public async Task<IActionResult> RenameDatabase(Guid id, Guid databaseId, string? name, CancellationToken ct)
    {
        await Guard(id, ct);

        var error = await logicalDatabases.RenameAsync(databaseId, name, ct);
        if (error is not null)
        {
            TempData["Error"] = error;
            return RedirectToAction(nameof(Details), new { id });
        }

        await audit.LogAsync("database.logical_database_renamed", "service", $"{id}:{databaseId}",
            HttpContext.Connection.RemoteIpAddress?.ToString(), ct: ct);
        TempData["Message"] = IsFa
            ? "پایگاه‌داده تغییر نام داد. تا استقرار بعدی، اپ‌های متصل هنوز نام قبلی را دارند."
            : "The database was renamed. Attached apps still have the old name until they redeploy.";
        return RedirectToAction(nameof(Details), new { id });
    }

    private IActionResult BackTo(string? returnUrl, string? message, bool error = false)
    {
        TempData[error ? "Error" : "Message"] = message;
        return string.IsNullOrWhiteSpace(returnUrl) ? RedirectToAction(nameof(Details)) : LocalRedirect(returnUrl);
    }

    /// <summary>
    /// "2 apps: api, worker" — or past three, "5 apps: api, worker, cron and 2 more". The
    /// <c>ProjectsController.Delete</c> refusal idiom, reused exactly as <c>StorageController</c>
    /// already reused it for buckets.
    /// </summary>
    private string NamedList(IReadOnlyList<string> names)
    {
        const int shown = 3;
        var listed = names.Count > shown
            ? string.Join(IsFa ? "، " : ", ", names.Take(shown)) +
              (IsFa ? $" و {names.Count - shown} مورد دیگر" : $" and {names.Count - shown} more")
            : string.Join(IsFa ? "، " : ", ", names);

        return IsFa ? $"{names.Count} اپ: {listed}" : $"{names.Count} app{(names.Count == 1 ? "" : "s")}: {listed}";
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

        var environmentQuery = db.Environments.AsNoTracking().Where(e => e.WorkspaceId == WorkspaceId);
        if (await access.VisibleProjectIdsAsync(ct) is { } visible)
            environmentQuery = environmentQuery.Where(e => visible.Contains(e.ProjectId));

        ViewBag.Environments = await environmentQuery
            .OrderBy(e => e.Project!.Name).ThenByDescending(e => e.IsDefault).ThenBy(e => e.Name)
            .Select(e => new SelectListItem($"{e.Project!.Name} · {e.Name}", e.Id.ToString()))
            .ToListAsync(ct);

        // The server list and the tier list, and the plan lookup that filtered them, all used to be
        // built here for two dropdowns. The SizePicker view component asks those questions itself —
        // the plan's pool, the plan's allowed tiers, each host's free capacity and what the pair costs
        // — so repeating them here would be queries a request whose answers nothing reads.
    }


    private static string Slugify(string name)
    {
        var slug = NonSlug().Replace(name.Trim().ToLowerInvariant(), "-").Trim('-');
        return string.IsNullOrWhiteSpace(slug) ? "svc-" + Guid.NewGuid().ToString("N")[..6] : slug;
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonSlug();
}
