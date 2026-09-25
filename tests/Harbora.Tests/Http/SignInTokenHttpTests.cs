using System.Net;
using System.Net.Http.Headers;
using AngleSharp.Html.Parser;
using FluentAssertions;
using Harbora.Domain.Common;
using Harbora.Domain.Identity;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The whole feature through the real pipeline: mint, redeem, the banner, a second attempt, and
/// revocation — the token analogue of <c>SupportAccessPageHttpTests</c>.
///
/// <para>
/// One thing this cannot prove: two concurrent redemptions producing one winner. EF's InMemory
/// provider — what <c>HarboraWebFactory</c> substitutes for every HTTP test in this project — does not
/// implement <c>ExecuteUpdateAsync</c> at all, so <c>SignInTokenService</c> falls back to a plain,
/// deliberately non-atomic read-then-write on it (see <c>TryClaimAsync</c>'s own doc). That fallback is
/// what lets an ordinary, single-caller redemption run here; it would not survive two truly concurrent
/// ones. <c>SignInTokenRedemptionTests</c> in the Postgres lane is where the real
/// <c>ExecuteUpdateAsync</c> path — and the two-concurrent-callers guarantee this feature's whole point
/// is — actually runs; it is unverified on this machine (no Docker).
/// </para>
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class SignInTokenHttpTests(HarboraHttpFixture fixture)
{
    private HarboraWebFactory Panel => fixture.Panel;

    private (Guid WorkspaceId, User Owner) GivenOwner(string slug, string email)
    {
        var workspaceId = Guid.CreateVersion7();
        Panel.Seed(db => db.Workspaces.Add(new Workspace
        {
            Id = workspaceId, Name = slug, Slug = slug, IsDefault = false
        }));
        return (workspaceId, Panel.GivenUser(workspaceId, email, SystemRole.Owner));
    }

    /// <summary>Mints a token over the real form and returns the plaintext shown on the response.</summary>
    private static async Task<string> MintTokenAsync(HttpClient client, string purpose)
    {
        var antiforgery = await client.AntiforgeryTokenFrom("/account/tokens");
        var response = await client.PostFormAsync("/account/tokens", antiforgery, ("purpose", purpose));
        response.StatusCode.Should().Be(HttpStatusCode.OK, "a successful mint re-renders the same page");

        var document = await new HtmlParser().ParseDocumentAsync(await response.Content.ReadAsStreamAsync());
        var code = document.QuerySelector("#signin-token-secret");
        code.Should().NotBeNull("a successful mint shows the plaintext exactly once");
        return code!.TextContent.Trim();
    }

    [Fact]
    public async Task Minting_shows_the_secret_once_and_the_next_load_of_the_page_does_not_repeat_it()
    {
        GivenOwner("tok-tenant-0", "tok-owner0@example.com");
        var client = await Panel.SignedInAs("203.0.113.140", "tok-owner0@example.com");

        var plaintext = await MintTokenAsync(client, "Fix the deploy on shop");
        plaintext.Should().NotBeNullOrWhiteSpace();

        var again = await client.GetAsync("/account/tokens");
        (await again.Content.ReadAsStringAsync()).Should().NotContain(plaintext,
            "the secret is shown on the response to the mint itself and never again");
    }

    [Fact]
    public async Task Minting_without_a_purpose_is_refused_and_nothing_is_created()
    {
        GivenOwner("tok-tenant-empty", "tok-owner-empty@example.com");
        var client = await Panel.SignedInAs("203.0.113.152", "tok-owner-empty@example.com");

        var antiforgery = await client.AntiforgeryTokenFrom("/account/tokens");
        var response = await client.PostFormAsync("/account/tokens", antiforgery, ("purpose", ""));

        response.StatusCode.Should().Be(HttpStatusCode.OK, "a refusal re-renders the same page rather than redirecting");
        (await response.Content.ReadAsStringAsync()).Should().Contain("Say what this token is for");
    }

    [Fact]
    public async Task A_token_that_never_matched_anything_is_refused_without_signing_anybody_in()
    {
        GivenOwner("tok-tenant-unknown", "tok-owner-unknown@example.com");
        var agent = Panel.ClientFrom("203.0.113.153");

        var response = await agent.GetAsync("/account/token-signin?token=this-was-never-issued");

        response.StatusCode.Should().Be(HttpStatusCode.OK, "a refusal re-renders the form rather than redirecting anywhere");
        (await response.Content.ReadAsStringAsync()).Should().Contain("This token is not valid.");

        var home = await agent.GetAsync("/");
        (await home.Content.ReadAsStringAsync()).Should().NotContain("data-signin-token-session=",
            "a refused redemption must not open any kind of session");
    }

    [Fact]
    public async Task The_redemption_page_with_no_token_shows_the_paste_a_token_form()
    {
        var response = await Panel.ClientFrom("203.0.113.154").GetAsync("/account/token-signin");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync();
        html.Should().Contain("name=\"token\"");
        html.Should().Contain("__RequestVerificationToken", "the form must carry an antiforgery field for its POST");
    }

    [Fact]
    public async Task Redeeming_via_the_link_signs_the_agent_in_and_the_banner_names_the_purpose()
    {
        GivenOwner("tok-tenant-1", "tok-owner1@example.com");
        var ownerClient = await Panel.SignedInAs("203.0.113.141", "tok-owner1@example.com");
        var plaintext = await MintTokenAsync(ownerClient, "Fix the deploy on shop");

        // A fresh, unauthenticated client — the agent has never had a cookie on this panel before.
        var agentClient = Panel.ClientFrom("203.0.113.142");
        var redeemed = await agentClient.GetAsync($"/account/token-signin?token={Uri.EscapeDataString(plaintext)}");

        redeemed.StatusCode.Should().Be(HttpStatusCode.Found, "a successful redemption signs in and redirects");
        redeemed.RedirectPath().Should().Be("/");

        var landing = await agentClient.GetAsync("/");
        landing.StatusCode.Should().Be(HttpStatusCode.OK, "the cookie the redemption issued must actually work");
        var html = await landing.Content.ReadAsStringAsync();
        html.Should().Contain("data-signin-token-session=", "the banner must be on every page while the session is live");
        html.Should().Contain("Fix the deploy on shop", "the banner names the purpose the owner gave");
    }

    /// <summary>The banner's own text node, decoded — Razor's default HtmlEncoder writes non-ASCII
    /// characters (every letter of Persian) as <c>&amp;#x...;</c> numeric references, so the raw
    /// response string never contains the literal Persian sentence even when the banner does. Parsing
    /// the document is what turns those references back into the text a person would actually read.</summary>
    private static async Task<string> BannerTextAsync(HttpResponseMessage response)
    {
        var document = await new HtmlParser().ParseDocumentAsync(await response.Content.ReadAsStreamAsync());
        var banner = document.QuerySelector("[data-signin-token-session]");
        banner.Should().NotBeNull("the banner must be on the page while the token session is live");
        return banner!.TextContent;
    }

    [Fact]
    public async Task The_banner_renders_in_english_when_asked_and_in_persian_by_default()
    {
        GivenOwner("tok-tenant-2", "tok-owner2@example.com");
        var ownerClient = await Panel.SignedInAs("203.0.113.143", "tok-owner2@example.com");
        var englishPlaintext = await MintTokenAsync(ownerClient, "Restart the failing worker");

        var englishAgent = Panel.ClientFrom("203.0.113.144");
        englishAgent.DefaultRequestHeaders.AcceptLanguage.Add(new StringWithQualityHeaderValue("en"));
        await englishAgent.GetAsync($"/account/token-signin?token={Uri.EscapeDataString(englishPlaintext)}");
        var englishBanner = await BannerTextAsync(await englishAgent.GetAsync("/"));
        englishBanner.Should().Contain("This session was opened with a one-time sign-in token.");

        GivenOwner("tok-tenant-2b", "tok-owner2b@example.com");
        var ownerClient2 = await Panel.SignedInAs("203.0.113.145", "tok-owner2b@example.com");
        var persianPlaintext = await MintTokenAsync(ownerClient2, "Restart the failing worker");

        // No Accept-Language at all — the panel's own default, per the request culture being "fa".
        var persianAgent = Panel.ClientFrom("203.0.113.146");
        await persianAgent.GetAsync($"/account/token-signin?token={Uri.EscapeDataString(persianPlaintext)}");
        var persianBanner = await BannerTextAsync(await persianAgent.GetAsync("/"));
        persianBanner.Should().Contain("این نشست با یک توکن ورود یک‌بارمصرف باز شده است.");
    }

    [Fact]
    public async Task Redeeming_a_second_time_is_refused_and_the_first_session_keeps_working()
    {
        GivenOwner("tok-tenant-3", "tok-owner3@example.com");
        var ownerClient = await Panel.SignedInAs("203.0.113.147", "tok-owner3@example.com");
        var plaintext = await MintTokenAsync(ownerClient, "Fix the deploy on shop");

        var firstAgent = Panel.ClientFrom("203.0.113.148");
        (await firstAgent.GetAsync($"/account/token-signin?token={Uri.EscapeDataString(plaintext)}"))
            .StatusCode.Should().Be(HttpStatusCode.Found);

        var secondAgent = Panel.ClientFrom("203.0.113.149");
        var second = await secondAgent.GetAsync($"/account/token-signin?token={Uri.EscapeDataString(plaintext)}");

        second.StatusCode.Should().Be(HttpStatusCode.OK, "a refused redemption re-renders the form rather than signing anybody in");
        (await second.Content.ReadAsStringAsync()).Should().Contain("This token has already been used.");

        var stillWorks = await firstAgent.GetAsync("/");
        stillWorks.StatusCode.Should().Be(HttpStatusCode.OK,
            "the winner's session must not be disturbed by the second, refused attempt");
    }

    [Fact]
    public async Task Revoking_ends_the_agents_session_at_the_very_next_request()
    {
        GivenOwner("tok-tenant-4", "tok-owner4@example.com");
        var ownerClient = await Panel.SignedInAs("203.0.113.150", "tok-owner4@example.com");
        var plaintext = await MintTokenAsync(ownerClient, "Fix the deploy on shop");

        var agentClient = Panel.ClientFrom("203.0.113.151");
        await agentClient.GetAsync($"/account/token-signin?token={Uri.EscapeDataString(plaintext)}");
        (await agentClient.GetAsync("/")).StatusCode.Should().Be(HttpStatusCode.OK, "the session must start out live");

        var listPage = await ownerClient.GetAsync("/account/tokens");
        var document = await new HtmlParser().ParseDocumentAsync(await listPage.Content.ReadAsStreamAsync());
        var tokenId = document.QuerySelector("[data-signin-token-row]")!.GetAttribute("data-signin-token-row");

        var revokeAntiforgery = await ownerClient.AntiforgeryTokenFrom("/account/tokens");
        var revoke = await ownerClient.PostFormAsync($"/account/tokens/{tokenId}/revoke", revokeAntiforgery);
        revoke.StatusCode.Should().Be(HttpStatusCode.Found);

        var next = await agentClient.GetAsync("/");
        next.StatusCode.Should().Be(HttpStatusCode.Found,
            "a token revoked a moment ago must stop the very next request, not whenever its ordinary cookie would otherwise expire");
        next.RedirectPath().Should().Be("/account/login");
    }

    [Fact]
    public async Task Revoking_a_never_redeemed_token_marks_it_revoked_on_the_owners_own_list()
    {
        GivenOwner("tok-tenant-5", "tok-owner5@example.com");
        var client = await Panel.SignedInAs("203.0.113.155", "tok-owner5@example.com");
        await MintTokenAsync(client, "Fix the deploy on shop");

        var listPage = await client.GetAsync("/account/tokens");
        var document = await new HtmlParser().ParseDocumentAsync(await listPage.Content.ReadAsStreamAsync());
        var row = document.QuerySelector("[data-signin-token-row]");
        row.Should().NotBeNull();
        var tokenId = row!.GetAttribute("data-signin-token-row");

        var revokeAntiforgery = await client.AntiforgeryTokenFrom("/account/tokens");
        var revoke = await client.PostFormAsync($"/account/tokens/{tokenId}/revoke", revokeAntiforgery);
        revoke.StatusCode.Should().Be(HttpStatusCode.Found);

        var after = await client.GetAsync("/account/tokens");
        var afterDocument = await new HtmlParser().ParseDocumentAsync(await after.Content.ReadAsStreamAsync());
        var afterRow = afterDocument.QuerySelector($"[data-signin-token-row='{tokenId}']");
        afterRow.Should().NotBeNull();
        afterRow!.QuerySelector("form").Should().BeNull("a revoked token no longer offers a revoke button");
    }
}
