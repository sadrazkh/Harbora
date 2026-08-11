using System.Net;
using FluentAssertions;
using Harbora.Domain.Common;
using Harbora.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

[Collection(HarboraHttpCollection.Name)]
public sealed class WorkspaceAccountHttpTests(HarboraHttpFixture fixture)
{
    private HarboraWebFactory Panel => fixture.Panel;

    [Fact]
    public async Task Public_registration_creates_a_personal_workspace_wallet_and_owner_membership()
    {
        var email = $"signup-{Guid.NewGuid():N}@example.com";
        var client = Panel.ClientFrom("203.0.113.181");
        var token = await client.AntiforgeryTokenFrom("/account/register");

        var response = await client.PostFormAsync("/account/register", token,
            ("Email", email), ("DisplayName", "New Customer"),
            ("Password", HarboraWebFactory.TestPassword),
            ("ConfirmPassword", HarboraWebFactory.TestPassword));

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.RedirectPath().Should().Be("/account/verify-pending");
        var user = Panel.Read(db => db.Users.IgnoreQueryFilters().Single(u => u.Email == email));
        user.EmailVerifiedAt.Should().BeNull();
        Panel.Read(db => db.UserSessions.Any(s => s.UserId == user.Id)).Should().BeFalse();
        var workspace = Panel.Read(db => db.Workspaces.IgnoreQueryFilters()
            .Single(w => w.OwnerUserId == user.Id && w.IsPersonal));
        Panel.Read(db => db.WorkspaceMembers.IgnoreQueryFilters()
            .Any(m => m.WorkspaceId == workspace.Id && m.UserId == user.Id && m.Role == WorkspaceRole.Admin)).Should().BeTrue();
        Panel.Read(db => db.Wallets.IgnoreQueryFilters().Single(w => w.WorkspaceId == workspace.Id).BalanceMinor).Should().Be(0);
        Panel.Read(db => db.Environments.IgnoreQueryFilters().Any(e => e.WorkspaceId == workspace.Id && e.IsDefault)).Should().BeTrue();
    }

    [Fact]
    public async Task A_regular_user_can_create_and_switch_to_an_additional_team_workspace()
    {
        var email = $"multi-{Guid.NewGuid():N}@example.com";
        var user = Panel.GivenUser(fixture.WorkspaceId, email, SystemRole.Member);
        var client = await Panel.SignedInAs("203.0.113.182", email);
        var token = await client.AntiforgeryTokenFrom("/workspaces");

        var response = await client.PostFormAsync("/workspaces/create", token, ("name", "Acme Shared Team"));

        response.RedirectPath().Should().Be("/workspaces");
        var team = Panel.Read(db => db.Workspaces.IgnoreQueryFilters()
            .Single(w => w.OwnerUserId == user.Id && !w.IsPersonal && w.Name == "Acme Shared Team"));
        Panel.Read(db => db.WorkspaceMembers.IgnoreQueryFilters()
            .Any(m => m.WorkspaceId == team.Id && m.UserId == user.Id && m.Role == WorkspaceRole.Admin)).Should().BeTrue();
        var page = await (await client.GetAsync("/workspaces")).Content.ReadAsStringAsync();
        page.Should().Contain("Acme Shared Team").And.Contain("current");
    }

    [Fact]
    public async Task Login_page_offers_registration()
    {
        var page = await (await Panel.ClientFrom("203.0.113.183").GetAsync("/account/login"))
            .Content.ReadAsStringAsync();
        page.Should().Contain("/account/register");
    }

