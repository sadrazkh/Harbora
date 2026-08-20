using System.Net;
using FluentAssertions;
using Harbora.Domain.Common;
using Harbora.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Support impersonation as a browser experiences it: the console starts it, the customer's every
/// page carries the banner, the hour is enforced against the row, and the button gives the
/// administrator their own account back.
///
/// <para>
/// All of it through real requests, because none of it is a method's decision. The claims are set by
/// the cookie middleware, the expiry is checked by a middleware, the banner is rendered by the
/// layout, and the refusal for a workspace owner comes from the capability policy — a controller
/// test would prove none of the four.
/// </para>
///
/// <para>
/// The banner is asserted through its <c>data-support-session</c> attribute rather than through any
/// sentence on it. This panel renders Persian by default, so a test that read the words would be a
/// test of a translation.
/// </para>
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class SupportImpersonationHttpTests(HarboraHttpFixture fixture)
{
    private HarboraWebFactory Panel => fixture.Panel;

    /// <summary>A customer workspace with one member in it — what support gets called about.</summary>
    private (Guid WorkspaceId, User Customer) GivenCustomer(string slug, string email)
    {
        var workspaceId = Guid.CreateVersion7();
        Panel.Seed(db => db.Workspaces.Add(new Workspace
        {
            Id = workspaceId, Name = slug, Slug = slug, IsDefault = false
        }));

        var customer = Panel.GivenUser(workspaceId, email, SystemRole.Member);
        return (workspaceId, customer);
    }

    private async Task<HttpClient> GivenSupportSession(
        string remoteIp, string adminEmail, Guid workspaceId, Guid customerId,
        string reason = "Reproducing the failing deploy the customer reported.")
    {
        var client = await Panel.SignedInAs(remoteIp, adminEmail);
        var path = $"/tenants/{workspaceId}/support/{customerId}";
        var token = await client.AntiforgeryTokenFrom(path);

        var started = await client.PostFormAsync(path, token, ("reason", reason));
        started.StatusCode.Should().Be(HttpStatusCode.Found);
        started.RedirectPath().Should().Be("/");

        return client;
    }

    private SupportSession? LatestSessionFor(Guid workspaceId) =>
        Panel.Read(db => db.SupportSessions.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.TargetWorkspaceId == workspaceId)
            .OrderByDescending(s => s.StartedAt)
            .FirstOrDefault());

    [Fact]
    public async Task A_platform_administrator_signs_in_as_a_customer_and_every_page_carries_the_banner()
    {
        Panel.GivenUser(fixture.WorkspaceId, "sup-admin1@example.com", SystemRole.Owner);
        var (workspaceId, customer) = GivenCustomer("sup-tenant-1", "sup-customer1@example.com");

        var client = await GivenSupportSession("203.0.113.60", "sup-admin1@example.com", workspaceId, customer.Id);

        var session = LatestSessionFor(workspaceId);
        session.Should().NotBeNull();
        session!.TargetUserId.Should().Be(customer.Id);
        session.Reason.Should().Be("Reproducing the failing deploy the customer reported.");
        session.EndedAt.Should().BeNull();
        session.ExpiresAt.Should().Be(session.StartedAt + SupportAccess.Lifetime);

        // Two different pages, because "on every page" is the promise and one page is not a pattern.
        foreach (var path in new[] { "/", "/apps" })
        {
            var page = await client.GetAsync(path);
            page.StatusCode.Should().Be(HttpStatusCode.OK);
            (await page.Content.ReadAsStringAsync()).Should().Contain(
                $"data-support-session=\"{session.Id}\"",
                $"{path} must say out loud that support is signed in as this customer");
        }
    }

    [Fact]
    public async Task An_ordinary_signed_in_customer_sees_no_banner()
    {
        // The other half of the assertion above: the attribute means something only if it is absent
        // the rest of the time.
        var (workspaceId, _) = GivenCustomer("sup-tenant-2", "sup-customer2@example.com");
        _ = workspaceId;
        var client = await Panel.SignedInAs("203.0.113.61", "sup-customer2@example.com");

        var page = await client.GetAsync("/");

        page.StatusCode.Should().Be(HttpStatusCode.OK);
        (await page.Content.ReadAsStringAsync()).Should().NotContain("data-support-session");
    }

    [Fact]
    public async Task Starting_a_session_without_a_reason_is_refused_and_nothing_is_opened()
    {
        Panel.GivenUser(fixture.WorkspaceId, "sup-admin3@example.com", SystemRole.Owner);
        var (workspaceId, customer) = GivenCustomer("sup-tenant-3", "sup-customer3@example.com");

        var client = await Panel.SignedInAs("203.0.113.62", "sup-admin3@example.com");
        var path = $"/tenants/{workspaceId}/support/{customer.Id}";
        var token = await client.AntiforgeryTokenFrom(path);

        var response = await client.PostFormAsync(path, token, ("reason", "   "));

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.RedirectPath().Should().Be(path, "a refusal comes back to the form that was refused");
        LatestSessionFor(workspaceId).Should().BeNull();
    }

    [Fact]
    public async Task A_workspace_owner_cannot_start_a_support_session()
    {
        // A workspace Admin is the most powerful thing a customer can be, and impersonation is not
        // among tenants.manage's grants at any workspace role.
        var (workspaceId, customer) = GivenCustomer("sup-tenant-4", "sup-customer4@example.com");
        var client = await Panel.SignedInAs("203.0.113.63", "sup-customer4@example.com");

        var page = await client.GetAsync($"/tenants/{workspaceId}/support/{customer.Id}");
        var post = await client.PostFormWithoutTokenAsync(
            $"/tenants/{workspaceId}/support/{customer.Id}", ("reason", "let me in"));

        page.StatusCode.Should().Be(HttpStatusCode.Found);
        page.RedirectPath().Should().Be("/account/denied");
        post.StatusCode.Should().Be(HttpStatusCode.Found);
        post.RedirectPath().Should().Be("/account/denied");
        LatestSessionFor(workspaceId).Should().BeNull();
    }

    [Fact]
    public async Task The_opening_of_a_session_is_audited_with_both_ids()
    {
        var admin = Panel.GivenUser(fixture.WorkspaceId, "sup-admin5@example.com", SystemRole.Owner);
        var (workspaceId, customer) = GivenCustomer("sup-tenant-5", "sup-customer5@example.com");

        await GivenSupportSession("203.0.113.64", "sup-admin5@example.com", workspaceId, customer.Id);
        var session = LatestSessionFor(workspaceId)!;

        var row = Panel.Read(db => db.AuditLogs.AsNoTracking()
            .Single(a => a.SupportSessionId == session.Id && a.Action == "support.session.started"));

        row.UserId.Should().Be(customer.Id, "the request really did run as the customer");
        row.SupportAdminUserId.Should().Be(admin.Id, "and this is who was behind it");
        row.MetadataJson.Should().Contain("sup-admin5@example.com");
    }

    [Fact]
    public async Task The_session_dies_at_its_expiry_even_though_the_cookie_is_still_good()
    {
        Panel.GivenUser(fixture.WorkspaceId, "sup-admin6@example.com", SystemRole.Owner);
        var (workspaceId, customer) = GivenCustomer("sup-tenant-6", "sup-customer6@example.com");

        var client = await GivenSupportSession("203.0.113.65", "sup-admin6@example.com", workspaceId, customer.Id);
        var session = LatestSessionFor(workspaceId)!;

        // Nothing is done to the cookie: it is untouched, unexpired and perfectly valid. Only the row
        // moves into the past. If the hour lived in the cookie, this request would sail through.
        Panel.Seed(db =>
        {
            var row = db.SupportSessions.IgnoreQueryFilters().Single(s => s.Id == session.Id);
            row.StartedAt = row.StartedAt - SupportAccess.Lifetime - TimeSpan.FromMinutes(1);
            row.ExpiresAt = row.StartedAt + SupportAccess.Lifetime;
        });

        var response = await client.GetAsync("/");

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.RedirectPath().Should().Be("/account/login");

        var after = Panel.Read(db => db.SupportSessions.IgnoreQueryFilters().AsNoTracking()
            .Single(s => s.Id == session.Id));
        after.EndedAt.Should().NotBeNull("the request that found it expired closed it");
        after.EndedBy.Should().Be(SupportSessionEnding.Expired);
    }

    [Fact]
    public async Task The_button_on_the_banner_ends_the_session_and_returns_the_administrator_to_their_own_account()
    {
        var admin = Panel.GivenUser(fixture.WorkspaceId, "sup-admin7@example.com", SystemRole.Owner);
        var (workspaceId, customer) = GivenCustomer("sup-tenant-7", "sup-customer7@example.com");

        var client = await GivenSupportSession("203.0.113.66", "sup-admin7@example.com", workspaceId, customer.Id);
        var session = LatestSessionFor(workspaceId)!;

        // The token comes off the customer's own dashboard, which is where the banner's form is
        // rendered — the same journey the person pressing it makes.
        var token = await client.AntiforgeryTokenFrom("/");
        var ended = await client.PostFormAsync("/support/end", token);

        ended.StatusCode.Should().Be(HttpStatusCode.Found);
        ended.RedirectPath().Should().Be("/tenants");

        var after = Panel.Read(db => db.SupportSessions.IgnoreQueryFilters().AsNoTracking()
            .Single(s => s.Id == session.Id));
        after.EndedAt.Should().NotBeNull();
        after.EndedBy.Should().Be(SupportSessionEnding.EndedByOperator);

        // Their own account back, not the login form: a way out that costs a re-login is a way out
        // people put off taking.
        var tenants = await client.GetAsync("/tenants");
        tenants.StatusCode.Should().Be(HttpStatusCode.OK, "the administrator is themselves again");
        var body = await tenants.Content.ReadAsStringAsync();
        body.Should().NotContain("data-support-session", "and nobody is being impersonated any more");

        Panel.Read(db => db.AuditLogs.AsNoTracking()
            .Count(a => a.SupportSessionId == session.Id
                        && a.Action == "support.session.ended"
                        && a.SupportAdminUserId == admin.Id))
            .Should().Be(1);
    }

    [Fact]
    public async Task A_second_session_for_the_same_administrator_closes_the_first()
    {
        // Two live sessions would leave the banner's button ending whichever the cookie named and
        // the other one running with no browser attached to notice.
        Panel.GivenUser(fixture.WorkspaceId, "sup-admin8@example.com", SystemRole.Owner);
        var (workspaceId, first) = GivenCustomer("sup-tenant-8", "sup-customer8a@example.com");
        var second = Panel.GivenUser(workspaceId, "sup-customer8b@example.com", SystemRole.Member);

        await GivenSupportSession("203.0.113.67", "sup-admin8@example.com", workspaceId, first.Id);

        // Signing in as the administrator again: the support cookie is the customer's, so the second
        // start is made the way the first was, from the console.
        await GivenSupportSession("203.0.113.68", "sup-admin8@example.com", workspaceId, second.Id);

        var sessions = Panel.Read(db => db.SupportSessions.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.TargetWorkspaceId == workspaceId)
            .OrderBy(s => s.StartedAt).ToList());

        sessions.Should().HaveCount(2);
        sessions[0].EndedAt.Should().NotBeNull("the first was closed when the second opened");
        sessions[1].EndedAt.Should().BeNull();
    }
}
