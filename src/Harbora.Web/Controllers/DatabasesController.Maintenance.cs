using Harbora.Domain.Authorization;
using Harbora.Domain.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Web.Controllers;

/// <summary>
/// Scheduled VACUUM/VACUUM FULL/ANALYZE/REINDEX (PostgreSQL) and OPTIMIZE TABLE (MySQL/MariaDB)
/// against one logical database (2.3, round-2 market-gaps plan). Same "/databases/{id}/…" prefix as
/// <c>DatabasesController.Pitr.cs</c> and <c>DatabasesController.LogicalDatabaseBackups.cs</c>, kept
/// as a separate file for the same reason those are.
/// </summary>
public sealed partial class DatabasesController
{
    /// <summary>Runs one maintenance statement now — the exact path
    /// <c>DatabaseMaintenanceScheduler</c>'s own tick also queues through, see
    /// <see cref="Harbora.Infrastructure.Services.DatabaseMaintenanceService"/>'s type doc.</summary>
    [HttpPost("{id:guid}/logical-databases/{databaseId:guid}/maintenance/run")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.DatabasesManage)]
    public async Task<IActionResult> RunMaintenance(
        Guid id, Guid databaseId, DatabaseMaintenanceOperation operation, CancellationToken ct)
    {
        await Guard(id, ct);
        var logical = await db.ManagedServiceDatabases.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == databaseId && d.ManagedServiceId == id, ct);
        if (logical is null) return NotFound();

        var (runId, error) = await maintenance.QueueAsync(
            databaseId, operation, DatabaseMaintenanceTrigger.Manual, null, ct);

        if (error is not null)
        {
            TempData["Error"] = error;
            return RedirectToAction(nameof(Details), new { id });
        }

        var label = Harbora.Infrastructure.Services.DatabaseMaintenanceSql.Label(operation);
        await audit.LogAsync("database.maintenance_queued", "service", $"{id}:{databaseId}",
            HttpContext.Connection.RemoteIpAddress?.ToString(), workspaceId: WorkspaceId, ct: ct);

        TempData["Message"] = IsFa
            ? $"{label} روی «{logical.Name}» صف شد."
            : $"{label} on \"{logical.Name}\" was queued.";
        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>Creates or updates the one schedule this database has for this operation.</summary>
    [HttpPost("{id:guid}/logical-databases/{databaseId:guid}/maintenance/schedule")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.DatabasesManage)]
    public async Task<IActionResult> SaveMaintenanceSchedule(
        Guid id, Guid databaseId, DatabaseMaintenanceOperation operation, bool enabled,
        string? schedule, string? timezone, CancellationToken ct)
    {
        await Guard(id, ct);
        var logical = await db.ManagedServiceDatabases.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == databaseId && d.ManagedServiceId == id, ct);
        if (logical is null) return NotFound();

        var error = await maintenance.SetScheduleAsync(databaseId, operation, enabled, schedule, timezone, ct);
        if (error is not null)
        {
            TempData["Error"] = error;
            return RedirectToAction(nameof(Details), new { id });
        }

        var label = Harbora.Infrastructure.Services.DatabaseMaintenanceSql.Label(operation);
        await audit.LogAsync("database.maintenance_schedule_saved", "service", $"{id}:{databaseId}",
            HttpContext.Connection.RemoteIpAddress?.ToString(), workspaceId: WorkspaceId, ct: ct);

        TempData["Message"] = IsFa
            ? $"زمان‌بندی {label} برای «{logical.Name}» ذخیره شد."
            : $"The {label} schedule for \"{logical.Name}\" was saved.";
        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>Removes a maintenance schedule. Idempotent on an already-missing row, the same as
    /// every other delete on this controller.</summary>
    [HttpPost("{id:guid}/logical-databases/{databaseId:guid}/maintenance/schedule/{scheduleId:guid}/delete")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.DatabasesManage)]
    public async Task<IActionResult> DeleteMaintenanceSchedule(
        Guid id, Guid databaseId, Guid scheduleId, CancellationToken ct)
    {
        await Guard(id, ct);
        var owned = await db.DatabaseMaintenanceSchedules.AsNoTracking()
            .AnyAsync(s => s.Id == scheduleId && s.ManagedServiceDatabaseId == databaseId, ct);
        if (!owned) return NotFound();

        await maintenance.DeleteScheduleAsync(scheduleId, ct);

        await audit.LogAsync("database.maintenance_schedule_removed", "service", $"{id}:{databaseId}",
            HttpContext.Connection.RemoteIpAddress?.ToString(), workspaceId: WorkspaceId, ct: ct);

        TempData["Message"] = IsFa ? "زمان‌بندی حذف شد." : "The schedule was removed.";
        return RedirectToAction(nameof(Details), new { id });
    }
}