    [Fact]
    public async Task Unverified_account_cannot_create_a_browser_session()
    {
        var email = $"unverified-{Guid.NewGuid():N}@example.com";
        var user = Panel.GivenUser(fixture.WorkspaceId, email, SystemRole.Member);
        Panel.Seed(db => db.Users.IgnoreQueryFilters().Single(u => u.Id == user.Id).EmailVerifiedAt = null);
        var client = Panel.ClientFrom("203.0.113.187");
        var token = await client.AntiforgeryTokenFrom("/account/login");

        var response = await client.PostFormAsync("/account/login", token,
            ("Email", email), ("Password", HarboraWebFactory.TestPassword));

        response.RedirectPath().Should().Be("/account/verify-pending");
        Panel.Read(db => db.UserSessions.Any(s => s.UserId == user.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task Revoking_the_server_session_invalidates_an_existing_cookie()
    {
        var email = $"revoked-session-{Guid.NewGuid():N}@example.com";
        var user = Panel.GivenUser(fixture.WorkspaceId, email, SystemRole.Member);
        var client = await Panel.SignedInAs("203.0.113.188", email);
        Panel.Seed(db => db.UserSessions.Single(s => s.UserId == user.Id).RevokedAt = DateTimeOffset.UtcNow);

        var response = await client.GetAsync("/");

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.RedirectPath().Should().Be("/account/login");
    }

    [Fact]
    public async Task Removing_a_membership_invalidates_an_existing_cookie_immediately()
    {
        var email = $"removed-{Guid.NewGuid():N}@example.com";
        var user = Panel.GivenUser(fixture.WorkspaceId, email, SystemRole.Member);
        var client = await Panel.SignedInAs("203.0.113.184", email);
        Panel.Seed(db => db.WorkspaceMembers.Remove(db.WorkspaceMembers.IgnoreQueryFilters()
            .Single(m => m.WorkspaceId == fixture.WorkspaceId && m.UserId == user.Id)));

        var response = await client.GetAsync("/");

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.RedirectPath().Should().Be("/account/login");
    }

    [Fact]
    public async Task Workspace_owner_can_scope_a_member_and_grant_one_project()
    {
        var ownerEmail = $"scope-owner-{Guid.NewGuid():N}@example.com";
        var memberEmail = $"scope-member-{Guid.NewGuid():N}@example.com";
        Panel.GivenUser(fixture.WorkspaceId, ownerEmail, SystemRole.Member);
        var member = Panel.GivenUser(fixture.WorkspaceId, memberEmail, SystemRole.Member);
        var client = await Panel.SignedInAs("203.0.113.185", ownerEmail);
        var token = await client.AntiforgeryTokenFrom("/workspaces");
        await client.PostFormAsync("/workspaces/create", token, ("name", "Scoped Team"));
        var team = Panel.Read(db => db.Workspaces.IgnoreQueryFilters()
            .Single(w => w.Name == "Scoped Team" && w.OwnerUser!.Email == ownerEmail));
        Panel.Seed(db => db.WorkspaceMembers.Add(new Harbora.Domain.Identity.WorkspaceMember
        {
            WorkspaceId = team.Id, UserId = member.Id, Role = WorkspaceRole.Member
        }));
        var project = Panel.Read(db => db.Projects.IgnoreQueryFilters().Single(p => p.WorkspaceId == team.Id));

        token = await client.AntiforgeryTokenFrom("/workspaces");
        await client.PostFormAsync($"/workspaces/members/{member.Id}/scope", token, ("scoped", "true"));
        token = await client.AntiforgeryTokenFrom("/workspaces");
        await client.PostFormAsync($"/workspaces/members/{member.Id}/grants", token,
            ("projectId", project.Id.ToString()), ("role", "Member"));

        Panel.Read(db => db.WorkspaceMembers.IgnoreQueryFilters()
            .Single(m => m.WorkspaceId == team.Id && m.UserId == member.Id).ScopedToProjects).Should().BeTrue();
        Panel.Read(db => db.ProjectGrants.IgnoreQueryFilters()
            .Any(g => g.WorkspaceId == team.Id && g.UserId == member.Id && g.ProjectId == project.Id)).Should().BeTrue();
    }

    [Fact]
    public async Task Shared_workspace_ownership_can_be_transferred_to_an_active_member()
    {
        var ownerEmail = $"transfer-owner-{Guid.NewGuid():N}@example.com";
        var nextEmail = $"transfer-next-{Guid.NewGuid():N}@example.com";
        Panel.GivenUser(fixture.WorkspaceId, ownerEmail, SystemRole.Member);
        var nextOwner = Panel.GivenUser(fixture.WorkspaceId, nextEmail, SystemRole.Member);
        var client = await Panel.SignedInAs("203.0.113.186", ownerEmail);
        var token = await client.AntiforgeryTokenFrom("/workspaces");
        await client.PostFormAsync("/workspaces/create", token, ("name", "Transfer Team"));
        var team = Panel.Read(db => db.Workspaces.IgnoreQueryFilters()
            .Single(w => w.Name == "Transfer Team" && w.OwnerUser!.Email == ownerEmail));
        Panel.Seed(db => db.WorkspaceMembers.Add(new Harbora.Domain.Identity.WorkspaceMember
        {
            WorkspaceId = team.Id, UserId = nextOwner.Id, Role = WorkspaceRole.Member
        }));

        token = await client.AntiforgeryTokenFrom("/workspaces");
        var response = await client.PostFormAsync("/workspaces/transfer-ownership", token,
            ("userId", nextOwner.Id.ToString()));

        response.RedirectPath().Should().Be("/workspaces");
        Panel.Read(db => db.Workspaces.IgnoreQueryFilters().Single(w => w.Id == team.Id).OwnerUserId)
            .Should().Be(nextOwner.Id);
        Panel.Read(db => db.WorkspaceMembers.IgnoreQueryFilters()
            .Single(m => m.WorkspaceId == team.Id && m.UserId == nextOwner.Id).Role).Should().Be(WorkspaceRole.Admin);
    }

    [Fact]
    public async Task Owner_can_archive_recover_and_safely_delete_a_team_workspace()
    {
        var email = $"lifecycle-{Guid.NewGuid():N}@example.com";
        var owner = Panel.GivenUser(fixture.WorkspaceId, email, SystemRole.Member);
        var client = await Panel.SignedInAs("203.0.113.189", email);
        var token = await client.AntiforgeryTokenFrom("/workspaces");
        await client.PostFormAsync("/workspaces/create", token, ("name", "Lifecycle Team"));
        var team = Panel.Read(db => db.Workspaces.IgnoreQueryFilters()
            .Single(w => w.OwnerUserId == owner.Id && !w.IsPersonal && w.Name == "Lifecycle Team"));

        token = await client.AntiforgeryTokenFrom("/workspaces");
        var archived = await client.PostFormAsync($"/workspaces/{team.Id}/archive", token,
            ("confirmation", team.Slug));

        archived.RedirectPath().Should().Be("/workspaces");
        var afterArchive = Panel.Read(db => db.Workspaces.IgnoreQueryFilters().Single(w => w.Id == team.Id));
        afterArchive.ArchivedAt.Should().NotBeNull();
        afterArchive.IsSuspended.Should().BeTrue();
        afterArchive.SuspendedReason.Should().Be(SuspensionReason.Archived);

        token = await client.AntiforgeryTokenFrom("/workspaces");
        await client.PostFormAsync($"/workspaces/{team.Id}/recover", token);
        var recovered = Panel.Read(db => db.Workspaces.IgnoreQueryFilters().Single(w => w.Id == team.Id));
        recovered.ArchivedAt.Should().BeNull();
        recovered.IsSuspended.Should().BeFalse();

        token = await client.AntiforgeryTokenFrom("/workspaces");
        await client.PostFormAsync($"/workspaces/{team.Id}/archive", token, ("confirmation", team.Slug));
        Panel.Seed(db => db.Workspaces.IgnoreQueryFilters().Single(w => w.Id == team.Id).ArchivedAt = DateTimeOffset.UtcNow.AddHours(-25));
        token = await client.AntiforgeryTokenFrom("/workspaces");
        await client.PostFormAsync($"/workspaces/{team.Id}/delete", token, ("confirmation", team.Slug));

        Panel.Read(db => db.Workspaces.IgnoreQueryFilters().Single(w => w.Id == team.Id).DeletedAt)
            .Should().NotBeNull();
        // Tombstones deliberately retain membership history, but can no longer be discovered or
        // selected by the former owner.
        var list = await client.GetStringAsync("/workspaces");
        list.Should().NotContain("Lifecycle Team");
        token = await client.AntiforgeryTokenFrom("/workspaces");
        var switchDeleted = await client.PostFormAsync("/workspaces/switch", token,
            ("workspaceId", team.Id.ToString()));
        switchDeleted.RedirectPath().Should().Be("/account/denied");
    }
}
