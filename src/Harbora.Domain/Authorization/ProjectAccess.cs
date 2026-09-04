using Harbora.Domain.Common;

namespace Harbora.Domain.Authorization;

/// <summary>Where a resource lives, for the purpose of deciding who may touch it.</summary>
/// <param name="ProjectId">Null for a resource created before projects existed.</param>
/// <param name="AppId">
/// 5.1: the app's own id, when this placement IS an app — set only by
/// <c>ProjectAccessService.CanTouchAppAsync</c>/<c>TouchableAppIdsAsync</c>, never guessed from
/// elsewhere. Lets a grant name one app rather than everything in <paramref name="ProjectId"/>.
/// </param>
/// <param name="ServiceId">The same, for a managed service.</param>
public readonly record struct ResourcePlacement(
    Guid? ProjectId, Guid? EnvironmentId, Guid? AppId = null, Guid? ServiceId = null);

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

        // 5.1: a grant naming this exact app or service beats every coarser one — it is the
        // narrowest possible statement, and unlike the environment narrowing below it applies even
        // when the member holds no project- or environment-wide grant here at all. A member scoped to
        // one app in a project they otherwise cannot see still reaches that one app.
        var forResource = placement.AppId is { } appId
            ? applicable.FirstOrDefault(g => g.AppId == appId)
            : placement.ServiceId is { } serviceId
                ? applicable.FirstOrDefault(g => g.ServiceId == serviceId)
                : null;
        if (forResource is not null) return RolePermissions.Allows(forResource.Role, capability);

        // No grant named this exact resource. What is left is whatever reaches it more broadly — a
        // project- or environment-wide grant — so only those rows are candidates from here on.
        var wholeProject = applicable.Where(g => g.AppId is null && g.ServiceId is null).ToList();

        // An environment-specific grant beats a whole-project one for that environment: it is the
        // narrower statement, and the reason to write it is usually to say "not production".
        var forEnvironment = placement.EnvironmentId is { } environmentId
            ? wholeProject.FirstOrDefault(g => g.EnvironmentId == environmentId)
            : null;

        var grant = forEnvironment ?? wholeProject.FirstOrDefault(g => g.EnvironmentId is null);
        if (grant is null) return false;

        // Intersected, never unioned: a grant chooses where a role reaches, and may narrow it, but
        // cannot hand out anything the role does not already have.
        return RolePermissions.Allows(grant.Role, capability);
    }

    /// <summary>
    /// Whether the member can see this project at all — the project page and everything it lists,
    /// not one named resource inside it. Used to decide what a list shows, which has to agree with
    /// what the actions allow — a project someone can open and never act on is a worse experience
    /// than one they cannot see.
    ///
    /// <para>
    /// 5.1: deliberately excludes an app- or service-scoped grant. Being handed one app in a project
    /// must not open the whole project's page to somebody who was only ever meant to see that one
    /// app — the exact leak "a scoped member who ... can see another team's app names has been shown
    /// something they should not have been" describes. <c>ProjectAccessService.CanSeeAppAsync</c> is
    /// where a resource-scoped grant's own visibility is answered instead.
    /// </para>
    /// </summary>
    public static bool CanSee(
        SystemRole workspaceRole, bool scopedToProjects,
        IReadOnlyCollection<ProjectGrant> grants, Guid projectId) =>
        workspaceRole is SystemRole.Owner or SystemRole.Admin
        || !scopedToProjects
        || grants.Any(g => g.ProjectId == projectId && g.AppId is null && g.ServiceId is null);

    /// <summary>
    /// One line describing what a grant means, for the screen that manages them. A permission
    /// nobody can read is a permission nobody audits.
    /// </summary>
    /// <param name="resourceName">
    /// 5.1: the app or service's own name, when <see cref="ProjectGrant.AppId"/> or
    /// <see cref="ProjectGrant.ServiceId"/> is set. Takes precedence over
    /// <paramref name="environmentName"/> — a resource-scoped grant carries no environment of its
    /// own to describe.
    /// </param>
    public static string Describe(
        ProjectGrant grant, string projectName, string? environmentName, string? resourceName = null) =>
        resourceName is not null
            ? $"{grant.Role} on {projectName} · {resourceName}"
            : environmentName is null
                ? $"{grant.Role} on all of {projectName}"
                : $"{grant.Role} on {projectName} · {environmentName}";
}
