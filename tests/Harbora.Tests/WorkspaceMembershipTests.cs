using FluentAssertions;
using Harbora.Domain.Common;
using Harbora.Infrastructure.Security;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Which workspace somebody signs in to.
///
/// This is the rule behind the failure that made the whole non-admin side of the panel look broken.
/// Creating a user wrote a User row and no WorkspaceMember. Sign-in resolved the workspace with
/// FirstOrDefaultAsync over an empty set, which is Guid.Empty — not null, not an error, an ordinary
/// id — and that went into the claim. Every query then filtered on a workspace that does not exist:
/// empty dashboard, empty app list, empty databases, and anything created stamped with Guid.Empty
/// and owned by nobody.
///
/// Nothing threw. Every page returned 200.
/// </summary>
public class WorkspaceMembershipTests
{
    private static readonly Guid Acme = Guid.CreateVersion7();
    private static readonly Guid Other = Guid.CreateVersion7();

    [Fact]
    public void A_member_signs_in_to_the_workspace_they_belong_to()
    {
        var resolution = WorkspaceMembership.Resolve([Acme], [Acme, Other]);

        resolution.Resolved.Should().BeTrue();
        resolution.WorkspaceId.Should().Be(Acme);
    }

    [Fact]
    public void Nobody_is_ever_signed_in_to_an_empty_workspace_id()
    {
        // The whole point. Every path either names a real workspace or refuses with a reason.
        var cases = new[]
        {
            WorkspaceMembership.Resolve([], []),
            WorkspaceMembership.Resolve([], [Acme]),
            WorkspaceMembership.Resolve([], [Acme, Other]),
            WorkspaceMembership.Resolve([Acme], [Acme])
        };

        cases.Should().NotContain(c => c.WorkspaceId == Guid.Empty);
        cases.Where(c => !c.Resolved).Should().OnlyContain(c => !string.IsNullOrWhiteSpace(c.Reason));
    }

    [Fact]
    public void An_account_with_no_membership_joins_the_only_workspace_there_is()
    {
        // Not a guess: on a single-workspace installation there is exactly one answer. It also
        // repairs every account created before this was fixed, on their next sign-in, without an
        // administrator having to find them first.
        var resolution = WorkspaceMembership.Resolve([], [Acme]);

        resolution.WorkspaceId.Should().Be(Acme);
    }

    [Fact]
    public void With_several_workspaces_and_no_membership_it_refuses_rather_than_guessing()
    {
        // Picking the first would put somebody inside another tenant's data — which is worse than
        // not signing in, and far harder to notice.
        var resolution = WorkspaceMembership.Resolve([], [Acme, Other]);

        resolution.Resolved.Should().BeFalse();
        resolution.Reason.Should().Contain("administrator");
    }

    [Fact]
    public void With_no_workspace_at_all_it_says_setup_is_unfinished()
    {
        var resolution = WorkspaceMembership.Resolve([], []);

        resolution.Resolved.Should().BeFalse();
        resolution.Reason.Should().Contain("setup");
    }

    [Fact]
    public void An_existing_membership_is_never_overruled_by_the_single_workspace_shortcut()
    {
        // The order matters: a real membership wins even when the installation has exactly one
        // workspace, so the shortcut can never move somebody.
        WorkspaceMembership.Resolve([Other], [Other]).WorkspaceId.Should().Be(Other);
    }

    [Theory]
    [InlineData(SystemRole.Owner, WorkspaceRole.Admin)]
    [InlineData(SystemRole.Admin, WorkspaceRole.Admin)]
    [InlineData(SystemRole.Member, WorkspaceRole.Member)]
    [InlineData(SystemRole.Operator, WorkspaceRole.Member)]
    [InlineData(SystemRole.Viewer, WorkspaceRole.Viewer)]
    public void The_membership_mirrors_the_system_role(SystemRole role, WorkspaceRole expected)
    {
        // An owner listed as an ordinary member of their own workspace is a permission bug waiting
        // to be reported as one.
        WorkspaceMembership.For(Acme, Guid.CreateVersion7(), role).Role.Should().Be(expected);
    }

    [Fact]
    public void The_membership_names_the_workspace_and_the_person()
    {
        var user = Guid.CreateVersion7();
        var membership = WorkspaceMembership.For(Acme, user, SystemRole.Member);

        membership.WorkspaceId.Should().Be(Acme);
        membership.UserId.Should().Be(user);
    }
}
