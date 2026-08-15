using Harbora.Domain.Authorization;
using Harbora.Domain.Services;
using Harbora.Web.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Web.Controllers;

/// <summary>
/// The database detail tabs. Separate file, same controller: every route here is still
/// /databases/{id}/…, and splitting them across controllers sends the next reader hunting — the same
/// reasoning <c>AppsController.Tabs.cs</c> used.
///
/// <para>
/// Only Usage gets a route of its own here. Access already has one (<see cref="Access"/> in
/// <c>DatabaseAccessActions.cs</c>) and Backups points at the workspace's existing backup surface
/// (<c>/backups</c>) — the shell's tab strip links to both rather than rebuilding either.
/// </para>
/// </summary>
public sealed partial class DatabasesController
{
    /// <summary>
    /// The database behind any tab, or null when this caller may not see it.
    ///
    /// Deliberately loads no collections — no grants, no backups, no history — the same reasoning
    /// <c>AppsController.LoadHeaderAsync</c> uses: the header and tab strip need only what every tab
    /// shows, and a tab that does not read a collection should not pay for loading it.
    /// </summary>
    private async Task<ManagedService?> LoadHeaderAsync(Guid id, CancellationToken ct)
    {
        if (!await access.CanSeeServiceAsync(id, ct)) return null;

        return await db.ManagedServices
            .AsNoTracking()
            .Include(s => s.Environment).ThenInclude(e => e!.Project)
            .FirstOrDefaultAsync(s => s.Id == id && s.WorkspaceId == WorkspaceId, ct);
    }

    /// <summary>
    /// What this database is consuming right now — CPU, memory, storage, each against its own
    /// ceiling, how many apps are attached, and the same figures charted over time. Moved out of
    /// Overview rather than rewritten: the same "this moment" grid and the same two metrics-chart
    /// islands, reading the same <see cref="Row"/> helper the list and Overview already use.
    ///
    /// <para>
    /// <paramref name="minutes"/> mirrors <c>AppsController.Usage</c>'s own parameter — taken here,
    /// clamped to the three offered windows, and put on the view model rather than read from
    /// <c>Request.Query</c> in Razor.
    /// </para>
    /// </summary>
    [HttpGet("{id:guid}/usage")]
    public async Task<IActionResult> Usage(Guid id, int? minutes, CancellationToken ct)
    {
        var service = await LoadHeaderAsync(id, ct);
        if (service is null) return NotFound();

        var selectedMinutes = Harbora.Infrastructure.Monitoring.UsageRangeWindow.Clamp(minutes);

        var canManage = await access.CanTouchServiceAsync(service.Id, Capabilities.DatabasesManage, ct);

        var metrics = await db.MonitoringMetrics.AsNoTracking()
            .Where(m => m.ResourceRef == service.ContainerName
                        && (m.Name == "cpu.percent" || m.Name == "mem.used"))
            .OrderByDescending(m => m.Timestamp).Take(200).ToListAsync(ct);

        // Who is attached, the same way BuildOverviewAsync answers it: every app in the workspace,
        // checked against this one container name.
        var apps = await db.Apps.AsNoTracking()
            .Include(a => a.EnvironmentVariables)
            .Where(a => a.WorkspaceId == WorkspaceId).ToListAsync(ct);
        var connections = usage.ConnectionsFor(apps, [service.ContainerName]);

        var row = Row(service, metrics, connections);

        return View(new DatabaseUsageViewModel
        {
            Id = row.Id, Name = row.Name, Type = row.Type, Version = row.Version, Status = row.Status,
            Project = row.Project, Environment = row.Environment,
            CanManage = canManage,
            CurrentTab = "usage",
            CpuPercent = row.CpuPercent,
            CpuLimit = service.CpuLimit,
            MemoryBytes = row.MemoryBytes,
            MemoryLimitBytes = service.MemoryLimitBytes,
            StorageBytes = row.StorageBytes,
            StorageMeasuredAt = row.StorageMeasuredAt,
            DiskLimitBytes = service.DiskLimitBytes,
            LinkedApps = row.LinkedApps,
            SelectedMinutes = selectedMinutes,
        });
    }
}
