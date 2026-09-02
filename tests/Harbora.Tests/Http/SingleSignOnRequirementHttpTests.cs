using System.Net;
using FluentAssertions;
using Harbora.Domain.Common;
using Harbora.Domain.Identity;
using Harbora.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// A workspace can require its members to sign in through single sign-on only — 1.4 in the round-two
/// plan. A password sign-in from a held member is refused by name, never "invalid credentials"; the
/// workspace's own owner and the installation owner are always exempt; turning the setting on with no
/// provider configured is refused by name; and a member with no linked provider is named in the panel
/// before the setting is saved, not discovered by a locked-out sign-in afterward.
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public sealed class SingleSignOnRequirementHttpTests(HarboraHttpFixture fixture)
{
    private HarboraWebFactory Panel => fixture.Panel;

    private const string Provider = ExternalLoginProviders.Google;

    [Fact]
    public async Task A_password_sign_in_from_a_held_member_is_refused_by_name_not_as_invalid_credentials()
    {
        await ConfigureAsync(Provider);
        try
        {
            var (_, ownerClient, team) = await GivenTeamAsync("refused", "198.51.100.230");
            var memberEmail = $"refused-member-{Guid.NewGuid():N}@example.com";
            var member = Panel.GivenUser(fixture.WorkspaceId, memberEmail, SystemRole.Member);
            GivenMemberOf(team.Id, member.Id);

            var toggleToken = await ownerClient.AntiforgeryTokenFrom("/workspaces");
            await ownerClient.PostFormAsync("/workspaces/sso", toggleToken, ("required", "true"));
            Panel.Read(db => db.Workspaces.IgnoreQueryFilters().Single(w => w.Id == team.Id).RequiresSingleSignOn)
                .Should().BeTrue("the toggle should have taken with a provider configured");

            var memberClient = Panel.ClientFrom("198.51.100.231");
            var loginToken = await memberClient.AntiforgeryTokenFrom("/account/login");

            var response = await memberClient.PostFormAsync("/account/login", loginToken,
                ("Email", memberEmail), ("Password", HarboraWebFactory.TestPassword));

            response.StatusCode.Should().Be(HttpStatusCode.OK,
                "a refusal re-renders the login form; it does not redirect the way a wrong password does either");
            var page = await response.Content.ReadAsStringAsync();
            page.Should().Contain("data-sso-login-refused=\"true\"");
            page.Should().Contain($"data-sso-required-workspace=\"{team.Slug}\"",
                "the refusal names the workspace, not just that something is wrong");
            page.Should().Contain($"data-sso-required-providers=\"{Provider}\"",
                "and names which provider to use instead");
            Panel.Read(db => db.UserSessions.Any(s => s.UserId == member.Id)).Should().BeFalse(
                "the correct password must not mint a session for a held member");
        }
        finally
        {
            await DisableAsync(Provider);
        }
    }

    [Fact]
    public async Task The_installation_owner_still_signs_in_with_a_password_despite_belonging_to_a_requiring_workspace()
    {
        await ConfigureAsync(Provider);
        try
        {
            var (_, ownerClient, team) = await GivenTeamAsync("install-owner", "198.51.100.232");
            var installOwnerEmail = $"install-owner-{Guid.NewGuid():N}@example.com";
            var installOwner = Panel.GivenUser(fixture.WorkspaceId, installOwnerEmail, SystemRole.Owner);
            GivenMemberOf(team.Id, installOwner.Id);

            var toggleToken = await ownerClient.AntiforgeryTokenFrom("/workspaces");
            await ownerClient.PostFormAsync("/workspaces/sso", toggleToken, ("required", "true"));

            var client = Panel.ClientFrom("198.51.100.233");
            var loginToken = await client.AntiforgeryTokenFrom("/account/login");

            var response = await client.PostFormAsync("/account/login", loginToken,
                ("Email", installOwnerEmail), ("Password", HarboraWebFactory.TestPassword));

            response.StatusCode.Should().Be(HttpStatusCode.Found,
                "the installation owner is the repair path and must never be locked out by a customer's own setting");
            response.RedirectPath().Should().Be("/");
            Panel.Read(db => db.UserSessions.Any(s => s.UserId == installOwner.Id)).Should().BeTrue();
        }
        finally
        {
            await DisableAsync(Provider);
        }
    }

    [Fact]
    public async Task A_workspace_owner_still_signs_in_with_a_password_for_their_own_requiring_workspace()
    {
        await ConfigureAsync(Provider);
        try
        {
            var (owner, ownerClient, team) = await GivenTeamAsync("workspace-owner", "198.51.100.234");

            var toggleToken = await ownerClient.AntiforgeryTokenFrom("/workspaces");
            await ownerClient.PostFormAsync("/workspaces/sso", toggleToken, ("required", "true"));

            var freshClient = Panel.ClientFrom("198.51.100.235");
            var loginToken = await freshClient.AntiforgeryTokenFrom("/account/login");

            var response = await freshClient.PostFormAsync("/account/login", loginToken,
                ("Email", owner.Email), ("Password", HarboraWebFactory.TestPassword));

            response.StatusCode.Should().Be(HttpStatusCode.Found,
                "a workspace's own owner is exempt from a setting only they (or the installation owner) can turn on");
            response.RedirectPath().Should().Be("/");
        }
        finally
        {
            await DisableAsync(Provider);
        }
    }

    [Fact]
    public async Task Turning_the_requirement_on_with_no_provider_configured_is_refused_by_name()
    {
        // Defensive: this platform-wide setting is shared across the whole HTTP collection, and every
        // other file that touches it resets itself — but this test's own claim depends on it being
        // genuinely off, so it is asserted here rather than assumed.
        await DisableAsync(ExternalLoginProviders.Google);
        await DisableAsync(ExternalLoginProviders.GitHub);
        await DisableAsync(ExternalLoginProviders.Oidc);

        var (_, ownerClient, team) = await GivenTeamAsync("no-provider", "198.51.100.236");

        var before = await Page(ownerClient, "/workspaces");
        before.Should().Contain("data-sso-no-provider=\"true\"",
            "the panel should already say no provider is configured before the toggle is even tried");

        var token = await ownerClient.AntiforgeryTokenFrom("/workspaces");
        var response = await ownerClient.PostFormAsync("/workspaces/sso", token, ("required", "true"));

        response.RedirectPath().Should().Be("/workspaces");
        Panel.Read(db => db.Workspaces.IgnoreQueryFilters().Single(w => w.Id == team.Id).RequiresSingleSignOn)
            .Should().BeFalse("refused, not silently accepted");

        var after = await Page(ownerClient, "/workspaces");
        after.Should().Contain("class=\"alert-danger",
            "the redirect carries an error banner — the CSS class is asserted, not the localized sentence");
        after.Should().Contain("data-sso-required=\"false\"");
    }

    [Fact]
    public async Task Turning_the_requirement_on_succeeds_once_a_provider_is_configured()
    {
        await ConfigureAsync(Provider);
        try
        {
            var (_, ownerClient, team) = await GivenTeamAsync("happy-path", "198.51.100.237");

            var token = await ownerClient.AntiforgeryTokenFrom("/workspaces");
            var response = await ownerClient.PostFormAsync("/workspaces/sso", token, ("required", "true"));

            response.RedirectPath().Should().Be("/workspaces");
            Panel.Read(db => db.Workspaces.IgnoreQueryFilters().Single(w => w.Id == team.Id).RequiresSingleSignOn)
                .Should().BeTrue();

            var page = await Page(ownerClient, "/workspaces");
            page.Should().Contain("data-sso-required=\"true\"");
            page.Should().Contain("data-sso-owner-exempt=\"true\"",
                "the panel that turns this on has to say on the same screen why the owner is not held to it");
        }
        finally
        {
            await DisableAsync(Provider);
        }
    }

    [Fact]
    public async Task A_member_with_no_linked_provider_is_named_in_the_panel_before_the_setting_is_saved()
    {
        var (_, ownerClient, team) = await GivenTeamAsync("unlinked", "198.51.100.238");
        var unlinkedEmail = $"unlinked-member-{Guid.NewGuid():N}@example.com";
        var unlinked = Panel.GivenUser(fixture.WorkspaceId, unlinkedEmail, SystemRole.Member);
        GivenMemberOf(team.Id, unlinked.Id);

        // Not toggled on yet — the panel has to name this member ahead of that decision, not after.
        var page = await Page(ownerClient, "/workspaces");

        page.Should().Contain("data-sso-required=\"false\"");
        page.Should().Contain($"data-sso-unlinked-member=\"{unlinkedEmail}\"",
            "an administrator about to require single sign-on needs to know who has no way in through it yet");
        page.Should().Contain("data-sso-unlinked-count=\"1\"");
    }

    [Fact]
    public async Task A_member_who_has_already_linked_a_provider_is_not_named_as_unlinked()
    {
        var (_, ownerClient, team) = await GivenTeamAsync("linked", "198.51.100.239");
        var linkedEmail = $"linked-member-{Guid.NewGuid():N}@example.com";
        var linked = Panel.GivenUser(fixture.WorkspaceId, linkedEmail, SystemRole.Member);
        GivenMemberOf(team.Id, linked.Id);
        Panel.Seed(db => db.ExternalLogins.Add(new ExternalLogin
        {
            UserId = linked.Id,
            Provider = Provider,
            Subject = "sub-" + Guid.NewGuid().ToString("N"),
            Email = linkedEmail,
            LinkedAt = DateTimeOffset.UtcNow
        }));

        var page = await Page(ownerClient, "/workspaces");

        page.Should().NotContain($"data-sso-unlinked-member=\"{linkedEmail}\"");
    }

    [Fact]
    public async Task An_admin_who_is_not_the_workspace_owner_cannot_toggle_the_requirement()
    {
        await ConfigureAsync(Provider);
        try
        {
            var (_, _, team) = await GivenTeamAsync("non-owner-admin", "198.51.100.240");
            var adminEmail = $"non-owner-admin-{Guid.NewGuid():N}@example.com";
            var admin = Panel.GivenUser(fixture.WorkspaceId, adminEmail, SystemRole.Member);
            Panel.Seed(db => db.WorkspaceMembers.Add(new WorkspaceMember
            {
                WorkspaceId = team.Id, UserId = admin.Id, Role = WorkspaceRole.Admin
            }));
            var adminClient = await Panel.SignedInAs("198.51.100.241", adminEmail);
            // The admin's own session is on their personal workspace, not the team, so switch first —
            // WorkspaceId in the controller comes from the session, not a route value.
            var switchToken = await adminClient.AntiforgeryTokenFrom("/workspaces");
            await adminClient.PostFormAsync("/workspaces/switch", switchToken,
                ("workspaceId", team.Id.ToString()), ("returnUrl", "/workspaces"));

            var token = await adminClient.AntiforgeryTokenFrom("/workspaces");
            var response = await adminClient.PostFormAsync("/workspaces/sso", token, ("required", "true"));

            // Forbid() under cookie authentication redirects to the access-denied page rather than
            // answering a bare 403 — the same shape WorkspacesController's other owner-only actions
            // already take (see ExternalLoginAdminHttpTests for the identical assertion elsewhere).
            response.RedirectPath().Should().Be("/account/denied",
                "only the workspace owner may turn this on — an ordinary admin is not exempt from it");
            Panel.Read(db => db.Workspaces.IgnoreQueryFilters().Single(w => w.Id == team.Id).RequiresSingleSignOn)
                .Should().BeFalse();
        }
        finally
        {
            await DisableAsync(Provider);
        }
    }

    [Fact]
    public async Task The_setting_is_not_offered_for_a_personal_workspace()
    {
        var email = $"personal-{Guid.NewGuid():N}@example.com";
        var user = Panel.GivenUser(fixture.WorkspaceId, email, SystemRole.Member);
        var client = await Panel.SignedInAs("198.51.100.242", email);
        var personal = Panel.Read(db => db.Workspaces.IgnoreQueryFilters()
            .Single(w => w.OwnerUserId == user.Id && w.IsPersonal));
        var switchToken = await client.AntiforgeryTokenFrom("/workspaces");
        await client.PostFormAsync("/workspaces/switch", switchToken,
            ("workspaceId", personal.Id.ToString()), ("returnUrl", "/workspaces"));

        var token = await client.AntiforgeryTokenFrom("/workspaces");
        var response = await client.PostFormAsync("/workspaces/sso", token, ("required", "true"));

        response.RedirectPath().Should().Be("/workspaces");
        Panel.Read(db => db.Workspaces.IgnoreQueryFilters().Single(w => w.Id == personal.Id).RequiresSingleSignOn)
            .Should().BeFalse();

        var page = await Page(client, "/workspaces");
        page.Should().NotContain("data-sso-required=");
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private async Task<(User Owner, HttpClient OwnerClient, Workspace Team)> GivenTeamAsync(string tag, string ip)
    {
        var ownerEmail = $"{tag}-owner-{Guid.NewGuid():N}@example.com";
        var owner = Panel.GivenUser(fixture.WorkspaceId, ownerEmail, SystemRole.Member);
        var client = await Panel.SignedInAs(ip, ownerEmail);
        var token = await client.AntiforgeryTokenFrom("/workspaces");
        var teamName = $"{tag}-team-{Guid.NewGuid():N}";
        await client.PostFormAsync("/workspaces/create", token, ("name", teamName));
        var team = Panel.Read(db => db.Workspaces.IgnoreQueryFilters()
            .Single(w => w.OwnerUserId == owner.Id && !w.IsPersonal && w.Name == teamName));
        return (owner, client, team);
    }

    private void GivenMemberOf(Guid workspaceId, Guid userId) =>
        Panel.Seed(db => db.WorkspaceMembers.Add(new WorkspaceMember
        {
            WorkspaceId = workspaceId, UserId = userId, Role = WorkspaceRole.Member
        }));

    private static async Task<string> Page(HttpClient client, string path)
    {
        var response = await client.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"{path} should render");
        return await response.Content.ReadAsStringAsync();
    }

    private async Task ConfigureAsync(string provider)
    {
        using var scope = Panel.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ExternalLoginSettingsService>()
            .SaveAsync(provider, true, "client-id", "client-secret",
                authority: "https://sso.example.com", displayName: "Company SSO", CancellationToken.None);
    }

    private async Task DisableAsync(string provider)
    {
        using var scope = Panel.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ExternalLoginSettingsService>()
            .SaveAsync(provider, false, "client-id", null, null, null, CancellationToken.None);
    }
}
