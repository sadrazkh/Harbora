using System.Net;
using FluentAssertions;
using Harbora.Domain.Authorization;
using Harbora.Domain.Common;
using Harbora.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// What a support session is refused, proven by asking for it.
///
/// <para>
/// Every one of these routes still renders its button while the session runs, on purpose. A hidden
/// control is not a control: what is under test here is that the POST behind it refuses, that the
/// refusal looks like every other refusal in this panel, that nothing happened, and that the attempt
/// was written down. Each block is paired with the same request made WITHOUT a support session, so a
/// passing test cannot mean "this route is simply broken".
/// </para>
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class SupportRestrictionHttpTests(HarboraHttpFixture fixture)
{
    private HarboraWebFactory Panel => fixture.Panel;

    private (Guid WorkspaceId, User Customer) GivenCustomer(
        string slug, string email, SystemRole role = SystemRole.Member)
    {
        var workspaceId = Guid.CreateVersion7();
        Panel.Seed(db => db.Workspaces.Add(new Workspace
        {
            Id = workspaceId, Name = slug, Slug = slug, IsDefault = false
        }));
        return (workspaceId, Panel.GivenUser(workspaceId, email, role));
    }

    /// <summary>A browser inside a support session against <paramref name="customerId"/>.</summary>
    private async Task<(HttpClient Client, Guid SessionId)> Impersonating(
        string remoteIp, string adminEmail, Guid workspaceId, Guid customerId)
    {
        var client = await Panel.SignedInAs(remoteIp, adminEmail);
        var path = $"/tenants/{workspaceId}/support/{customerId}";
        var token = await client.AntiforgeryTokenFrom(path);

        var started = await client.PostFormAsync(path, token, ("reason", "Reproducing a reported failure."));
        started.StatusCode.Should().Be(HttpStatusCode.Found);

        var sessionId = Panel.Read(db => db.SupportSessions.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.TargetUserId == customerId && s.EndedAt == null)
            .OrderByDescending(s => s.StartedAt).First().Id);

        return (client, sessionId);
    }

    private void RefusalWasAudited(Guid sessionId, SupportRestrictedAct act)
    {
        var rows = Panel.Read(db => db.AuditLogs.AsNoTracking()
            .Where(a => a.SupportSessionId == sessionId && a.Action == SupportRestrictions.RefusedAction)
            .ToList());

        rows.Should().Contain(r => r.MetadataJson!.Contains(act.ToString()),
            "an attempt that changed nothing is only ever written down here");
        rows.Should().OnlyContain(r => r.SupportAdminUserId != null,
            "the refusal names the administrator behind it, like everything else the session writes");
    }

    // ---- reachable by any customer: tokens, sessions, two-factor ---------------------------------

    [Fact]
    public async Task A_support_session_cannot_mint_an_api_token_and_an_ordinary_customer_still_can()
    {
        Panel.GivenUser(fixture.WorkspaceId, "res-admin1@example.com", SystemRole.Owner);
        var (workspaceId, customer) = GivenCustomer("res-tenant-1", "res-customer1@example.com");

        var (support, sessionId) = await Impersonating(
            "203.0.113.118", "res-admin1@example.com", workspaceId, customer.Id);
        var supportToken = await support.AntiforgeryTokenFrom("/settings");
        var refused = await support.PostFormAsync("/settings/tokens", supportToken, ("name", "support-token"));

        refused.StatusCode.Should().Be(HttpStatusCode.Found);
        refused.RedirectPath().Should().Be("/account/denied",
            "a support session hitting a wall looks like every other wall in this panel");
        Panel.Read(db => db.ApiTokens.Count(t => t.UserId == customer.Id)).Should().Be(0);
        RefusalWasAudited(sessionId, SupportRestrictedAct.ApiToken);

        // The same wall, but it says which wall. "You don't have access" alone would send somebody
        // hunting a permission that is not the problem.
        var denied = await support.GetAsync("/account/denied");
        denied.StatusCode.Should().Be(HttpStatusCode.OK);
        (await denied.Content.ReadAsStringAsync()).Should().Contain("data-support-denied");

        // The control: the same route, the same customer, no support session.
        var theirs = await Panel.SignedInAs("203.0.113.119", "res-customer1@example.com");
        var theirToken = await theirs.AntiforgeryTokenFrom("/settings");
        var allowed = await theirs.PostFormAsync("/settings/tokens", theirToken, ("name", "my-token"));

        allowed.StatusCode.Should().Be(HttpStatusCode.Found);
        allowed.RedirectPath().Should().Be("/Settings");
        Panel.Read(db => db.ApiTokens.Count(t => t.UserId == customer.Id)).Should().Be(1);
    }

    [Fact]
    public async Task A_support_session_cannot_end_the_customers_other_sessions()
    {
        Panel.GivenUser(fixture.WorkspaceId, "res-admin2@example.com", SystemRole.Owner);
        var (workspaceId, customer) = GivenCustomer("res-tenant-2", "res-customer2@example.com");

        // The customer's own browser, somewhere else, signed in and staying signed in.
        await Panel.SignedInAs("203.0.113.120", "res-customer2@example.com");
        var live = Panel.Read(db => db.UserSessions.Count(s => s.UserId == customer.Id && s.RevokedAt == null));
        live.Should().BeGreaterThan(0);

        var (support, sessionId) = await Impersonating(
            "203.0.113.121", "res-admin2@example.com", workspaceId, customer.Id);
        var token = await support.AntiforgeryTokenFrom("/settings");
        var refused = await support.PostFormAsync("/settings/sessions/revoke-others", token);

        refused.StatusCode.Should().Be(HttpStatusCode.Found);
        refused.RedirectPath().Should().Be("/account/denied");
        Panel.Read(db => db.UserSessions.Count(s => s.UserId == customer.Id && s.RevokedAt == null))
            .Should().Be(live + 1, "the customer's own sessions are untouched, and support added one");
        RefusalWasAudited(sessionId, SupportRestrictedAct.Sessions);
    }

    [Fact]
    public async Task A_support_session_cannot_start_enrolling_the_customer_in_two_factor()
    {
        Panel.GivenUser(fixture.WorkspaceId, "res-admin3@example.com", SystemRole.Owner);
        var (workspaceId, customer) = GivenCustomer("res-tenant-3", "res-customer3@example.com");

        var (support, sessionId) = await Impersonating(
            "203.0.113.122", "res-admin3@example.com", workspaceId, customer.Id);
        var token = await support.AntiforgeryTokenFrom("/settings");
        var refused = await support.PostFormAsync("/settings/totp/begin", token);

        refused.StatusCode.Should().Be(HttpStatusCode.Found);
        refused.RedirectPath().Should().Be("/account/denied");
        Panel.Read(db => db.Users.IgnoreQueryFilters().Single(u => u.Id == customer.Id).TotpSecretEncrypted)
            .Should().BeNull("no draft secret was written for an account whose owner did not ask for one");
        RefusalWasAudited(sessionId, SupportRestrictedAct.TwoFactor);
    }

    // ---- reachable only when the impersonated account is itself an operator ----------------------
    //
    // Password resets, email verification and wallet movements live behind tenants.manage, which no
    // workspace role grants. Support impersonating another platform operator is the case where those
    // routes are reachable at all — and it is exactly the case the refusals have to hold in.

    [Fact]
    public async Task A_support_session_inside_an_operators_account_cannot_replace_anybody_s_password()
    {
        Panel.GivenUser(fixture.WorkspaceId, "res-admin4@example.com", SystemRole.Owner);
        var operatorAccount = Panel.GivenUser(fixture.WorkspaceId, "res-operator4@example.com", SystemRole.Admin);
        var victim = Panel.GivenUser(fixture.WorkspaceId, "res-victim4@example.com", SystemRole.Member);
        var hashBefore = Panel.Read(db => db.Users.IgnoreQueryFilters().Single(u => u.Id == victim.Id).PasswordHash);

        var (support, sessionId) = await Impersonating(
            "203.0.113.123", "res-admin4@example.com", fixture.WorkspaceId, operatorAccount.Id);
        // The token comes off /settings rather than /users: an antiforgery pair is per-browser, not
        // per-page, and /users answers 500 for everybody on master today — an untranslatable GroupBy
        // in its Index, unrelated to any of this and raised separately.
        var token = await support.AntiforgeryTokenFrom("/settings");
        var refused = await support.PostFormAsync(
            $"/users/{victim.Id}/password", token, ("password", "a-brand-new-password"));

        refused.StatusCode.Should().Be(HttpStatusCode.Found);
        refused.RedirectPath().Should().Be("/account/denied");
        Panel.Read(db => db.Users.IgnoreQueryFilters().Single(u => u.Id == victim.Id).PasswordHash)
            .Should().Be(hashBefore);
        RefusalWasAudited(sessionId, SupportRestrictedAct.Password);
    }

    [Fact]
    public async Task A_support_session_inside_an_operators_account_cannot_mark_an_email_verified()
    {
        Panel.GivenUser(fixture.WorkspaceId, "res-admin5@example.com", SystemRole.Owner);
        var operatorAccount = Panel.GivenUser(fixture.WorkspaceId, "res-operator5@example.com", SystemRole.Admin);

        var unverified = Panel.GivenUser(fixture.WorkspaceId, "res-unverified5@example.com", SystemRole.Member);
        Panel.Seed(db => db.Users.IgnoreQueryFilters().Single(u => u.Id == unverified.Id).EmailVerifiedAt = null);

        var (support, sessionId) = await Impersonating(
            "203.0.113.124", "res-admin5@example.com", fixture.WorkspaceId, operatorAccount.Id);
        var token = await support.AntiforgeryTokenFrom("/settings");
        var refused = await support.PostFormAsync($"/users/{unverified.Id}/email/verify", token);

        refused.StatusCode.Should().Be(HttpStatusCode.Found);
        refused.RedirectPath().Should().Be("/account/denied");
        Panel.Read(db => db.Users.IgnoreQueryFilters().Single(u => u.Id == unverified.Id).EmailVerifiedAt)
            .Should().BeNull();
        RefusalWasAudited(sessionId, SupportRestrictedAct.Email);
    }

    [Fact]
    public async Task A_support_session_inside_an_operators_account_cannot_credit_a_wallet()
    {
        Panel.GivenUser(fixture.WorkspaceId, "res-admin6@example.com", SystemRole.Owner);
        var operatorAccount = Panel.GivenUser(fixture.WorkspaceId, "res-operator6@example.com", SystemRole.Admin);
        var (payingWorkspace, _) = GivenCustomer("res-tenant-6", "res-customer6@example.com");

        var (support, sessionId) = await Impersonating(
            "203.0.113.125", "res-admin6@example.com", fixture.WorkspaceId, operatorAccount.Id);

        // The confirmation page still renders — hiding the form would be the thing this feature is
        // deliberately not doing — and its POST is what refuses.
        var confirm = $"/tenants/{payingWorkspace}/credit";
        var token = await support.AntiforgeryTokenFrom(confirm);
        var refused = await support.PostFormAsync(confirm, token,
            ("creditId", Guid.CreateVersion7().ToString()), ("amount", "250000"), ("note", "a gift"));

        refused.StatusCode.Should().Be(HttpStatusCode.Found);
        refused.RedirectPath().Should().Be("/account/denied");
        Panel.Read(db => db.BillingLedger.IgnoreQueryFilters().Count(e => e.WorkspaceId == payingWorkspace))
            .Should().Be(0, "no money moved");
        RefusalWasAudited(sessionId, SupportRestrictedAct.WalletCredit);
    }

    [Fact]
    public async Task A_support_session_cannot_spend_a_voucher_into_the_customers_wallet()
    {
        Panel.GivenUser(fixture.WorkspaceId, "res-admin7@example.com", SystemRole.Owner);
        var (workspaceId, customer) = GivenCustomer("res-tenant-7", "res-customer7@example.com");

        var (support, sessionId) = await Impersonating(
            "203.0.113.126", "res-admin7@example.com", workspaceId, customer.Id);
        var token = await support.AntiforgeryTokenFrom("/billing");
        var refused = await support.PostFormAsync("/billing/voucher", token, ("code", "HARBORA-TEST"));

        refused.StatusCode.Should().Be(HttpStatusCode.Found);
        refused.RedirectPath().Should().Be("/account/denied");
        RefusalWasAudited(sessionId, SupportRestrictedAct.WalletCredit);
    }

    [Fact]
    public async Task A_support_session_cannot_connect_an_external_sign_in_to_the_customers_account()
    {
        // Not on the plan's original list, because external sign-in did not exist when that list was
        // written — it landed from a parallel sub-project while this one was in flight. Linking one
        // mints a durable, self-owned way into somebody else's account, which is the same thing an
        // API token is and longer-lived, so it is refused by the same rule.
        Panel.GivenUser(fixture.WorkspaceId, "res-admin9@example.com", SystemRole.Owner);
        var (workspaceId, customer) = GivenCustomer("res-tenant-9", "res-customer9@example.com");

        var (support, sessionId) = await Impersonating(
            "203.0.113.128", "res-admin9@example.com", workspaceId, customer.Id);
        var token = await support.AntiforgeryTokenFrom("/settings");

        var link = await support.PostFormAsync(
            "/account/external/google/start", token, ("link", "true"), ("returnUrl", "/settings"));
        var unlink = await support.PostFormAsync("/account/external/google/unlink", token);

        link.RedirectPath().Should().Be("/account/denied");
        unlink.RedirectPath().Should().Be("/account/denied");
        Panel.Read(db => db.ExternalLogins.IgnoreQueryFilters().Count(e => e.UserId == customer.Id))
            .Should().Be(0);
        RefusalWasAudited(sessionId, SupportRestrictedAct.ExternalLogin);
    }

    [Fact]
    public async Task Everything_the_list_does_not_name_is_still_allowed()
    {
        // The list is deliberately short. A support session that could not read a page or press an
        // ordinary button would be a support session nobody could use to reproduce anything.
        Panel.GivenUser(fixture.WorkspaceId, "res-admin8@example.com", SystemRole.Owner);
        var (workspaceId, customer) = GivenCustomer("res-tenant-8", "res-customer8@example.com");

        var (support, _) = await Impersonating(
            "203.0.113.127", "res-admin8@example.com", workspaceId, customer.Id);

        foreach (var path in new[] { "/", "/apps", "/settings", "/billing" })
            (await support.GetAsync(path)).StatusCode.Should().Be(HttpStatusCode.OK, $"{path} stays open");

        // And an ordinary write goes through: the panel-mode switch is an act on the customer's own
        // account that nothing about support has any business refusing.
        var token = await support.AntiforgeryTokenFrom("/settings");
        var switched = await support.PostFormAsync("/account/panel-mode", token, ("mode", "Simple"));

        switched.StatusCode.Should().Be(HttpStatusCode.Found);
        switched.RedirectPath().Should().NotBe("/account/denied");

        // Do-not-change item 23, and the one place it is not a matter of taste: Simple mode folds
        // advanced material away and never removes it, and the banner is not advanced material at
        // all. A customer who prefers the simpler panel is not a customer who opted out of being
        // told somebody else is signed in as them.
        var simple = await support.GetAsync("/");
        simple.StatusCode.Should().Be(HttpStatusCode.OK);
        (await simple.Content.ReadAsStringAsync()).Should().Contain("data-support-session",
            "the banner is operational information, and operational information is never folded");
    }
}
