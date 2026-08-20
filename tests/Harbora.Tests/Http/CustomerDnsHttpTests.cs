using System.Net;
using AngleSharp;
using AngleSharp.Dom;
using FluentAssertions;
using Harbora.Domain.Common;
using Harbora.Domain.Identity;
using Harbora.Domain.Networking;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// F9 (2026-08-21 functions-and-services plan, decision 5) rendered through real HTTP requests —
/// the honest states only, never the live-Cloudflare ones: this machine has no Cloudflare
/// credential, and <see cref="HarboraWebFactory"/> does not substitute <c>IHttpClientFactory</c>, so
/// a test that pushed a stored token down the "list zones" path would make a real outbound call to
/// Cloudflare. <see cref="CustomerCloudflareServiceTests"/> proves the live-call paths (round trip,
/// cannot-list-zones, tenancy) against a fake handler instead; this file proves what the actual
/// rendered pages say when there is nothing to call out for — the no-token state, and the stored
/// summary card, both of which are pure database reads.
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class CustomerDnsHttpTests(HarboraHttpFixture fixture)
{
    private HarboraWebFactory Panel => fixture.Panel;

    private static async Task<IDocument> ParseAsync(string html) =>
        await BrowsingContext.New(Configuration.Default).OpenAsync(req => req.Content(html));

    /// <summary>A workspace of its own, so seeding a <see cref="CustomerDnsCredential"/> row for it
    /// cannot collide with the shared fixture workspace other tests in this collection also use —
    /// the table's own unique index is one row per workspace.</summary>
    private Guid GivenWorkspace(string slug)
    {
        var workspaceId = Guid.CreateVersion7();
        Panel.Seed(db => db.Workspaces.Add(new Workspace { Id = workspaceId, Name = slug, Slug = slug }));
        return workspaceId;
    }

    // ---- no token: says what is needed, nothing else ----

    [Fact]
    public async Task The_dns_page_shows_the_add_token_form_when_none_is_set()
    {
        Panel.GivenUser(fixture.WorkspaceId, "dns-no-token@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.170", "dns-no-token@example.com");

        var response = await client.GetAsync("/domains/dns");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var document = await ParseAsync(await response.Content.ReadAsStringAsync());

        document.QuerySelector("[data-dns-no-token]").Should().NotBeNull(
            "a workspace with no token must be told what is needed, not shown an empty records table");
        document.QuerySelector("[data-dns-token-form] input[name='token']").Should().NotBeNull(
            "the form to add a token must actually be here, not just described");

        document.QuerySelector("[data-dns-zone-picker]").Should().BeNull(
            "there is nothing to pick a zone from until a token exists");
        document.QuerySelector("[data-dns-records-table]").Should().BeNull(
            "no table at all — not even an empty one — until there is a real answer to show in it");
    }

    [Fact]
    public async Task Saving_a_blank_token_is_refused_and_nothing_is_stored()
    {
        Panel.GivenUser(fixture.WorkspaceId, "dns-blank-token@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.171", "dns-blank-token@example.com");

        var token = await client.AntiforgeryTokenFrom("/domains/dns");

        var response = await client.PostFormAsync("/domains/dns/token", token, ("token", ""));

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.RedirectPath().Should().Be("/domains/dns");
        Panel.Read(db => db.CustomerDnsCredentials.IgnoreQueryFilters()
                .Any(c => c.WorkspaceId == fixture.WorkspaceId))
            .Should().BeFalse("an empty token must never reach Cloudflare or be stored");
    }

    // ---- the Domains page's own summary: stored state only, no live call ----

    [Fact]
    public async Task The_domains_page_says_no_token_is_set_when_none_is()
    {
        var workspaceId = GivenWorkspace("dns-summary-none");
        Panel.GivenUser(workspaceId, "dns-summary-none@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.172", "dns-summary-none@example.com");

        var document = await ParseAsync(
            await (await client.GetAsync("/domains")).Content.ReadAsStringAsync());

        document.QuerySelector("[data-dns-summary-no-token]").Should().NotBeNull();
        document.QuerySelector("[data-dns-summary-error]").Should().BeNull();
        document.QuerySelector("[data-dns-summary-verified]").Should().BeNull();
    }

    [Fact]
    public async Task The_domains_page_reads_back_a_stored_verification_error_for_its_own_workspace()
    {
        var workspaceId = GivenWorkspace("dns-summary-error");
        Panel.Seed(db => db.CustomerDnsCredentials.Add(new CustomerDnsCredential
        {
            WorkspaceId = workspaceId,
            EncryptedToken = "not-decrypted-by-this-test",
            LastVerificationError = "The token is valid but cannot read active zone example.com."
        }));
        Panel.GivenUser(workspaceId, "dns-summary-error@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.173", "dns-summary-error@example.com");

        var document = await ParseAsync(
            await (await client.GetAsync("/domains")).Content.ReadAsStringAsync());

        var banner = document.QuerySelector("[data-dns-summary-error]");
        banner.Should().NotBeNull("a stored failure must be read back, not hidden behind a plain \"has token\" line");
        banner!.TextContent.Should().Contain("cannot read active zone");
        document.QuerySelector("[data-dns-summary-no-token]").Should().BeNull();
    }

    [Fact]
    public async Task Another_workspaces_stored_dns_error_never_appears_on_this_workspaces_domains_page()
    {
        var otherWorkspaceId = GivenWorkspace("dns-tenant-owner");
        Panel.Seed(db => db.CustomerDnsCredentials.Add(new CustomerDnsCredential
        {
            WorkspaceId = otherWorkspaceId,
            EncryptedToken = "not-decrypted-by-this-test",
            LastVerificationError = "SECRET-ONLY-THE-OWNER-WORKSPACE-SHOULD-EVER-SEE"
        }));

        var callerWorkspaceId = GivenWorkspace("dns-tenant-caller");
        Panel.GivenUser(callerWorkspaceId, "dns-tenant-caller@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.174", "dns-tenant-caller@example.com");

        var document = await ParseAsync(
            await (await client.GetAsync("/domains")).Content.ReadAsStringAsync());

        document.QuerySelector("[data-dns-summary-no-token]").Should().NotBeNull(
            "this workspace has no token of its own — the other workspace's row must not leak in");
        document.QuerySelector("body")!.TextContent.Should().NotContain(
            "SECRET-ONLY-THE-OWNER-WORKSPACE-SHOULD-EVER-SEE");
    }
}
