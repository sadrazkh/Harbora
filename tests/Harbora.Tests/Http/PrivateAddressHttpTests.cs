using Harbora.Domain.Apps;
using Harbora.Domain.Authorization;
using Harbora.Domain.Common;
using FluentAssertions;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The private address on the app's Overview tab, over real HTTP.
///
/// The state that matters most is the null one: an app that has not deployed since this shipped
/// must not be shown an address it was never actually given. That is the promise-without-a-feature
/// this project has spent three sub-projects removing.
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class PrivateAddressHttpTests(HarboraHttpFixture fixture)
{
    private HarboraWebFactory Panel => fixture.Panel;

    /// <summary>An app in the fixture's workspace, in whatever private-address state the test needs.</summary>
    private Guid SeedApp(string slug, ServiceKind kind, PrivateAddressOutcome? state, int port = 80)
    {
        var app = new App
        {
            WorkspaceId = fixture.WorkspaceId,
            ServerId = Guid.CreateVersion7(),
            Name = slug,
            Slug = slug,
            Kind = kind,
            ContainerPort = port,
            PrivateAddressState = state,
            SourceType = AppSourceType.PrebuiltImage,
            PrebuiltImage = "ghcr.io/example/seeded:1.0",
            Status = AppStatus.Running
        };
        Panel.Seed(db => db.Apps.Add(app));
        return app.Id;
    }

    [Fact]
    public async Task An_apps_overview_shows_the_name_its_neighbours_can_call_it_by()
    {
        var id = SeedApp("priv-shop", ServiceKind.Web, PrivateAddressOutcome.Registered, port: 8080);
        Panel.GivenUser(fixture.WorkspaceId, "priv-shop@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.210", "priv-shop@example.com");

        var html = await (await client.GetAsync($"/apps/details/{id}")).Content.ReadAsStringAsync();

        html.Should().Contain("http://priv-shop:8080",
            "the app's own ContainerPort, so a hard-coded 80 fails this");
    }

    [Fact]
    public async Task An_app_whose_name_was_taken_is_told_so_rather_than_shown_a_blank()
    {
        var id = SeedApp("priv-taken", ServiceKind.Web, PrivateAddressOutcome.Ambiguous);
        Panel.GivenUser(fixture.WorkspaceId, "priv-taken@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.211", "priv-taken@example.com");

        var html = await (await client.GetAsync($"/apps/details/{id}")).Content.ReadAsStringAsync();

        html.Should().Contain("data-private-address-state=\"ambiguous\"");
        html.Should().NotContain("http://priv-taken:",
            "offering an address that resolves to somebody else's service is worse than offering none");
    }

    [Fact]
    public async Task A_release_task_is_not_offered_a_private_address_at_all()
    {
        var id = SeedApp("priv-migrate", ServiceKind.ReleaseTask, PrivateAddressOutcome.KindDoesNotJoin);
        Panel.GivenUser(fixture.WorkspaceId, "priv-migrate@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.212", "priv-migrate@example.com");

        var html = await (await client.GetAsync($"/apps/details/{id}")).Content.ReadAsStringAsync();

        html.Should().Contain("data-private-address-state=\"no-join\"");
    }

    [Fact]
    public async Task A_compose_stack_is_told_its_services_already_carry_their_own_names()
    {
        var id = SeedApp("priv-stack", ServiceKind.Web, PrivateAddressOutcome.ComposeManaged);
        Panel.GivenUser(fixture.WorkspaceId, "priv-stack@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.214", "priv-stack@example.com");

        var html = await (await client.GetAsync($"/apps/details/{id}")).Content.ReadAsStringAsync();

        html.Should().Contain("data-private-address-state=\"compose\"");
        html.Should().NotContain("http://priv-stack:",
            "the app's own slug is not one of the stack's registered aliases — showing it as if it " +
            "were would be a guess dressed up as an address");
    }

    [Fact]
    public async Task An_app_that_has_not_deployed_since_this_shipped_is_not_shown_an_address_it_does_not_have()
    {
        var id = SeedApp("priv-unknown", ServiceKind.Web, state: null);
        Panel.GivenUser(fixture.WorkspaceId, "priv-unknown@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.213", "priv-unknown@example.com");

        var html = await (await client.GetAsync($"/apps/details/{id}")).Content.ReadAsStringAsync();

        html.Should().Contain("data-private-address-state=\"unknown\"");
        html.Should().NotContain("http://priv-unknown:",
            "the alias is registered at deploy time — showing it before then is the " +
            "promise-without-a-feature this project keeps removing");
    }
}
