using Harbora.Data;
using Harbora.Domain.Authorization;
using Harbora.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Infrastructure.Deployments;

/// <summary>
/// Who besides one named person could approve a deploy to one app (5.2, 2026-09 market-gaps round
/// two). Deliberately not built on <c>ProjectAccessService</c>: that class answers for the ambient
/// <c>ICurrentUser</c>, and this has to answer for every OTHER member of the workspace at once,
/// including from a webhook or a CLI push with no signed-in caller behind it at all. It shares
/// <c>ProjectAccess.Allows</c> — the pure rule both classes are built on — so "who can touch this
/// app" is decided in exactly one place either way.
/// </summary>
public sealed class DeploymentApproverFinder(HarboraDbContext db)
{
    /// <summary>
    /// Every active member of the app's workspace, other than <paramref name="excludingUserId"/>, who
    /// could exercise <paramref name="capability"/> on this exact app right now. Empty when the app
    /// does not exist, or nobody else qualifies.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> EligibleApproversAsync(
        Guid appId, Guid workspaceId, string capability, Guid excludingUserId, CancellationToken ct)
    {
        // IgnoreQueryFilters + explicit WorkspaceId ==: the caller may be a webhook or a background
        // approval-expiry sweep with no ambient tenant at all.
        var app = await db.Apps.AsNoTracking().IgnoreQueryFilters()
            .Where(a => a.Id == appId && a.WorkspaceId == workspaceId)
            .Select(a => new { a.EnvironmentId, ProjectId = (Guid?)a.Environment!.ProjectId })
            .FirstOrDefaultAsync(ct);
        if (app is null) return [];

        var placement = new ResourcePlacement(app.ProjectId, app.EnvironmentId, AppId: appId);

        var members = await db.WorkspaceMembers.AsNoTracking().IgnoreQueryFilters()
            .Where(m => m.WorkspaceId == workspaceId && m.UserId != excludingUserId && m.User!.IsActive)
            .Select(m => new { m.UserId, m.Role, m.ScopedToProjects })
            .ToListAsync(ct);
        if (members.Count == 0) return [];

        var scopedIds = members.Where(m => m.ScopedToProjects).Select(m => m.UserId).ToList();
        var grantsByUser = scopedIds.Count == 0
            ? new Dictionary<Guid, List<ProjectGrant>>()
            : (await db.ProjectGrants.AsNoTracking()
                .Where(g => g.WorkspaceId == workspaceId && scopedIds.Contains(g.UserId))
                .ToListAsync(ct))
              .GroupBy(g => g.UserId).ToDictionary(g => g.Key, g => g.ToList());

        var eligible = new List<Guid>();
        foreach (var member in members)
        {
            var grants = member.ScopedToProjects && grantsByUser.TryGetValue(member.UserId, out var g)
                ? g : (IReadOnlyCollection<ProjectGrant>)[];

            if (ProjectAccess.Allows(MapRole(member.Role), member.ScopedToProjects, grants, placement, capability))
                eligible.Add(member.UserId);
        }

        return eligible;
    }

    private static SystemRole MapRole(WorkspaceRole role) => role switch
    {
        WorkspaceRole.Admin => SystemRole.Admin,
        WorkspaceRole.Member => SystemRole.Member,
        WorkspaceRole.Operator => SystemRole.Operator,
        _ => SystemRole.Viewer
    };
}
