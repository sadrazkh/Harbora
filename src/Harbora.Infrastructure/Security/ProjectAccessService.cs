using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Authorization;
using Harbora.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Infrastructure.Security;

/// <summary>
/// Answers "may this person do this to this resource" against the database.
///
/// The rule itself is <see cref="ProjectAccess"/> and is tested on its own; this is the part that
/// knows where a resource sits and what the caller has been granted. It exists so every screen asks
/// the same question the same way — access control that lives in twenty places is access control
/// that is wrong in one of them.
/// </summary>
public sealed class ProjectAccessService(HarboraDbContext db, ICurrentUser currentUser)
{
    /// <summary>Whether the caller may exercise this capability on this app.</summary>
    public async Task<bool> CanTouchAppAsync(Guid appId, string capability, CancellationToken ct)
    {
        var app = await db.Apps.AsNoTracking()
            .Where(a => a.Id == appId && a.WorkspaceId == currentUser.WorkspaceId)
            .Select(a => new { a.EnvironmentId, ProjectId = (Guid?)a.Environment!.ProjectId })
            .FirstOrDefaultAsync(ct);

        // Not theirs, or not there. Both answer the same way, and deliberately: telling someone
        // which of the two it is tells them a resource exists.
        if (app is null) return false;

        var (role, scoped, grants) = await CallerAsync(ct);

        return ProjectAccess.Allows(role, scoped, grants,
            new ResourcePlacement(app.ProjectId, app.EnvironmentId, AppId: appId), capability);
    }

    /// <summary>
    /// Batch form used by resource tables. Besides keeping action visibility aligned with the
    /// endpoint, it avoids turning a page of apps into three authorization queries per row.
    /// </summary>
    public async Task<IReadOnlySet<Guid>> TouchableAppIdsAsync(
        IEnumerable<Guid> appIds, string capability, CancellationToken ct)
    {
        var ids = appIds.Distinct().ToList();
        if (ids.Count == 0) return new HashSet<Guid>();

        var placements = await db.Apps.AsNoTracking()
            .Where(a => ids.Contains(a.Id) && a.WorkspaceId == currentUser.WorkspaceId)
            .Select(a => new
            {
                a.Id,
                a.EnvironmentId,
                ProjectId = (Guid?)a.Environment!.ProjectId
            })
            .ToListAsync(ct);
        var (role, scoped, grants) = await CallerAsync(ct);

        return placements
            .Where(a => ProjectAccess.Allows(role, scoped, grants,
                new ResourcePlacement(a.ProjectId, a.EnvironmentId, AppId: a.Id), capability))
            .Select(a => a.Id)
            .ToHashSet();
    }

    /// <summary>Whether the caller may exercise this capability on this managed database.</summary>
    public async Task<bool> CanTouchServiceAsync(Guid serviceId, string capability, CancellationToken ct)
    {
        var service = await db.ManagedServices.AsNoTracking()
            .Where(s => s.Id == serviceId && s.WorkspaceId == currentUser.WorkspaceId)
            .Select(s => new { s.EnvironmentId, ProjectId = (Guid?)s.Environment!.ProjectId })
            .FirstOrDefaultAsync(ct);

        if (service is null) return false;

        var (role, scoped, grants) = await CallerAsync(ct);

        return ProjectAccess.Allows(role, scoped, grants,
            new ResourcePlacement(service.ProjectId, service.EnvironmentId, ServiceId: serviceId), capability);
    }

    /// <summary>
    /// Whether the caller may act on this backup, judged by what it is a backup <i>of</i>.
    ///
    /// A backup is only as private as the thing it came from: an export of production's database is
    /// production's data whatever list it appears in.
    /// </summary>
    public async Task<bool> CanTouchBackupAsync(Guid backupId, string capability, CancellationToken ct)
    {
        var backup = await db.Backups.AsNoTracking()
            .Where(b => b.Id == backupId && b.WorkspaceId == currentUser.WorkspaceId)
            .Select(b => new { b.Type, b.TargetRef })
            .FirstOrDefaultAsync(ct);

        if (backup is null) return false;

        // A platform-wide snapshot belongs to no project, so only someone unscoped can act on it —
        // which is the right answer: it contains every project.
        if (backup.Type == BackupType.FullPlatform)
            return await AllowsAsync(new ResourcePlacement(null, null), capability, ct);

        if (!Guid.TryParse(backup.TargetRef, out var targetId))
            return await AllowsAsync(new ResourcePlacement(null, null), capability, ct);

        return backup.Type switch
        {
            BackupType.Database or BackupType.Service => await CanTouchServiceAsync(targetId, capability, ct),
            _ => await CanTouchAppAsync(targetId, capability, ct)
        };
    }

    /// <summary>
    /// Whether the caller may act on this route, judged by the app it points at. A route with no app
    /// behind it is workspace-level and only an unscoped member can change it — a rule that reaches
    /// every project should be edited by someone who can see every project.
    /// </summary>
    public async Task<bool> CanTouchRouteAsync(Guid routeId, string capability, CancellationToken ct)
    {
        var route = await db.Routes.AsNoTracking()
            .Where(r => r.Id == routeId && r.WorkspaceId == currentUser.WorkspaceId)
            .Select(r => new { r.AppId })
            .FirstOrDefaultAsync(ct);

        if (route is null) return false;

        return route.AppId is { } appId
            ? await CanTouchAppAsync(appId, capability, ct)
            : await AllowsAsync(new ResourcePlacement(null, null), capability, ct);
    }

