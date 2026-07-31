using FluentAssertions;
using Harbora.Domain.Authorization;
using Harbora.Domain.Common;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Who may touch which project.
///
/// Roles were workspace-wide: anyone who could deploy could deploy everything the tenant owned. That
/// makes "let the contractor work on the marketing site" impossible to say without also handing them
/// production.
///
/// The split: the workspace role says <b>what</b> someone may do, project grants say <b>where</b>.
/// The two are intersected and never unioned, so the worst a mistaken grant can do is give someone
/// access to a project — never a power they did not already have.
/// </summary>
public class ProjectAccessTests
{
    private static readonly Guid Shop = Guid.NewGuid();
    private static readonly Guid Marketing = Guid.NewGuid();
    private static readonly Guid Production = Guid.NewGuid();
    private static readonly Guid Staging = Guid.NewGuid();

    private static ProjectGrant Grant(Guid project, SystemRole role = SystemRole.Member, Guid? environment = null) =>
        new() { ProjectId = project, EnvironmentId = environment, Role = role };

    private static ResourcePlacement In(Guid? project, Guid? environment = null) => new(project, environment);

    [Fact]
    public void A_member_who_is_not_scoped_reaches_everything_as_before()
    {
        // The default, and what every existing member is: this change must not quietly take access
        // away from anyone.
        ProjectAccess.Allows(SystemRole.Member, scopedToProjects: false, [], In(Shop), Capabilities.AppsDeploy)
            .Should().BeTrue();
    }

    [Fact]
    public void A_scoped_member_reaches_only_what_they_were_granted()
    {
        // The whole point of the feature.
        var grants = new[] { Grant(Shop) };

        ProjectAccess.Allows(SystemRole.Member, true, grants, In(Shop), Capabilities.AppsDeploy).Should().BeTrue();
        ProjectAccess.Allows(SystemRole.Member, true, grants, In(Marketing), Capabilities.AppsDeploy).Should().BeFalse();
    }

    [Fact]
    public void A_grant_cannot_hand_out_a_power_the_role_does_not_have()
    {
        // Intersected, never unioned. A mistake in a grant must not become privilege escalation —
        // this is the property that makes the feature safe to give to a tenant to administer.
        var grants = new[] { Grant(Shop, SystemRole.Owner) };

        ProjectAccess.Allows(SystemRole.Viewer, true, grants, In(Shop), Capabilities.AppsDeploy)
            .Should().BeFalse("a viewer granted 'owner on shop' is still a viewer");

        ProjectAccess.Allows(SystemRole.Operator, true, grants, In(Shop), Capabilities.AppsDeploy)
            .Should().BeFalse("an operator cannot deploy, wherever they are pointed");
    }

    [Fact]
    public void A_grant_may_narrow_the_role_for_one_project()
    {
        // "You are a developer, but on this project you only restart things."
        var grants = new[] { Grant(Shop, SystemRole.Operator) };

        ProjectAccess.Allows(SystemRole.Member, true, grants, In(Shop), Capabilities.AppsOperate).Should().BeTrue();
        ProjectAccess.Allows(SystemRole.Member, true, grants, In(Shop), Capabilities.AppsDeploy).Should().BeFalse();
    }

    [Fact]
    public void An_environment_grant_is_narrower_than_the_project_it_sits_in()
    {
        // The request behind most of these: deploy staging, look at production.
        var grants = new[]
        {
            Grant(Shop),                                                     // whole project: Member
            Grant(Shop, SystemRole.Viewer, environment: Production)          // except production
        };

        ProjectAccess.Allows(SystemRole.Member, true, grants, In(Shop, Staging), Capabilities.AppsDeploy)
            .Should().BeTrue();
        ProjectAccess.Allows(SystemRole.Member, true, grants, In(Shop, Production), Capabilities.AppsDeploy)
            .Should().BeFalse("production was deliberately narrowed");
    }

    [Fact]
    public void An_environment_with_no_grant_of_its_own_falls_back_to_the_project()
    {
        var grants = new[] { Grant(Shop) };

        ProjectAccess.Allows(SystemRole.Member, true, grants, In(Shop, Staging), Capabilities.AppsDeploy)
            .Should().BeTrue();
    }

    [Fact]
    public void A_grant_for_only_one_environment_does_not_cover_the_others()
    {
        // "Deploy staging" must not read as "deploy everything".
        var grants = new[] { Grant(Shop, SystemRole.Member, environment: Staging) };

        ProjectAccess.Allows(SystemRole.Member, true, grants, In(Shop, Staging), Capabilities.AppsDeploy)
            .Should().BeTrue();
        ProjectAccess.Allows(SystemRole.Member, true, grants, In(Shop, Production), Capabilities.AppsDeploy)
            .Should().BeFalse();
    }

    [Fact]
    public void A_resource_belonging_to_no_project_is_out_of_reach_of_a_scoped_member()
    {
        // Created before projects existed, or never reassigned. There is no grant that could cover
        // it, and guessing in favour of access is the wrong way to be wrong.
        ProjectAccess.Allows(SystemRole.Member, true, [Grant(Shop)], In(null), Capabilities.AppsDeploy)
            .Should().BeFalse();
    }

    [Fact]
    public void An_administrator_is_never_scoped()
    {
        // Administering a workspace you can only see half of is not administering it.
        foreach (var role in new[] { SystemRole.Owner, SystemRole.Admin })
        {
            ProjectAccess.Allows(role, scopedToProjects: true, [], In(Marketing), Capabilities.AppsDeploy)
                .Should().BeTrue($"{role} must not be locked out by an empty grant list");
        }
    }

    [Fact]
    public void A_viewer_is_still_a_viewer_everywhere()
    {
        ProjectAccess.Allows(SystemRole.Viewer, false, [], In(Shop), Capabilities.AppsDeploy).Should().BeFalse();
        ProjectAccess.Allows(SystemRole.Viewer, true, [Grant(Shop)], In(Shop), Capabilities.AppsDeploy).Should().BeFalse();
    }

    [Fact]
    public void What_a_list_shows_agrees_with_what_the_buttons_allow()
    {
        // A project someone can open and never act on is a worse experience than one they cannot see.
        ProjectAccess.CanSee(SystemRole.Member, true, [Grant(Shop)], Shop).Should().BeTrue();
        ProjectAccess.CanSee(SystemRole.Member, true, [Grant(Shop)], Marketing).Should().BeFalse();

        ProjectAccess.CanSee(SystemRole.Member, false, [], Marketing).Should().BeTrue();
        ProjectAccess.CanSee(SystemRole.Admin, true, [], Marketing).Should().BeTrue();
    }

    [Fact]
    public void A_grant_reads_as_a_sentence()
    {
        // A permission nobody can read is a permission nobody audits.
        ProjectAccess.Describe(Grant(Shop), "Shop", null).Should().Be("Member on all of Shop");
        ProjectAccess.Describe(Grant(Shop, SystemRole.Viewer, Production), "Shop", "Production")
            .Should().Be("Viewer on Shop · Production");
    }
}
