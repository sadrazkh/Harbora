using Harbora.Domain.Common;

namespace Harbora.Domain.Authorization;

/// <summary>Where a resource lives, for the purpose of deciding who may touch it.</summary>
/// <param name="ProjectId">Null for a resource created before projects existed.</param>
public readonly record struct ResourcePlacement(Guid? ProjectId, Guid? EnvironmentId);

/// <summary>
/// Whether this member may do this thing, to this resource.
///
/// The split that makes it teachable: the <b>workspace role</b> says what someone may do, and
/// <b>project grants</b> say where. A grant can never add a capability the role does not already
/// carry — the two are intersected — so the worst a mistake in a grant can do is give someone access
/// to a project, never a power they did not have.
///
/// Written as one rule with tests rather than a condition per screen, because access control that
/// lives in twenty places is access control that is wrong in one of them.
/// </summary>
public static class ProjectAccess
{
    /// <summary>
    /// True when the member may exercise <paramref name="capability"/> on a resource in this place.
    ///
    /// <paramref name="scopedToProjects"/> is the switch that changes a member from
    /// workspace-wide — which is what everyone was before this existed, and remains the default — to
    /// reaching only what they have been granted.
    /// </summary>
    public static bool Allows(
        SystemRole workspaceRole,
        bool scopedToProjects,
        IReadOnlyCollection<ProjectGrant> grants,
        ResourcePlacement placement,
        string capability)
    {
        // Nothing a grant can do makes up for a role that does not carry the capability at all.
        if (!RolePermissions.Allows(workspaceRole, capability)) return false;

        // An administrator is not scoped: administering a workspace it can only see half of is not
        // administering it.
        if (workspaceRole is SystemRole.Owner or SystemRole.Admin || !scopedToProjects) return true;

        // A scoped member and a resource that belongs to no project: there is no grant that could
        // cover it, and guessing in favour of access is the wrong way to be wrong.
        if (placement.ProjectId is not { } projectId) return false;

        var applicable = grants.Where(g => g.ProjectId == projectId).ToList();

        // An environment-specific grant beats a whole-project one for that environment: it is the
        // narrower statement, and the reason to write it is usually to say "not production".
        var forEnvironment = placement.EnvironmentId is { } environmentId
            ? applicable.FirstOrDefault(g => g.EnvironmentId == environmentId)
            : null;

        var grant = forEnvironment ?? applicable.FirstOrDefault(g => g.EnvironmentId is null);
        if (grant is null) return false;

        // Intersected, never unioned: a grant chooses where a role reaches, and may narrow it, but
        // cannot hand out anything the role does not already have.
        return RolePermissions.Allows(grant.Role, capability);
    }

    /// <summary>
    /// Whether the member can see this project at all. Used to decide what a list shows, which has
    /// to agree with what the actions allow — a project someone can open and never act on is a
    /// worse experience than one they cannot see.
    /// </summary>
    public static bool CanSee(
        SystemRole workspaceRole, bool scopedToProjects,
        IReadOnlyCollection<ProjectGrant> grants, Guid projectId) =>
        workspaceRole is SystemRole.Owner or SystemRole.Admin
        || !scopedToProjects
        || grants.Any(g => g.ProjectId == projectId);

    /// <summary>
    /// One line describing what a grant means, for the screen that manages them. A permission
    /// nobody can read is a permission nobody audits.
    /// </summary>
    public static string Describe(ProjectGrant grant, string projectName, string? environmentName) =>
        environmentName is null
            ? $"{grant.Role} on all of {projectName}"
            : $"{grant.Role} on {projectName} · {environmentName}";
}
