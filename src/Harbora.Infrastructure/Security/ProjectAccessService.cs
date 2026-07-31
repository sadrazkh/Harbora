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
            new ResourcePlacement(app.ProjectId, app.EnvironmentId), capability);
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
            new ResourcePlacement(service.ProjectId, service.EnvironmentId), capability);
    }

    /// <summary>
    /// The projects the caller may see. Null means "all of them" — the common case, and worth
    /// distinguishing from an empty list so a caller does not filter everything away by accident.
    /// </summary>
    public async Task<IReadOnlyCollection<Guid>?> VisibleProjectIdsAsync(CancellationToken ct)
    {
        var (role, scoped, grants) = await CallerAsync(ct);

        if (role is SystemRole.Owner or SystemRole.Admin || !scoped) return null;

        return grants.Select(g => g.ProjectId).Distinct().ToList();
    }

    private async Task<(SystemRole Role, bool Scoped, IReadOnlyCollection<ProjectGrant> Grants)> CallerAsync(
        CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking()
            .Where(u => u.Id == currentUser.UserId)
            .Select(u => new { u.Role, u.ScopedToProjects })
            .FirstOrDefaultAsync(ct);

        // No user record behind the claim: nothing is granted. Deny rather than fall back to a
        // default role, which is how a deleted account keeps working.
        if (user is null) return (SystemRole.Viewer, true, []);

        // Only loaded when they matter — an unscoped member is the common case and needs no query.
        if (!user.ScopedToProjects) return (user.Role, false, []);

        var grants = await db.ProjectGrants.AsNoTracking()
            .Where(g => g.UserId == currentUser.UserId && g.WorkspaceId == currentUser.WorkspaceId)
            .ToListAsync(ct);

        return (user.Role, true, grants);
    }
}
