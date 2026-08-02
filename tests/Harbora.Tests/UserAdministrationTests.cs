using FluentAssertions;
using Harbora.Domain.Authorization;
using Harbora.Domain.Common;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Who may do what to whom.
///
/// Every rule here exists because breaking it produces a platform nobody can administer. The
/// codebase already ships a break-glass <c>harbora make-owner</c> command "for when the only owner
/// was deleted or demoted" — that command is the scar. This class is the thing that should have
/// made it unnecessary.
/// </summary>
public class UserAdministrationTests
{
    private static readonly Guid Actor = Guid.CreateVersion7();
    private static readonly Guid Target = Guid.CreateVersion7();

    private static UserAdminContext Context(
        SystemRole actorRole = SystemRole.Owner,
        SystemRole targetRole = SystemRole.Member,
        bool sameUser = false,
        int activeOwners = 2,
        bool targetActive = true) =>
        new(Actor, actorRole, sameUser ? Actor : Target, targetRole, targetActive, activeOwners);

    // ---- role changes ----

    [Fact]
    public void An_owner_may_change_an_ordinary_role()
    {
        UserAdministration.RefuseRoleChange(Context(), SystemRole.Operator).Should().BeNull();
    }

    [Fact]
    public void Nobody_may_change_their_own_role()
    {
        // Both directions are dangerous. Downwards an owner locks themselves out of the platform;
        // upwards any admin quietly becomes an owner, which is privilege escalation with a form.
        UserAdministration.RefuseRoleChange(Context(sameUser: true), SystemRole.Viewer)
            .Should().NotBeNull();

        UserAdministration.RefuseRoleChange(Context(actorRole: SystemRole.Admin, sameUser: true), SystemRole.Owner)
            .Should().NotBeNull();
    }

    [Fact]
    public void Only_an_owner_may_grant_ownership()
    {
        // An admin who can appoint owners is an owner, with extra steps.
        UserAdministration.RefuseRoleChange(Context(actorRole: SystemRole.Admin), SystemRole.Owner)
            .Should().NotBeNull();

        UserAdministration.RefuseRoleChange(Context(actorRole: SystemRole.Owner), SystemRole.Owner)
            .Should().BeNull();
    }

    [Fact]
    public void An_admin_may_not_modify_an_owner()
    {
        UserAdministration.RefuseRoleChange(
            Context(actorRole: SystemRole.Admin, targetRole: SystemRole.Owner), SystemRole.Member)
            .Should().NotBeNull();
    }

    [Fact]
    public void The_last_owner_may_not_be_demoted()
    {
        // The rule the break-glass command exists because of.
        UserAdministration.RefuseRoleChange(
            Context(targetRole: SystemRole.Owner, activeOwners: 1), SystemRole.Admin)
            .Should().NotBeNull();
    }

    [Fact]
    public void An_owner_may_be_demoted_while_another_owner_remains()
    {
        UserAdministration.RefuseRoleChange(
            Context(targetRole: SystemRole.Owner, activeOwners: 2), SystemRole.Admin)
            .Should().BeNull();
    }

    [Fact]
    public void Someone_without_the_capability_may_not_manage_users_at_all()
    {
        foreach (var role in new[] { SystemRole.Member, SystemRole.Operator, SystemRole.Viewer })
            UserAdministration.RefuseRoleChange(Context(actorRole: role), SystemRole.Viewer)
                .Should().NotBeNull($"a {role} must not administer users");
    }

    // ---- deactivation ----

    [Fact]
    public void An_owner_may_deactivate_an_ordinary_user()
    {
        UserAdministration.RefuseDeactivation(Context()).Should().BeNull();
    }

    [Fact]
    public void Nobody_may_deactivate_themselves()
    {
        // The most direct way to lock yourself out of your own platform.
        UserAdministration.RefuseDeactivation(Context(sameUser: true)).Should().NotBeNull();
    }

    [Fact]
    public void The_last_owner_may_not_be_deactivated()
    {
        UserAdministration.RefuseDeactivation(Context(targetRole: SystemRole.Owner, activeOwners: 1))
            .Should().NotBeNull();
    }

