using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Web.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Web.Controllers;

/// <summary>
/// The app detail tabs. Separate file, same controller: the routes all live under
/// /apps/{id}/… and splitting them across two controllers sends the next reader hunting.
/// </summary>
public sealed partial class AppsController
{
    /// <summary>
    /// The app behind any tab, or null when this caller may not see it.
    ///
    /// <para>
    /// Deliberately loads no <em>collections</em>: no volumes, no deployments, no environment
    /// variables. That is the whole point of one route per tab — the Overview no longer pays for
    /// twenty deployments just to draw a header, and this tab pays for neither. <see cref="App.GitRepository"/>
    /// is included anyway: it is a single reference, not a collection, and the header's subtitle
    /// line needs its <c>FullName</c> on every tab.
    /// </para>
    /// </summary>
    private async Task<App?> LoadHeaderAsync(Guid id, CancellationToken ct)
    {
        if (!await access.CanSeeAppAsync(id, ct)) return null;

        return await db.Apps
            .AsNoTracking()
            .Include(a => a.GitRepository)
            .FirstOrDefaultAsync(a => a.Id == id && a.WorkspaceId == WorkspaceId, ct);
    }

    /// <summary>
    /// What this app is actually consuming, against what it was allotted — CPU, memory, disk, each
    /// against its own ceiling, plus the same figures charted over time. Moved out of Overview
    /// rather than rewritten: the measurement logic already treats "never sampled" as its own state
    /// rather than a zero (do-not-change item 18), and moving it verbatim is what keeps that true.
    ///
    /// <para>
    /// <paramref name="minutes"/> is the chart window, taken as an action parameter and clamped by
    /// <see cref="Harbora.Infrastructure.Monitoring.UsageRangeWindow.Clamp"/> rather than read from
    /// <c>Request.Query</c> in the view — an arbitrary value from the URL must not reach the range
    /// control or the chart islands unvalidated.
    /// </para>
    /// </summary>
    [HttpGet("apps/{id:guid}/usage")]
    public async Task<IActionResult> Usage(Guid id, int? minutes, CancellationToken ct)
    {
        var app = await LoadHeaderAsync(id, ct);
        if (app is null) return NotFound();

        var selectedMinutes = Harbora.Infrastructure.Monitoring.UsageRangeWindow.Clamp(minutes);

        // The container is named per deployment — old and new coexist during a cutover — so the one
        // to read is the deployment currently serving, not a name derived from the app alone.
        var active = app.ActiveDeploymentId is { } activeId
            ? await db.Deployments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == activeId, ct)
            : null;
        active ??= await db.Deployments.AsNoTracking()
            .Where(d => d.AppId == id && d.Status == DeploymentStatus.Succeeded)
            .OrderByDescending(d => d.Number)
            .FirstOrDefaultAsync(ct);

        var containerName = active is null
            ? Harbora.Infrastructure.Deployments.DeploymentPlanning.LegacyContainerName(app.Slug)
            : Harbora.Infrastructure.Deployments.DeploymentPlanning.ContainerName(app.Slug, active.Number);

        var samples = await db.MonitoringMetrics.AsNoTracking()
            .Where(m => m.ResourceRef == containerName
                        && (m.Name == "cpu.percent" || m.Name == "mem.used"))
            .OrderByDescending(m => m.Timestamp).Take(120).ToListAsync(ct);

        // Disk, alongside memory and CPU. A tier sells storage now, so the page that shows what a
        // tier gave this app has to show that figure too — and how much of it is gone.
        var disk = await AppDiskUsageAsync(app.Id, ct);

        return View(new AppUsageViewModel
        {
            Id = app.Id, Name = app.Name, Slug = app.Slug, Kind = app.Kind, Status = app.Status,
            CurrentTab = "usage",
            SourceType = app.SourceType,
            GitRepositoryFullName = app.GitRepository?.FullName,
            InstanceSizeKey = app.InstanceSizeKey,
            // LoadHeaderAsync deliberately does not include Volumes (a collection), so the header's
            // "is there a Data button" question is answered with an existence check rather than by
            // loading the whole list this tab has no other use for.
            HasVolumes = await db.Volumes.AnyAsync(v => v.AppId == app.Id, ct),
            CpuPercent = samples.FirstOrDefault(m => m.Name == "cpu.percent")?.Value,
            MemoryUsed = samples.FirstOrDefault(m => m.Name == "mem.used")?.Value,
            MemoryLimitBytes = app.MemoryLimitBytes,
            CpuLimit = app.CpuLimit,
            DiskLimitBytes = app.DiskLimitBytes,
            DiskUsedBytes = disk.MeasuredBytes,
            DiskCaveat = Harbora.Infrastructure.Tenancy.InstanceDisk.Caveat(disk),
            MeasuredAt = samples.FirstOrDefault()?.Timestamp,
            SelectedMinutes = selectedMinutes
        });
    }

    /// <summary>
    /// This app's persistent storage — the mounted paths, plus the forms that add and remove one.
    /// Moved out of Overview rather than rewritten: the same <c>AddVolume</c>/<c>RemoveVolume</c>
    /// actions, the same antiforgery token, the same route values.
    /// </summary>
    [HttpGet("apps/{id:guid}/volumes")]
    public async Task<IActionResult> Volumes(Guid id, CancellationToken ct)
    {
        var app = await LoadHeaderAsync(id, ct);
        if (app is null) return NotFound();

        var volumes = await db.Volumes.AsNoTracking()
            .Where(v => v.AppId == id)
            .OrderBy(v => v.MountPath)
            .ToListAsync(ct);

        return View(new AppVolumesViewModel
        {
            Id = app.Id, Name = app.Name, Slug = app.Slug, Kind = app.Kind, Status = app.Status,
            CurrentTab = "volumes",
            SourceType = app.SourceType,
            GitRepositoryFullName = app.GitRepository?.FullName,
            InstanceSizeKey = app.InstanceSizeKey,
            // This tab loads the volumes anyway, so the header's "is there a Data button" question is
            // answered from the list already in hand rather than a second existence query (contrast
            // Usage, which never loads the collection and so asks db.Volumes.AnyAsync directly).
            HasVolumes = volumes.Count > 0,
            Volumes = volumes,
        });
    }

    /// <summary>
    /// This app's release history, and the rollback link the deployment list has always offered a
    /// succeeded, inactive entry. Moved out of Overview rather than rewritten: the same windowed
    /// query — <c>OrderByDescending(d =&gt; d.Number).Take(20)</c> — and the same rollback anchors.
    /// </summary>
    [HttpGet("apps/{id:guid}/deployments")]
    public async Task<IActionResult> Deployments(Guid id, CancellationToken ct)
    {
        var app = await LoadHeaderAsync(id, ct);
        if (app is null) return NotFound();

        var deployments = await db.Deployments.AsNoTracking()
            .Where(d => d.AppId == id)
            .OrderByDescending(d => d.Number)
            .Take(20)
            .ToListAsync(ct);

        return View(new AppDeploymentsViewModel
        {
            Id = app.Id, Name = app.Name, Slug = app.Slug, Kind = app.Kind, Status = app.Status,
            CurrentTab = "deployments",
            SourceType = app.SourceType,
            GitRepositoryFullName = app.GitRepository?.FullName,
            InstanceSizeKey = app.InstanceSizeKey,
            HasVolumes = await db.Volumes.AnyAsync(v => v.AppId == app.Id, ct),
            Deployments = deployments,
            ActiveDeploymentId = app.ActiveDeploymentId,
        });
    }
}
