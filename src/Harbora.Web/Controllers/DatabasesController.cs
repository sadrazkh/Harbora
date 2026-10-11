using System.Security.Cryptography;
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
    IServerEngineFactory engines,
    Harbora.Infrastructure.Backups.WalArchivingService walArchiving,
    Harbora.Infrastructure.Backups.PitrRestoreService pitrRestore,
    Harbora.Infrastructure.Services.DatabaseMaintenanceService maintenance,
    Harbora.Application.Abstractions.ISystemClock clock,
    // Round six (phase 0b): creation, removal and attach/detach live in these services now, which the
    // in-panel agent calls too. DI always supplies both; see Provisioning below for why they are
    // optional.
    IDatabaseProvisioningService? provisioning = null,
    IDatabaseAttachmentService? attachments = null) : Controller
{
    private Guid WorkspaceId => currentUser.WorkspaceId ?? Guid.Empty;

    /// <summary>
    /// The registered service, or — for a controller built by hand, as the tests that exercise its
    /// other actions do — one built from the same dependencies this controller was handed, so it
    /// behaves identically either way. That fallback is also the only reader left of
    /// <c>scheduler</c> and <c>protector</c>, which the creation code used before it moved; they stay
    /// in the constructor because those tests pass them by position.
    /// </summary>
    private IDatabaseProvisioningService Provisioning => provisioning ??= new DatabaseProvisioningService(
        db, engine, quota, scheduler, protector, projects, access, creationBilling, audit, currentUser);

    private IDatabaseAttachmentService Attachments =>
        attachments ??= new DatabaseAttachmentService(db, access, audit, currentUser);

    private string? ClientIp => HttpContext.Connection.RemoteIpAddress?.ToString();

    private string InReadersLanguage(ServiceRefusal refusal) => IsFa ? refusal.MessageFa : refusal.Message;

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
            HttpContext.Connection.RemoteIpAddress?.ToString(), workspaceId: WorkspaceId, ct: ct);

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
        // 5.1: same union AppsController.Index applies, for the same reason — a service-scoped
        // grant must show that one service without opening its whole project.
        if (visibleProjectIds is { } visible)
        {
            var grantedServiceIds = await access.GrantedServiceIdsAsync(ct);
            query = query.Where(s => visible.Contains(s.Environment!.ProjectId) || grantedServiceIds.Contains(s.Id));
        }

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
                CanManageLogicalDatabasesLocally = logicalDatabases.CanCreateLocally,
                Replicas = await BuildReplicaRowsAsync(service.Id, ct),
                ReplicationSupported = Harbora.Infrastructure.Services.ReplicationSupport.Supports(service.Type),
                ReplicationUnsupportedReason = Harbora.Infrastructure.Services.ReplicationSupport.Supports(service.Type)
                    ? null : Harbora.Infrastructure.Services.ReplicationSupport.UnsupportedReason(service.Type),
                PrimaryName = service.PrimaryManagedServiceId is { } primaryId
                    ? services.FirstOrDefault(s => s.Id == primaryId)?.Name
                    : null,
                OwnLag = service.PrimaryManagedServiceId is not null
                    ? Harbora.Infrastructure.Backups.ReplicationLagPresenter.Compute(
                        await db.ReplicationLagStatuses.AsNoTracking()
                            .FirstOrDefaultAsync(s => s.ManagedServiceId == service.Id, ct), clock.UtcNow)
                    : null,
                Maintenance = await BuildMaintenanceRowsAsync(service, ct),
                MaintenanceSupported = DatabaseMaintenanceSql.Supports(service.Type),
                MaintenanceUnsupportedReason = DatabaseMaintenanceSql.Supports(service.Type)
                    ? null : DatabaseMaintenanceSql.UnsupportedReason(service.Type),
                CanRunMaintenanceLocally = maintenance.CanRunLocally
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
            RedisEvictionPolicy = service.RedisEvictionPolicy,
            RedisMaxMemoryBytes = service.RedisMaxMemoryBytes,
            RedisMemoryPolicyUnpublished = service.HasUnpublishedChanges,
            LogicalDatabases = logicalSupported ? await BuildLogicalDatabaseRowsAsync(service, ct) : [],
            LogicalDatabasesSupported = logicalSupported,
            LogicalDatabasesUnsupportedReason = logicalSupported
                ? null : Harbora.Infrastructure.Services.DatabaseGrantSql.UnsupportedReason(service.Type),
            CanManageLogicalDatabasesLocally = logicalDatabases.CanCreateLocally,
            Maintenance = await BuildMaintenanceRowsAsync(service, ct),
            MaintenanceSupported = DatabaseMaintenanceSql.Supports(service.Type),
            MaintenanceUnsupportedReason = DatabaseMaintenanceSql.Supports(service.Type)
                ? null : DatabaseMaintenanceSql.UnsupportedReason(service.Type),
            CanRunMaintenanceLocally = maintenance.CanRunLocally,
            PgVectorEnabled = service.PgVectorEnabled,
            PgVectorUnpublished = service.HasUnpublishedChanges,
            // 3.1 (round-2 market-gaps plan): computed fresh on every render, never cached — a stale
            // "window" is exactly the "green dot for a probe that never fired" defect this feature
            // exists to refuse. Only meaningful for PostgreSQL; PitrSupport.Supports gates the panel.
            PitrEnabled = service.PitrEnabled,
            PitrUnpublished = service.HasUnpublishedChanges,
            PitrWindow = Harbora.Infrastructure.Backups.PitrSupport.Supports(service.Type)
                ? await walArchiving.RecoveryWindowAsync(service.Id, clock.UtcNow, ct)
                : null,
            Replicas = await BuildReplicaRowsAsync(service.Id, ct),
            ReplicationSupported = Harbora.Infrastructure.Services.ReplicationSupport.Supports(service.Type),
            ReplicationUnsupportedReason = Harbora.Infrastructure.Services.ReplicationSupport.Supports(service.Type)
                ? null : Harbora.Infrastructure.Services.ReplicationSupport.UnsupportedReason(service.Type),
            PrimaryName = service.PrimaryManagedServiceId is { } primaryId
                ? await db.ManagedServices.AsNoTracking().Where(s => s.Id == primaryId)
                    .Select(s => s.Name).FirstOrDefaultAsync(ct)
                : null,
            OwnLag = service.PrimaryManagedServiceId is not null
                ? Harbora.Infrastructure.Backups.ReplicationLagPresenter.Compute(
                    await db.ReplicationLagStatuses.AsNoTracking()
                        .FirstOrDefaultAsync(s => s.ManagedServiceId == service.Id, ct), clock.UtcNow)
                : null
        };
    }

    /// <summary>
    /// The rows for this instance's own "read replicas" panel (3.2, round-2 market-gaps plan) — lag
    /// computed fresh from <see cref="ReplicationLagPresenter.Compute"/> every render, never cached,
    /// for the reason that class states: a cached reading is exactly the "green dot for a probe that
    /// never fired" defect this whole feature exists to refuse.
    /// </summary>
    private async Task<IReadOnlyList<ReplicaRowViewModel>> BuildReplicaRowsAsync(Guid serviceId, CancellationToken ct)
    {
        var replicas = await db.ManagedServices.AsNoTracking()
            .Where(r => r.PrimaryManagedServiceId == serviceId)
            .OrderBy(r => r.CreatedAt)
            .ToListAsync(ct);
        if (replicas.Count == 0) return [];

        var replicaIds = replicas.Select(r => r.Id).ToList();
        var statuses = await db.ReplicationLagStatuses.AsNoTracking()
            .Where(s => replicaIds.Contains(s.ManagedServiceId))
            .ToDictionaryAsync(s => s.ManagedServiceId, ct);

        return replicas.Select(r => new ReplicaRowViewModel(
            r.Id, r.Name, r.Status,
            Harbora.Infrastructure.Backups.ReplicationLagPresenter.Compute(
                statuses.GetValueOrDefault(r.Id), clock.UtcNow))).ToList();
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
                CanDelete: !d.IsDefault,
                HasVectorExtension: d.HasVectorExtension,
                VectorExtensionCheckedAt: d.VectorExtensionCheckedAt);
        }).ToList();
    }

    /// <summary>
    /// One row per (logical database, maintenance operation) this engine offers (2.3, round-2
    /// market-gaps plan) — see <see cref="DatabaseMaintenanceRowViewModel"/>. Reads the most recent
    /// runs across every logical database on this instance and keeps only the freshest one per
    /// (database, operation) pair, rather than a second query per row.
    /// </summary>
    private async Task<IReadOnlyList<DatabaseMaintenanceRowViewModel>> BuildMaintenanceRowsAsync(
        ManagedService service, CancellationToken ct)
    {
        var operations = DatabaseMaintenanceSql.OperationsFor(service.Type);
        if (operations.Count == 0) return [];

        var databases = await db.ManagedServiceDatabases.AsNoTracking()
            .Where(d => d.ManagedServiceId == service.Id)
            .OrderByDescending(d => d.IsDefault).ThenBy(d => d.Name)
            .ToListAsync(ct);
        if (databases.Count == 0) return [];

        var databaseIds = databases.Select(d => d.Id).ToList();

        var schedules = await db.DatabaseMaintenanceSchedules.AsNoTracking()
            .Where(s => databaseIds.Contains(s.ManagedServiceDatabaseId))
            .ToListAsync(ct);

        // Most recent runs across every database/operation on this instance. Grouped in memory below
        // for the freshest one per pair — a full history page can query DatabaseMaintenanceRuns
        // directly rather than this overview needing a second round trip per row.
        var recentRuns = await db.DatabaseMaintenanceRuns.AsNoTracking()
            .Where(r => databaseIds.Contains(r.ManagedServiceDatabaseId))
            .OrderByDescending(r => r.CreatedAt)
            .Take(200)
            .ToListAsync(ct);

        var rows = new List<DatabaseMaintenanceRowViewModel>();
        foreach (var d in databases)
        {
            foreach (var operation in operations)
            {
                var schedule = schedules.FirstOrDefault(
                    s => s.ManagedServiceDatabaseId == d.Id && s.Operation == operation);
                var pairRuns = recentRuns
                    .Where(r => r.ManagedServiceDatabaseId == d.Id && r.Operation == operation)
                    .Take(3)
                    .Select(r => new DatabaseMaintenanceRunHistoryEntry(
                        r.Status, r.FinishedAt ?? r.StartedAt ?? r.CreatedAt,
                        r is { StartedAt: { } s, FinishedAt: { } f } ? f - s : null, r.Error))
                    .ToList();
                var lastRun = recentRuns.FirstOrDefault(
                    r => r.ManagedServiceDatabaseId == d.Id && r.Operation == operation);

                rows.Add(new DatabaseMaintenanceRowViewModel(
                    d.Id, d.Name, operation,
                    DatabaseMaintenanceSql.Label(operation),
                    DatabaseMaintenanceSql.Describe(operation),
                    DatabaseMaintenanceSql.IsOnline(operation),
                    schedule?.Id, schedule?.Schedule, schedule?.Timezone, schedule?.Enabled ?? false,
                    schedule?.NextRunAt,
                    lastRun?.Status, lastRun?.FinishedAt ?? lastRun?.StartedAt,
                    lastRun is { StartedAt: { } started, FinishedAt: { } finished } ? finished - started : null,
                    lastRun?.Error,
                    pairRuns));
            }
        }
        return rows;
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
        var request = new DatabaseCreateRequest(
            WorkspaceId, currentUser.UserId ?? Guid.Empty, model.Name, model.Type, model.Version,
            model.EnvironmentId, model.InstanceSizeKey, model.ServerId,
            model.RedisEvictionPolicy, model.RedisMaxMemoryMb, ClientIp);

        // A form that did not bind (an empty name) is never created. The service is still asked what
        // else it would refuse, so the page shows those beside it, as it always has.
        if (!ModelState.IsValid)
        {
            AddCreateRefusals(await Provisioning.ValidateCreateAsync(request, ct));
            await PopulateCreateAsync(ct);
            return View(model);
        }

        var outcome = await Provisioning.CreateAsync(request, ct);
        if (!outcome.Ok)
        {
            AddCreateRefusals(outcome.Refusals);
            await PopulateCreateAsync(ct);
            return View(model);
        }

        return RedirectToAction(nameof(Details), new { id = outcome.Value!.ServiceId });
    }

    /// <summary>
    /// A refusal's <see cref="ServiceRefusal.Field"/> is the request property it is about, named the
    /// same as the form's; one with no field is a form-level error.
    /// </summary>
    private void AddCreateRefusals(IReadOnlyList<ServiceRefusal> refusals)
    {
        foreach (var refusal in refusals)
        {
            var key = refusal.Field ?? string.Empty;

            // The view model's own [Required] already said the name is missing; the service says it
            // again for callers with no model binding, and the form should not say it twice.
            if (key == nameof(CreateServiceViewModel.Name) && ModelState[key]?.Errors.Count > 0) continue;

            ModelState.AddModelError(key, InReadersLanguage(refusal));
        }
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
    /// Sets a Redis instance's memory eviction policy — see
    /// <see cref="Harbora.Infrastructure.Services.RedisMemoryPolicy"/> for why a cache and a queue
    /// need opposite answers to the same setting.
    ///
    /// <para>
    /// Applied live immediately when the instance is running — Redis takes both settings through
    /// <c>CONFIG SET</c> — and always saved so the next rebuild bakes the choice into the container's
    /// own command line. The three outcomes are told apart honestly rather than folded into one
    /// "saved" banner: a live apply does not survive a plain restart until that rebuild happens, and a
    /// stopped instance or a refusal from the engine are different facts from either.
    /// </para>
    /// </summary>
    [HttpPost("{id:guid}/redis-memory")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.DatabasesManage)]
    public async Task<IActionResult> RedisMemory(Guid id, string? evictionPolicy, int maxMemoryMb, CancellationToken ct)
    {
        await Guard(id, ct);

        var maxMemoryBytes = maxMemoryMb <= 0 ? 0L : (long)maxMemoryMb * 1024 * 1024;

        try
        {
            var outcome = await engine.UpdateRedisMemoryPolicyAsync(id, evictionPolicy, maxMemoryBytes, ct);

            if (!outcome.WasRunning)
            {
                TempData["Message"] = IsFa
                    ? "ذخیره شد. با بازسازی بعدی کانتینر اعمال می‌شود."
                    : "Saved. It applies the next time the container is rebuilt.";
            }
            else if (outcome.AppliedLive)
            {
                TempData["Message"] = IsFa
                    ? "همین الان روی نمونهٔ در حال اجرا اعمال شد. برای ماندگاری پس از هر راه‌اندازی مجدد، کانتینر را بازسازی کنید."
                    : "Applied to the running instance right now. Rebuild the container so it survives a restart.";
            }
            else
            {
                TempData["Error"] = IsFa
                    ? $"ذخیره شد، ولی نمونهٔ در حال اجرا آن را نپذیرفت: {outcome.LiveApplyError} تا بازسازی بعدی اعمال نمی‌شود."
                    : $"Saved, but the running instance refused it: {outcome.LiveApplyError} It will not take effect until the next rebuild.";
            }
        }
        catch (Harbora.Infrastructure.Services.RedisMemoryPolicyRefusedException ex)
        {
            TempData["Error"] = (IsFa ? ex.ReasonFa : null) ?? ex.Message;
        }

        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>
    /// Turns pgvector support on or off for this PostgreSQL instance (1.7, pgvector-as-option plan).
    ///
    /// <para>
    /// The stock <c>postgres</c> image carries none of the extension's files, and swapping images is
    /// only ever safe on a full container recreate — so, exactly like a resize or a Redis memory
    /// policy, this only ever <em>stores</em> the request and marks
    /// <see cref="Harbora.Domain.Services.ManagedService.HasUnpublishedChanges"/>; <see cref="Reprovision"/>
    /// is what actually rebuilds the container onto <see cref="Harbora.Infrastructure.Services.PgVectorImage"/>'s
    /// tag. An existing running instance is therefore never silently left behind and never told a
    /// half-truth about being ready: it is told plainly that a rebuild is what makes this real, the
    /// same "applies on next deploy" idiom this page already uses for a resize.
    /// </para>
    ///
    /// <para>
    /// Turning it back off is refused by name — never silently accepted — while any logical database
    /// on this instance still has the extension installed: rebuilding onto the plain image afterward
    /// does not drop anything, but it does break every query touching a <c>vector</c> column until
    /// pgvector is turned on again, and that should never surface later as an opaque engine error the
    /// operator has to trace back to a setting flipped weeks earlier.
    /// </para>
    /// </summary>
    [HttpPost("{id:guid}/pgvector")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.DatabasesManage)]
    public async Task<IActionResult> PgVector(Guid id, bool enable, CancellationToken ct)
    {
        await Guard(id, ct);
        var exists = await db.ManagedServices.AsNoTracking().AnyAsync(s => s.Id == id && s.WorkspaceId == WorkspaceId, ct);
        if (!exists) return NotFound();

        // LogicalDatabaseService.SetPgVectorEnabledAsync owns both the refusal (still installed
        // somewhere, or a non-PostgreSQL instance) and the write — kept there rather than duplicated
        // here so it is provable without an HTTP round-trip, the same reasoning every other
        // logical-database operation on this page already follows.
        var error = await logicalDatabases.SetPgVectorEnabledAsync(id, enable, ct);
        if (error is not null)
        {
            TempData["Error"] = error;
            return RedirectToAction(nameof(Details), new { id });
        }

        TempData["Message"] = enable
            ? (IsFa
                ? "ذخیره شد. با بازسازی کانتینر، ایمیجِ دارای pgvector اجرا می‌شود."
                : "Saved. Rebuild the container to run the pgvector-capable image.")
            : (IsFa
                ? "ذخیره شد. با بازسازی بعدی، ایمیج معمولی PostgreSQL دوباره اجرا می‌شود."
                : "Saved. The next rebuild will run the plain PostgreSQL image again.");

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
        var svc = await db.ManagedServices.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id && s.WorkspaceId == WorkspaceId, ct);
        if (svc is null) return NotFound();

        // Apps still attached, replicas still streaming — DatabaseProvisioningService names them. Asked
        // before the typed name, so a refusal is read before anyone is made to type anything.
        var refusals = await Provisioning.ValidateRemoveAsync(id, ct);
        if (refusals.Count > 0)
        {
            TempData["Error"] = InReadersLanguage(refusals[0]);
            return RedirectToAction(nameof(ConfirmRemove), new { id, deleteData });
        }

        // Typing the name is asked for only when the data goes with it — see ServiceRemovalPlan.
        if (!Harbora.Infrastructure.Services.ServiceRemovalPlan.IsConfirmed(deleteData, confirmName, svc.Name))
        {
            TempData["Error"] = $"To delete the data, type the name exactly: {svc.Name}";
            return RedirectToAction(nameof(ConfirmRemove), new { id, deleteData });
        }

        var outcome = await Provisioning.RemoveAsync(id, deleteData, ClientIp, ct);
        if (!outcome.Ok)
        {
            TempData["Error"] = InReadersLanguage(outcome.Refusals[0]);
            return RedirectToAction(nameof(ConfirmRemove), new { id, deleteData });
        }

        var removed = outcome.Value!;
        TempData["Message"] = removed.DataDeleted
            ? $"{removed.Name} and its data were deleted."
            : $"{removed.Name} was removed. Its data is still on the server in volume \"{removed.VolumeName}\".";
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
        // DatabaseAttachmentService asks both of these again — it has callers that are not this page.
        // Asked here first because each has its own HTTP answer the service has no way to give.
        await Guard(id, ct);
        if (!await access.CanTouchAppAsync(appId, Capabilities.AppsEnv, ct)) return NotFound();

        var outcome = await Attachments.AttachAsync(id, appId, alias, databaseId, ClientIp, ct);
        if (!outcome.Ok)
        {
            var refusal = outcome.Refusals[0];
            switch (refusal.Field)
            {
                // A logical database that is not this instance's was always a 404, not a message.
                case DatabaseRefusalFields.Database:
                    return NotFound();
                // The cross-network refusal always went to the database's own page, not back to
                // wherever the form was posted from.
                case DatabaseRefusalFields.Environment:
                    TempData["Error"] = InReadersLanguage(refusal);
                    return RedirectToAction(nameof(Details), new { id });
                default:
                    return BackTo(returnUrl, InReadersLanguage(refusal), error: true);
            }
        }

        var attached = outcome.Value!;
        return BackTo(returnUrl, IsFa
            ? $"«{attached.ServiceName}» به {attached.AppName} وصل شد و با نام {attached.Alias}_* در دسترس است. متغیرهایش با استقرار بعدی این اپ اعمال می‌شوند."
            : $"Attached '{attached.ServiceName}' to {attached.AppName}, reachable under {attached.Alias}_*. Its variables apply on this app's next deploy.");
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
        // Asked here as well as in the service, for the reason Attach gives.
        await Guard(id, ct);
        if (!await access.CanTouchAppAsync(appId, Capabilities.AppsEnv, ct)) return NotFound();

        var outcome = await Attachments.DetachAsync(id, appId, databaseId, ClientIp, ct);
        if (!outcome.Ok) return BackTo(returnUrl, InReadersLanguage(outcome.Refusals[0]), error: true);

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
            HttpContext.Connection.RemoteIpAddress?.ToString(), workspaceId: WorkspaceId, ct: ct);
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
            HttpContext.Connection.RemoteIpAddress?.ToString(), workspaceId: WorkspaceId, ct: ct);
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
            HttpContext.Connection.RemoteIpAddress?.ToString(), workspaceId: WorkspaceId, ct: ct);
        TempData["Message"] = IsFa
            ? "پایگاه‌داده تغییر نام داد. تا استقرار بعدی، اپ‌های متصل هنوز نام قبلی را دارند."
            : "The database was renamed. Attached apps still have the old name until they redeploy.";
        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>
    /// Installs pgvector inside one logical database (1.7, pgvector-as-option plan). A real
    /// <c>CREATE EXTENSION</c> against the running engine, not a checkbox Harbora believes on its own
    /// — a refusal names which engine declined and why, exactly as <see cref="CreateDatabase"/>
    /// already does for the operation one level up. Idempotent: pressing this on a database that
    /// already has pgvector is a success, not an error.
    /// </summary>
    [HttpPost("{id:guid}/logical-databases/{databaseId:guid}/pgvector")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.DatabasesManage)]
    public async Task<IActionResult> EnableVectorExtension(Guid id, Guid databaseId, CancellationToken ct)
    {
        await Guard(id, ct);

        var (present, error) = await logicalDatabases.EnableVectorExtensionAsync(databaseId, ct);
        if (present != true)
        {
            TempData["Error"] = error ?? (IsFa ? "pgvector تأیید نشد." : "pgvector could not be confirmed on this database.");
            return RedirectToAction(nameof(Details), new { id });
        }

        await audit.LogAsync("database.pgvector_enabled", "service", $"{id}:{databaseId}",
            HttpContext.Connection.RemoteIpAddress?.ToString(), workspaceId: WorkspaceId, ct: ct);
        TempData["Message"] = IsFa
            ? "pgvector روی این پایگاه‌داده فعال شد."
            : "pgvector is enabled on this database.";
        return RedirectToAction(nameof(Details), new { id });
    }

    private IActionResult BackTo(string? returnUrl, string? message, bool error = false)
    {
        TempData[error ? "Error" : "Message"] = message;
        return string.IsNullOrWhiteSpace(returnUrl) ? RedirectToAction(nameof(Details)) : LocalRedirect(returnUrl);
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


    /// <summary>The container-name rule creation uses, so a replica is named the same way.</summary>
    private static string Slugify(string name) => DatabaseProvisioningService.Slugify(name);
}