    [Fact]
    public void An_admin_may_not_deactivate_an_owner()
    {
        UserAdministration.RefuseDeactivation(Context(actorRole: SystemRole.Admin, targetRole: SystemRole.Owner))
            .Should().NotBeNull();
    }

    [Fact]
    public void Reactivating_a_suspended_user_is_allowed()
    {
        // Reactivation cannot lock anybody out, so the owner-count rule must not block it — least
        // of all at zero active owners, where restoring one is the only way out from inside.
        UserAdministration.RefuseReactivation(
            Context(targetRole: SystemRole.Owner, activeOwners: 0, targetActive: false))
            .Should().BeNull();
    }

    [Fact]
    public void Restoring_is_not_the_mirror_of_suspending()
    {
        // The asymmetry stated outright, because collapsing the two is the obvious "cleanup" and it
        // silently reintroduces the lockout: suspending yourself is forbidden, restoring is not.
        var self = Context(sameUser: true);

        UserAdministration.RefuseDeactivation(self).Should().NotBeNull();
        UserAdministration.RefuseReactivation(self).Should().BeNull();
    }

    [Fact]
    public void An_already_suspended_owner_is_not_the_last_owner()
    {
        // Somebody who cannot sign in is not holding the platform up. Counting them would refuse a
        // perfectly safe demotion and leave a suspended owner nobody can clear.
        UserAdministration.RefuseRoleChange(
            Context(targetRole: SystemRole.Owner, activeOwners: 1, targetActive: false),
            SystemRole.Member)
            .Should().BeNull();
    }

    // ---- creation ----

    [Fact]
    public void An_admin_may_create_an_ordinary_user()
    {
        UserAdministration.RefuseCreation(SystemRole.Admin, SystemRole.Member).Should().BeNull();
    }

    [Fact]
    public void Only_an_owner_may_create_another_owner()
    {
        UserAdministration.RefuseCreation(SystemRole.Admin, SystemRole.Owner).Should().NotBeNull();
        UserAdministration.RefuseCreation(SystemRole.Owner, SystemRole.Owner).Should().BeNull();
    }

    [Fact]
    public void A_member_may_not_create_users()
    {
        UserAdministration.RefuseCreation(SystemRole.Member, SystemRole.Viewer).Should().NotBeNull();
    }

    // ---- password resets ----

    [Fact]
    public void An_owner_may_reset_an_ordinary_password()
    {
        UserAdministration.RefusePasswordReset(Context()).Should().BeNull();
    }

    [Fact]
    public void An_admin_may_not_reset_an_owners_password()
    {
        // Otherwise "admin" is a role that can take over the owner's account in two clicks.
        UserAdministration.RefusePasswordReset(
            Context(actorRole: SystemRole.Admin, targetRole: SystemRole.Owner))
            .Should().NotBeNull();
    }

    [Fact]
    public void Resetting_your_own_password_is_allowed()
    {
        // Unlike a role change, which must go through somebody else.
        UserAdministration.RefusePasswordReset(Context(sameUser: true)).Should().BeNull();
    }

    [Fact]
    public void A_member_may_not_reset_anyones_password()
    {
        UserAdministration.RefusePasswordReset(Context(actorRole: SystemRole.Member))
            .Should().NotBeNull();
    }

    // ---- the reason is shown to a person ----

    [Fact]
    public void Every_refusal_explains_itself()
    {
        // A blank refusal renders as a form that silently does nothing, which is the failure mode
        // this whole codebase has been removing.
        var refusals = new[]
        {
            UserAdministration.RefuseRoleChange(Context(sameUser: true), SystemRole.Viewer),
            UserAdministration.RefuseRoleChange(Context(targetRole: SystemRole.Owner, activeOwners: 1), SystemRole.Admin),
            UserAdministration.RefuseDeactivation(Context(sameUser: true)),
            UserAdministration.RefuseCreation(SystemRole.Member, SystemRole.Viewer),
        };

        refusals.Should().OnlyContain(r => r != null && r.Length > 10);
    }
}
