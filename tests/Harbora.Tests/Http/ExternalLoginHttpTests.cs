using System.Net;
using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Domain.Common;
using Harbora.Domain.Identity;
using Harbora.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Signing in with Google, GitHub or an operator's own OpenID Connect provider — every rule that
/// decides whether an external identity becomes a signed-in person, driven through real requests.
///
/// <para>
/// The provider itself is substituted at the external cookie scheme (see
/// <see cref="TestExternalAuthHandler"/>, which also records what that substitution puts out of
/// reach). Everything downstream of it — routing, antiforgery, the rate limiters, the callback's own
/// reading of the principal, the database, Razor — is the shipped code.
/// </para>
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public sealed class ExternalLoginHttpTests(HarboraHttpFixture fixture)
{
    private HarboraWebFactory Panel => fixture.Panel;

    private static string Subject() => "sub-" + Guid.NewGuid().ToString("N");
    private static string Address(string tag) => $"{tag}-{Guid.NewGuid():N}@example.com";

    // ---- what an unknown identity mirrors -------------------------------------------------------

    /// <summary>
    /// The fact the auto-provisioning branch is built on, pinned so it cannot change underneath it.
    ///
    /// <para>
    /// There is no <c>AllowRegistration</c> setting anywhere in this codebase; the register action's
    /// own guards are the only truth about whether this platform lets strangers in, and they let a
    /// registration through with no invitation. That is why an external identity nobody has an
    /// account for becomes an account rather than "ask your workspace owner for an invitation". If
    /// this test ever goes red, <c>AccountController.ProvisionFromExternalAsync</c> is the thing to
    /// revisit — the invitation refusal is the honest answer the moment registration closes.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Registration_here_is_open_which_is_the_behaviour_an_unknown_external_identity_mirrors()
    {
        var email = Address("open-registration");
        var client = Panel.ClientFrom("203.0.113.61");
        var token = await client.AntiforgeryTokenFrom("/account/register");

        var response = await client.PostFormAsync("/account/register", token,
            ("Email", email), ("DisplayName", "A Stranger"),
            ("Password", HarboraWebFactory.TestPassword),
            ("ConfirmPassword", HarboraWebFactory.TestPassword));

        response.StatusCode.Should().Be(HttpStatusCode.Found,
            "the register action carries no invitation-required guard, so registration is open");
        response.RedirectPath().Should().Be("/account/verify-pending",
            "an open registration still has to prove the address before it is a way in");
        Panel.Read(db => db.Users.IgnoreQueryFilters().Any(u => u.Email == email)).Should().BeTrue();
    }

    // ---- which buttons exist --------------------------------------------------------------------

    [Fact]
    public async Task The_sign_in_page_offers_a_provider_only_once_an_operator_has_configured_it()
    {
        var client = Panel.ClientFrom("203.0.113.62");

        var before = await Page(client, "/account/login");
        before.Should().NotContain("data-external-provider=\"google\"",
            "nothing is offered until somebody configures it");

        await ConfigureAsync(ExternalLoginProviders.Google);
        try
        {
            var after = await Page(client, "/account/login");
            after.Should().Contain("data-external-provider=\"google\"");
            after.Should().Contain("/account/external/google/start");
            after.Should().NotContain("data-external-provider=\"github\"",
                "a provider nobody configured stays absent even while another is offered");
        }
        finally
        {
            await DisableAsync(ExternalLoginProviders.Google);
        }

        var afterwards = await Page(client, "/account/login");
        afterwards.Should().NotContain("data-external-provider=\"google\"",
            "switching a provider off takes its button away without restarting the panel");
    }

    /// <summary>
    /// The absence of a button is a rendering decision; this is the one that refuses. Uses the
    /// generic provider, which no test in this class ever configures.
    /// </summary>
    [Fact]
    public async Task A_provider_nobody_configured_cannot_be_challenged()
    {
        var client = Panel.ClientFrom("203.0.113.63");
        var token = await client.AntiforgeryTokenFrom("/account/login");

        var response = await client.PostFormAsync("/account/external/oidc/start", token);

        response.RedirectPath().Should().Be("/account/login",
            "an unconfigured provider refuses rather than redirecting somebody to a broken consent screen");
    }

    /// <summary>
    /// The failure mode the placeholder credentials in <c>ExternalAuthenticationRegistration</c>
    /// exist to prevent, named so it cannot come back quietly.
    ///
    /// <para>
    /// <c>UseAuthentication</c> initialises every remote handler on every request, and initialising
    /// one validates its options — so three providers registered with empty client ids would throw
    /// on the way to <i>every</i> page, not just the sign-in one. This panel has all three registered
    /// and none of them configured, which is the shipped state of every install.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Three_unconfigured_providers_do_not_stop_the_panel_serving_ordinary_pages()
    {
        var client = Panel.ClientFrom("203.0.113.85");

        (await client.GetAsync("/account/login")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/healthz")).StatusCode.Should().Be(HttpStatusCode.OK);
        // Their callback paths answer too, rather than throwing: nothing correlates, so the remote
        // failure handler sends the person back to the sign-in page saying so.
        var callback = await client.GetAsync("/signin-google");
        callback.StatusCode.Should().Be(HttpStatusCode.Found);
        callback.RedirectPath().Should().Be("/account/login");
    }

    [Fact]
    public async Task A_provider_that_is_not_one_of_ours_is_not_a_route()
    {
        var client = Panel.ClientFrom("203.0.113.64");
        var token = await client.AntiforgeryTokenFrom("/account/login");

        var response = await client.PostFormAsync("/account/external/facebook/start", token);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---- an identity nobody has an account for --------------------------------------------------

    [Fact]
    public async Task An_unknown_identity_a_provider_has_verified_becomes_an_account_and_signs_in()
    {
        var email = Address("provisioned");
        var subject = Subject();
        var client = Panel.ClientFrom("203.0.113.65");

        var response = await client.ExternalCallbackAsync(
            ExternalLoginProviders.Google, subject, email, emailVerified: true, displayName: "New Person");

        response.RedirectPath().Should().Be("/");
        var user = Panel.Read(db => db.Users.IgnoreQueryFilters().Single(u => u.Email == email));
        user.EmailVerifiedAt.Should().NotBeNull(
            "a provider that says it proved the address has proven it at least as well as our own emailed link");
        user.PasswordHash.Should().BeEmpty("nobody set a password, and none was invented");
        Panel.Read(db => db.ExternalLogins.IgnoreQueryFilters()
            .Any(l => l.UserId == user.Id && l.Provider == ExternalLoginProviders.Google && l.Subject == subject))
            .Should().BeTrue();
        Panel.Read(db => db.Workspaces.IgnoreQueryFilters().Any(w => w.OwnerUserId == user.Id && w.IsPersonal))
            .Should().BeTrue("the account gets the same personal workspace registering gives");
        Panel.Read(db => db.UserSessions.Any(s => s.UserId == user.Id)).Should().BeTrue();
    }

    [Fact]
    public async Task An_unknown_identity_the_provider_has_not_verified_waits_for_the_same_email_link_registering_waits_for()
    {
        var email = Address("unverified-provider");
        var client = Panel.ClientFrom("203.0.113.66");

        var response = await client.ExternalCallbackAsync(
            ExternalLoginProviders.GitHub, Subject(), email, emailVerified: false);

        response.RedirectPath().Should().Be("/account/verify-pending",
            "an unverified claim is not evidence, so it meets exactly what a public registration meets");
        var user = Panel.Read(db => db.Users.IgnoreQueryFilters().Single(u => u.Email == email));
        user.EmailVerifiedAt.Should().BeNull();
        Panel.Read(db => db.UserSessions.Any(s => s.UserId == user.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task An_identity_with_no_address_is_refused_rather_than_given_an_invented_one()
    {
        var client = Panel.ClientFrom("203.0.113.67");
        var before = Panel.Read(db => db.Users.IgnoreQueryFilters().Count());

        var response = await client.ExternalCallbackAsync(ExternalLoginProviders.GitHub, Subject(), email: null);

        response.RedirectPath().Should().Be("/account/login");
        Panel.Read(db => db.Users.IgnoreQueryFilters().Count()).Should().Be(before,
            "a provider that shared no address cannot produce an account");
    }

    [Fact]
    public async Task An_identity_with_no_subject_is_refused_because_the_subject_is_what_the_row_is_keyed_on()
    {
        var client = Panel.ClientFrom("203.0.113.68");
        var email = Address("no-subject");

        var response = await client.ExternalCallbackAsync(ExternalLoginProviders.Google, subject: null, email: email);

        response.RedirectPath().Should().Be("/account/login");
        Panel.Read(db => db.Users.IgnoreQueryFilters().Any(u => u.Email == email)).Should().BeFalse();
    }

    // ---- the match that must not become a link --------------------------------------------------

    [Fact]
    public async Task An_address_that_already_has_an_account_is_never_linked_by_the_match_alone()
    {
        var email = Address("already-here");
        var user = Panel.GivenUser(fixture.WorkspaceId, email, SystemRole.Member);
        var client = Panel.ClientFrom("203.0.113.69");

        var response = await client.ExternalCallbackAsync(
            ExternalLoginProviders.Google, Subject(), email, emailVerified: true);

        response.RedirectPath().Should().Be("/account/external/confirm",
            "a matching address buys the offer to prove the password, never the link itself");
        Panel.Read(db => db.ExternalLogins.IgnoreQueryFilters().Any(l => l.UserId == user.Id))
            .Should().BeFalse("nothing is linked before the password is proven");
        Panel.Read(db => db.UserSessions.Any(s => s.UserId == user.Id))
            .Should().BeFalse("and nobody is signed in either");
    }

    [Fact]
    public async Task The_confirm_page_names_the_account_and_asks_for_its_password()
    {
        var email = Address("confirm-page");
        Panel.GivenUser(fixture.WorkspaceId, email, SystemRole.Member);
        var client = Panel.ClientFrom("203.0.113.70");
        await client.ExternalCallbackAsync(ExternalLoginProviders.Google, Subject(), email);

        var page = await Page(client, "/account/external/confirm");

        page.Should().Contain("data-external-confirm=\"google\"");
        page.Should().Contain(email);
    }

    [Fact]
    public async Task Proving_the_password_is_what_writes_the_link_and_signs_in()
    {
        var email = Address("prove-password");
        var user = Panel.GivenUser(fixture.WorkspaceId, email, SystemRole.Member);
        var subject = Subject();
        var client = Panel.ClientFrom("203.0.113.71");
        await client.ExternalCallbackAsync(ExternalLoginProviders.Google, subject, email);
        var token = await client.AntiforgeryTokenFrom("/account/external/confirm");

        var response = await client.PostFormAsync("/account/external/confirm", token,
            ("password", HarboraWebFactory.TestPassword));

        response.RedirectPath().Should().Be("/");
        Panel.Read(db => db.ExternalLogins.IgnoreQueryFilters().Any(
            l => l.UserId == user.Id && l.Provider == ExternalLoginProviders.Google && l.Subject == subject))
            .Should().BeTrue();
        Panel.Read(db => db.UserSessions.Any(s => s.UserId == user.Id)).Should().BeTrue();
    }

    [Fact]
    public async Task A_wrong_password_links_nothing_and_signs_nobody_in()
    {
        var email = Address("wrong-password");
        var user = Panel.GivenUser(fixture.WorkspaceId, email, SystemRole.Member);
        var client = Panel.ClientFrom("203.0.113.72");
        await client.ExternalCallbackAsync(ExternalLoginProviders.Google, Subject(), email);
        var token = await client.AntiforgeryTokenFrom("/account/external/confirm");

        var response = await client.PostFormAsync("/account/external/confirm", token,
            ("password", "not-the-password"));

        response.StatusCode.Should().Be(HttpStatusCode.OK, "a refusal re-renders the form, it does not redirect");
        Panel.Read(db => db.ExternalLogins.IgnoreQueryFilters().Any(l => l.UserId == user.Id)).Should().BeFalse();
        Panel.Read(db => db.UserSessions.Any(s => s.UserId == user.Id)).Should().BeFalse();
    }

    /// <summary>
    /// The offer survives the second factor, and is only spent on the far side of it — a correct
    /// password alone must not connect a provider to an account that also wanted a code.
    /// </summary>
    [Fact]
    public async Task An_account_with_two_factor_still_meets_the_code_prompt_before_anything_is_linked()
    {
        var email = Address("confirm-2fa");
        var user = Panel.GivenUser(fixture.WorkspaceId, email, SystemRole.Member);
        GivenTwoFactor(user.Id);
        var client = Panel.ClientFrom("203.0.113.73");
        await client.ExternalCallbackAsync(ExternalLoginProviders.Google, Subject(), email);
        var token = await client.AntiforgeryTokenFrom("/account/external/confirm");

        var response = await client.PostFormAsync("/account/external/confirm", token,
            ("password", HarboraWebFactory.TestPassword));

        response.RedirectPath().Should().Be("/account/totp");
        Panel.Read(db => db.ExternalLogins.IgnoreQueryFilters().Any(l => l.UserId == user.Id))
            .Should().BeFalse("the link waits for the code, like the session does");
        Panel.Read(db => db.UserSessions.Any(s => s.UserId == user.Id)).Should().BeFalse();
    }

    // ---- signing in with a link that already exists ---------------------------------------------

    [Fact]
    public async Task A_linked_identity_signs_in_without_a_password()
    {
        var email = Address("linked");
        var user = Panel.GivenUser(fixture.WorkspaceId, email, SystemRole.Member);
        var subject = Subject();
        GivenLink(user.Id, ExternalLoginProviders.GitHub, subject, email);
        var client = Panel.ClientFrom("203.0.113.74");

        var response = await client.ExternalCallbackAsync(ExternalLoginProviders.GitHub, subject, email);

        response.RedirectPath().Should().Be("/");
        Panel.Read(db => db.UserSessions.Any(s => s.UserId == user.Id)).Should().BeTrue();
    }

    /// <summary>
    /// The acceptance the owner asked for by name. Local two-factor is this panel's own demand; how
    /// well a provider authenticated somebody is not something this panel is told or can check.
    /// </summary>
    [Fact]
    public async Task Local_two_factor_still_fires_after_an_external_sign_in()
    {
        var email = Address("external-2fa");
        var user = Panel.GivenUser(fixture.WorkspaceId, email, SystemRole.Member);
        GivenTwoFactor(user.Id);
        var subject = Subject();
        GivenLink(user.Id, ExternalLoginProviders.Google, subject, email);
        var client = Panel.ClientFrom("203.0.113.75");

        var response = await client.ExternalCallbackAsync(ExternalLoginProviders.Google, subject, email);

        response.RedirectPath().Should().Be("/account/totp",
            "the provider's own second factor, if it had one, is not ours to assume");
        Panel.Read(db => db.UserSessions.Any(s => s.UserId == user.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task A_linked_identity_on_a_deactivated_account_is_refused()
    {
        var email = Address("deactivated");
        var user = Panel.GivenUser(fixture.WorkspaceId, email, SystemRole.Member);
        Panel.Seed(db => db.Users.IgnoreQueryFilters().Single(u => u.Id == user.Id).IsActive = false);
        var subject = Subject();
        GivenLink(user.Id, ExternalLoginProviders.Google, subject, email);
        var client = Panel.ClientFrom("203.0.113.76");

        var response = await client.ExternalCallbackAsync(ExternalLoginProviders.Google, subject, email);

        response.RedirectPath().Should().Be("/account/login");
        Panel.Read(db => db.UserSessions.Any(s => s.UserId == user.Id)).Should().BeFalse();
    }

    // ---- linking from account settings ----------------------------------------------------------

    [Fact]
    public async Task A_signed_in_person_can_connect_a_provider_from_their_own_settings()
    {
        var email = Address("link-from-settings");
        var user = Panel.GivenUser(fixture.WorkspaceId, email, SystemRole.Member);
        var client = await Panel.SignedInAs("203.0.113.77", email);
        var subject = Subject();

        var response = await client.ExternalCallbackAsync(
            ExternalLoginProviders.Google, subject, email, displayName: "Work Google", link: true);

        response.RedirectPath().Should().Be("/settings");
        Panel.Read(db => db.ExternalLogins.IgnoreQueryFilters().Any(
            l => l.UserId == user.Id && l.Provider == ExternalLoginProviders.Google && l.Subject == subject))
            .Should().BeTrue();
    }

    [Fact]
    public async Task A_provider_account_already_connected_to_somebody_else_is_refused()
    {
        var owner = Panel.GivenUser(fixture.WorkspaceId, Address("first-owner"), SystemRole.Member);
        var subject = Subject();
        GivenLink(owner.Id, ExternalLoginProviders.Google, subject, owner.Email);

        var second = Address("second-comer");
        Panel.GivenUser(fixture.WorkspaceId, second, SystemRole.Member);
        var client = await Panel.SignedInAs("203.0.113.78", second);

        var response = await client.ExternalCallbackAsync(
            ExternalLoginProviders.Google, subject, owner.Email, link: true);

        response.RedirectPath().Should().Be("/settings");
        Panel.Read(db => db.ExternalLogins.IgnoreQueryFilters()
            .Count(l => l.Provider == ExternalLoginProviders.Google && l.Subject == subject))
            .Should().Be(1, "the row stays with the account that connected it first");
    }

    [Fact]
    public async Task The_same_provider_and_subject_cannot_be_stored_twice()
    {
        var user = Panel.GivenUser(fixture.WorkspaceId, Address("dedupe"), SystemRole.Member);
        var subject = Subject();
        GivenLink(user.Id, ExternalLoginProviders.Google, subject, user.Email);
        var client = await Panel.SignedInAs("203.0.113.79", user.Email);

        await client.ExternalCallbackAsync(ExternalLoginProviders.Google, subject, user.Email, link: true);

        Panel.Read(db => db.ExternalLogins.IgnoreQueryFilters()
            .Count(l => l.Provider == ExternalLoginProviders.Google && l.Subject == subject)).Should().Be(1);
    }

    // ---- unlinking -------------------------------------------------------------------------------

    [Fact]
    public async Task Unlinking_refuses_when_it_would_leave_the_account_with_no_way_in()
    {
        var email = Address("only-way-in");
        var subject = Subject();
        var client = Panel.ClientFrom("203.0.113.80");
        // Provisioned by the provider, so it has never had a password — the account this refusal
        // exists for.
        await client.ExternalCallbackAsync(ExternalLoginProviders.Google, subject, email, emailVerified: true);
        var user = Panel.Read(db => db.Users.IgnoreQueryFilters().Single(u => u.Email == email));
        var token = await client.AntiforgeryTokenFrom("/settings");

        var response = await client.PostFormAsync("/account/external/google/unlink", token);

        response.RedirectPath().Should().Be("/settings");
        Panel.Read(db => db.ExternalLogins.IgnoreQueryFilters().Any(l => l.UserId == user.Id))
            .Should().BeTrue("the last door is not one this panel lets somebody brick up");
    }

    [Fact]
    public async Task Unlinking_is_allowed_while_a_password_remains()
    {
        var email = Address("has-password");
        var user = Panel.GivenUser(fixture.WorkspaceId, email, SystemRole.Member);
        GivenLink(user.Id, ExternalLoginProviders.Google, Subject(), email);
        var client = await Panel.SignedInAs("203.0.113.81", email);
        var token = await client.AntiforgeryTokenFrom("/settings");

        var response = await client.PostFormAsync("/account/external/google/unlink", token);

        response.RedirectPath().Should().Be("/settings");
        Panel.Read(db => db.ExternalLogins.IgnoreQueryFilters().Any(l => l.UserId == user.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task Unlinking_is_allowed_while_another_provider_remains()
    {
        var email = Address("two-providers");
        var subject = Subject();
        var client = Panel.ClientFrom("203.0.113.82");
        await client.ExternalCallbackAsync(ExternalLoginProviders.Google, subject, email, emailVerified: true);
        var user = Panel.Read(db => db.Users.IgnoreQueryFilters().Single(u => u.Email == email));
        GivenLink(user.Id, ExternalLoginProviders.GitHub, Subject(), email);
        var token = await client.AntiforgeryTokenFrom("/settings");

        var response = await client.PostFormAsync("/account/external/google/unlink", token);

        response.RedirectPath().Should().Be("/settings");
        Panel.Read(db => db.ExternalLogins.IgnoreQueryFilters()
            .Count(l => l.UserId == user.Id)).Should().Be(1, "the GitHub link is still a way in, so Google may go");
    }

    [Fact]
    public async Task Unlinking_needs_somebody_signed_in()
    {
        var client = Panel.ClientFrom("203.0.113.83");
        var token = await client.AntiforgeryTokenFrom("/account/login");

        var response = await client.PostFormAsync("/account/external/google/unlink", token);

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.RedirectPath().Should().Be("/account/login");
    }

    // ---- the settings page ------------------------------------------------------------------------

    [Fact]
    public async Task The_account_page_shows_a_connected_provider_and_offers_to_disconnect_it()
    {
        var email = Address("settings-shows");
        var user = Panel.GivenUser(fixture.WorkspaceId, email, SystemRole.Member);
        GivenLink(user.Id, ExternalLoginProviders.GitHub, Subject(), "work@example.com");
        var client = await Panel.SignedInAs("203.0.113.84", email);

        var page = await Page(client, "/settings");

        page.Should().Contain("data-external-link=\"github\"");
        page.Should().Contain("/account/external/github/unlink");
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private static async Task<string> Page(HttpClient client, string path)
    {
        var response = await client.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"{path} should render");
        return await response.Content.ReadAsStringAsync();
    }

    private void GivenLink(Guid userId, string provider, string subject, string email) =>
        Panel.Seed(db => db.ExternalLogins.Add(new ExternalLogin
        {
            UserId = userId,
            Provider = provider,
            Subject = subject,
            Email = email,
            LinkedAt = DateTimeOffset.UtcNow
        }));

    private void GivenTwoFactor(Guid userId)
    {
        var secret = Harbora.Infrastructure.Security.Totp.GenerateSecret();
        var protector = Panel.Resolve<ISecretProtector>();
        Panel.Seed(db =>
        {
            var user = db.Users.IgnoreQueryFilters().Single(u => u.Id == userId);
            user.TotpSecretEncrypted = protector.Protect(secret);
            user.TotpEnabledAt = DateTimeOffset.UtcNow;
        });
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
