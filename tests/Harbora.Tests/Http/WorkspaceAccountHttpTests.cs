using System.Net;
using FluentAssertions;
using Harbora.Domain.Common;
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
        var user = Panel.Read(db => db.Users.IgnoreQueryFilters().Single(u => u.Email == email));
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
}