    /// <summary>
    /// Whether the caller may <i>look at</i> this app.
    ///
    /// Deliberately not an action capability: a viewer is allowed to read, and gating a page on
    /// "may you operate this" locks them out of something the list is still showing them. Reading
    /// follows the same visibility as the list, so the two always agree.
    /// </summary>
    public async Task<bool> CanSeeAppAsync(Guid appId, CancellationToken ct)
    {
        var projectId = await db.Apps.AsNoTracking()
            .Where(a => a.Id == appId && a.WorkspaceId == currentUser.WorkspaceId)
            .Select(a => (Guid?)a.Environment!.ProjectId)
            .FirstOrDefaultAsync(ct);

        if (!await db.Apps.AsNoTracking().AnyAsync(a => a.Id == appId && a.WorkspaceId == currentUser.WorkspaceId, ct))
            return false;

        // 5.1: a grant naming this app specifically is enough on its own — a contractor scoped to
        // one app must reach it even in a project none of their other grants cover.
        var (role, scoped, grants) = await CallerAsync(ct);
        if (role is SystemRole.Owner or SystemRole.Admin || !scoped) return true;
        if (grants.Any(g => g.AppId == appId)) return true;

        return projectId is { } id && ProjectAccess.CanSee(role, scoped, grants, id);
    }

    /// <summary>The same question for a managed database.</summary>
    public async Task<bool> CanSeeServiceAsync(Guid serviceId, CancellationToken ct)
    {
        var exists = await db.ManagedServices.AsNoTracking()
            .AnyAsync(s => s.Id == serviceId && s.WorkspaceId == currentUser.WorkspaceId, ct);
        if (!exists) return false;

        var projectId = await db.ManagedServices.AsNoTracking()
            .Where(s => s.Id == serviceId && s.WorkspaceId == currentUser.WorkspaceId)
            .Select(s => (Guid?)s.Environment!.ProjectId)
            .FirstOrDefaultAsync(ct);

        var (role, scoped, grants) = await CallerAsync(ct);
        if (role is SystemRole.Owner or SystemRole.Admin || !scoped) return true;
        if (grants.Any(g => g.ServiceId == serviceId)) return true;

        return projectId is { } id && ProjectAccess.CanSee(role, scoped, grants, id);
    }

    /// <summary>The rule, asked about a placement the caller has already worked out.</summary>
    public async Task<bool> AllowsAsync(ResourcePlacement placement, string capability, CancellationToken ct)
    {
        var (role, scoped, grants) = await CallerAsync(ct);
        return ProjectAccess.Allows(role, scoped, grants, placement, capability);
    }

    /// <summary>
    /// The projects the caller may see. Null means "all of them" — the common case, and worth
    /// distinguishing from an empty list so a caller does not filter everything away by accident.
    ///
    /// <para>
    /// 5.1: excludes a project whose only grant is app- or service-scoped. Being handed one app must
    /// not make every other app in that project's own list page visible too — see
    /// <see cref="GrantedAppIdsAsync"/>/<see cref="GrantedServiceIdsAsync"/> for the narrower id set a
    /// list page unions in on top of this one to still show that single named resource.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyCollection<Guid>?> VisibleProjectIdsAsync(CancellationToken ct)
    {
        var (role, scoped, grants) = await CallerAsync(ct);

        if (role is SystemRole.Owner or SystemRole.Admin || !scoped) return null;

        return grants.Where(g => g.AppId is null && g.ServiceId is null)
            .Select(g => g.ProjectId).Distinct().ToList();
    }

    /// <summary>
    /// The apps the caller was granted by name, on top of whatever <see cref="VisibleProjectIdsAsync"/>
    /// already covers project-wide. Empty for an unscoped caller — never consulted for one, since a
    /// list page only asks this after <see cref="VisibleProjectIdsAsync"/> came back non-null.
    /// </summary>
    public async Task<IReadOnlyCollection<Guid>> GrantedAppIdsAsync(CancellationToken ct)
    {
        var (role, scoped, grants) = await CallerAsync(ct);
        if (role is SystemRole.Owner or SystemRole.Admin || !scoped) return [];

        return grants.Where(g => g.AppId is not null).Select(g => g.AppId!.Value).ToList();
    }

    /// <summary>The same, for managed services.</summary>
    public async Task<IReadOnlyCollection<Guid>> GrantedServiceIdsAsync(CancellationToken ct)
    {
        var (role, scoped, grants) = await CallerAsync(ct);
        if (role is SystemRole.Owner or SystemRole.Admin || !scoped) return [];

        return grants.Where(g => g.ServiceId is not null).Select(g => g.ServiceId!.Value).ToList();
    }

    private async Task<(SystemRole Role, bool Scoped, IReadOnlyCollection<ProjectGrant> Grants)> CallerAsync(
        CancellationToken ct)
    {
        var membership = await db.WorkspaceMembers.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.UserId == currentUser.UserId && m.WorkspaceId == currentUser.WorkspaceId)
            .Select(m => new { m.Role, m.ScopedToProjects })
            .FirstOrDefaultAsync(ct);

        // No user record behind the claim: nothing is granted. Deny rather than fall back to a
        // default role, which is how a deleted account keeps working.
        if (membership is null) return (SystemRole.Viewer, true, []);

        var role = membership.Role switch
        {
            WorkspaceRole.Admin => SystemRole.Admin,
            WorkspaceRole.Member => SystemRole.Member,
            WorkspaceRole.Operator => SystemRole.Operator,
            _ => SystemRole.Viewer
        };

        // Only loaded when they matter — an unscoped member is the common case and needs no query.
        if (!membership.ScopedToProjects) return (role, false, []);

        var grants = await db.ProjectGrants.AsNoTracking()
            .Where(g => g.UserId == currentUser.UserId && g.WorkspaceId == currentUser.WorkspaceId)
            .ToListAsync(ct);

        return (role, true, grants);
    }
}
