using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Projects;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Harbora.Infrastructure.Projects;

/// <summary>
/// Deletes a project and everything placed inside it — the confirmed cascade that
/// <c>ProjectsController.Delete</c>'s <c>DeleteBehavior.Restrict</c> guard deliberately refuses to do
/// by itself (see that method's own remarks). A plain, unconfirmed delete still refuses exactly as it
/// did before this existed; this is reached only once someone has typed the project's name back,
/// which is what the confirm screen (<see cref="PlanAsync"/>) exists to ask for.
///
/// <para>
/// Nothing here talks to a container or a volume directly. Every app goes through
/// <see cref="IAppOperationsService.DeleteAsync"/> and every database through
/// <see cref="IManagedServiceEngine.RemoveAsync"/> — the same two places a single-item delete already
/// goes through from <c>AppsController</c> and <c>DatabasesController</c>. A project delete is many
/// calls to those, not a second, parallel way of tearing a workload down that could skip a step they
/// do not skip (routes, host ports, the proxy re-apply, the preview-environment cleanup).
/// </para>
/// </summary>
public sealed class ProjectDeletionService(
    HarboraDbContext db,
    IAppOperationsService appOps,
    IManagedServiceEngine serviceEngine,
    ILogger<ProjectDeletionService> logger)
{
    /// <summary>
    /// Everything a delete of this project would destroy, or null if there is no such project in this
    /// workspace. Read by the confirm screen; <see cref="DeleteAsync"/> below builds the identical
    /// plan the same way, rather than trusting whatever the browser posted back.
    /// </summary>
    public async Task<ProjectRemovalPlan?> PlanAsync(Guid workspaceId, Guid projectId, CancellationToken ct)
    {
        var project = await db.Projects.Include(p => p.Environments)
            .FirstOrDefaultAsync(p => p.Id == projectId && p.WorkspaceId == workspaceId, ct);
        return project is null ? null : await BuildPlanAsync(project, ct);
    }

    private async Task<ProjectRemovalPlan> BuildPlanAsync(Project project, CancellationToken ct)
    {
        var environmentIds = project.Environments.Select(e => e.Id).ToList();
        var environmentNames = project.Environments.ToDictionary(e => e.Id, e => e.Name);

        // Projected to an anonymous type and named afterwards, in memory: EF cannot translate a
        // dictionary lookup into SQL, and the project only ever has a handful of environments.
        var appRows = await db.Apps.Where(a => environmentIds.Contains(a.EnvironmentId))
            .OrderBy(a => a.Name)
            .Select(a => new { a.Id, a.Name, a.EnvironmentId })
            .ToListAsync(ct);
        var apps = appRows
            .Select(a => new ProjectRemovalItem(a.Id, a.Name, environmentNames.GetValueOrDefault(a.EnvironmentId, "")))
            .ToList();

        var serviceRows = await db.ManagedServices.Where(s => environmentIds.Contains(s.EnvironmentId))
            .OrderBy(s => s.Name)
            .Select(s => new { s.Id, s.Name, s.EnvironmentId })
            .ToListAsync(ct);
        var databases = serviceRows
            .Select(s => new ProjectRemovalItem(s.Id, s.Name, environmentNames.GetValueOrDefault(s.EnvironmentId, "")))
            .ToList();

        // Cascades automatically once the owning app is gone (App.Domains is DeleteBehavior.Cascade
        // in HarboraDbContext) — named here so the confirm screen can say so, not because anything
        // extra has to be done to remove them.
        var appIds = apps.Select(a => a.Id).ToList();
        var domainHosts = appIds.Count == 0
            ? []
            : await db.Domains.Where(d => appIds.Contains(d.AppId))
                .OrderBy(d => d.Host).Select(d => d.Host).ToListAsync(ct);

        // Also cascades with its app (FunctionDefinition.AppId is DeleteBehavior.Cascade). Counted
        // separately because a scheduled function is not visible anywhere else in this plan — it is
        // not its own app, just a row hanging off a function-host app that is already listed.
        var scheduledFunctionCount = appIds.Count == 0
            ? 0
            : await db.FunctionDefinitions
                .Where(f => appIds.Contains(f.AppId) && f.Trigger == Domain.Functions.FunctionTrigger.Cron)
                .CountAsync(ct);

        return new ProjectRemovalPlan(project.Id, project.Name, apps, databases, domainHosts, scheduledFunctionCount);
    }

    /// <summary>
    /// Deletes every app and database <see cref="PlanAsync"/> would have named, then the now-empty
    /// environments and the project itself — but only once a re-read of the database confirms nothing
    /// is left. One container that resists removal must not stop the rest of the project from going,
    /// and must not stop the items that did succeed from being reported as gone — so each item is
    /// attempted independently, and what actually happened is decided afterwards by asking the
    /// database again, not by trusting that no exception means success. This is the same rule
    /// <c>PreviewEnvironmentService.RemoveAsync</c> already follows for a single preview.
    /// </summary>
    public async Task<ProjectRemovalOutcome> DeleteAsync(Guid workspaceId, Guid projectId, CancellationToken ct)
    {
        // Caller (ProjectsController.Delete) has already loaded and 404'd on this project — this
        // fetch is not a second authorization check, it is what turns "the plan" into an entity this
        // method can hand to db.Projects.Remove once everything inside it is confirmed gone.
        var project = await db.Projects.Include(p => p.Environments)
            .FirstAsync(p => p.Id == projectId && p.WorkspaceId == workspaceId, ct);

        var plan = await BuildPlanAsync(project, ct);

        foreach (var app in plan.Apps)
        {
            try { await appOps.DeleteAsync(app.Id, removeVolumes: true, ct); }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Project {ProjectId} delete: app {AppId} ({AppName}) was not removed.",
                    projectId, app.Id, app.Name);
            }
        }

        foreach (var svc in plan.Databases)
        {
            try { await serviceEngine.RemoveAsync(svc.Id, deleteData: true, ct); }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Project {ProjectId} delete: database {ServiceId} ({ServiceName}) was not removed.",
                    projectId, svc.Id, svc.Name);
            }
        }

        // Checked, not assumed. A caught exception above already names one kind of failure; this also
        // catches the quieter kind, where a delete returns without throwing and without actually
        // removing the row — the exact shape this platform keeps finding (see AppOperationsService's
        // and PreviewEnvironmentService's own remarks on the same point).
        var environmentIds = project.Environments.Select(e => e.Id).ToList();
        var remainingApps = await db.Apps.Where(a => environmentIds.Contains(a.EnvironmentId))
            .OrderBy(a => a.Name).Select(a => a.Name).ToListAsync(ct);
        var remainingDatabases = await db.ManagedServices.Where(s => environmentIds.Contains(s.EnvironmentId))
            .OrderBy(s => s.Name).Select(s => s.Name).ToListAsync(ct);

        if (remainingApps.Count > 0 || remainingDatabases.Count > 0)
        {
            logger.LogWarning(
                "Project {ProjectId} delete left {Apps} app(s) and {Databases} database(s) behind.",
                projectId, remainingApps.Count, remainingDatabases.Count);
            return new ProjectRemovalOutcome(false, project.Name, remainingApps, remainingDatabases);
        }

        // Nothing left anywhere in the project. Dropping the project row cascades its now-empty
        // environments (Project.Environments is DeleteBehavior.Cascade in HarboraDbContext) — nothing
        // points at them any more, so the Restrict guard on App/ManagedService.EnvironmentId has
        // nothing left to refuse.
        db.Projects.Remove(project);
        await db.SaveChangesAsync(ct);

        return new ProjectRemovalOutcome(true, project.Name, [], []);
    }
}
