using System.Net;
using AngleSharp.Html.Parser;
using FluentAssertions;
using Harbora.Domain.Common;
using Harbora.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The customer's own view of support access — the second half of "two-sided", and a partial answer
/// to backlog HARBORA-0056.
///
/// <para>
/// Parsed with AngleSharp rather than matched as text: what is under test is which rows a workspace
/// is shown, and a substring search on a page that contains a session id in three places cannot tell
/// "listed" from "mentioned". The tenancy assertion is made in both directions, because a page that
/// finds its own rows proves nothing about whether it can also reach somebody else's.
/// </para>
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class SupportAccessPageHttpTests(HarboraHttpFixture fixture)
{
    private HarboraWebFactory Panel => fixture.Panel;

    private (Guid WorkspaceId, User Customer) GivenCustomer(string slug, string email)
    {
        var workspaceId = Guid.CreateVersion7();
        Panel.Seed(db => db.Workspaces.Add(new Workspace
        {
            Id = workspaceId, Name = slug, Slug = slug, IsDefault = false
        }));
        return (workspaceId, Panel.GivenUser(workspaceId, email, SystemRole.Member));
    }

    private async Task<Guid> GivenSupportSession(
        string remoteIp, string adminEmail, Guid workspaceId, Guid customerId, string reason)
    {
        var client = await Panel.SignedInAs(remoteIp, adminEmail);
        var path = $"/tenants/{workspaceId}/support/{customerId}";
        var token = await client.AntiforgeryTokenFrom(path);
        (await client.PostFormAsync(path, token, ("reason", reason)))
            .StatusCode.Should().Be(HttpStatusCode.Found);

        return Panel.Read(db => db.SupportSessions.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.TargetUserId == customerId)
            .OrderByDescending(s => s.StartedAt).First().Id);
    }

    /// <summary>The session ids the page actually lists, read from the DOM rather than the text.</summary>
    private static async Task<IReadOnlyList<string>> ListedSessionsAsync(HttpResponseMessage page)
    {
        var document = await new HtmlParser().ParseDocumentAsync(await page.Content.ReadAsStreamAsync());
        return document.QuerySelectorAll("[data-support-session-row]")
            .Select(e => e.GetAttribute("data-support-session-row")!)
            .ToList();
    }

    [Fact]
    public async Task A_workspace_with_no_support_history_says_so_rather_than_showing_a_blank_table()
    {
        var (_, _) = GivenCustomer("acc-tenant-0", "acc-customer0@example.com");
        var client = await Panel.SignedInAs("203.0.113.130", "acc-customer0@example.com");

        var page = await client.GetAsync("/workspaces/support-access");

        page.StatusCode.Should().Be(HttpStatusCode.OK);
        var document = await new HtmlParser().ParseDocumentAsync(await page.Content.ReadAsStreamAsync());
        document.QuerySelectorAll("[data-support-session-row]").Should().BeEmpty();
        // The shared empty state, not a header with nothing under it.
        document.QuerySelector("[data-support-access]").Should().NotBeNull();
        document.QuerySelector("[data-lucide=shield-check]").Should().NotBeNull(
            "an empty list still owes the reader a sentence");
    }

    [Fact]
    public async Task The_customer_sees_the_session_that_was_opened_against_them_and_what_it_did()
    {
        Panel.GivenUser(fixture.WorkspaceId, "acc-admin1@example.com", SystemRole.Owner);
        var (workspaceId, customer) = GivenCustomer("acc-tenant-1", "acc-customer1@example.com");

        var sessionId = await GivenSupportSession(
            "203.0.113.131", "acc-admin1@example.com", workspaceId, customer.Id,
            "Checking why their deploy fails.");

        var client = await Panel.SignedInAs("203.0.113.132", "acc-customer1@example.com");
        var page = await client.GetAsync("/workspaces/support-access");

        page.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ListedSessionsAsync(page)).Should().ContainSingle().Which.Should().Be(sessionId.ToString());

        var body = await page.Content.ReadAsStringAsync();
        body.Should().Contain("acc-admin1@example.com", "the customer is owed the name of who it was");
        body.Should().Contain("Checking why their deploy fails.", "and the reason they were given");
        body.Should().Contain("support.session.started",
            "the acts recorded under the session are the point of the page");
    }

    [Fact]
    public async Task One_workspace_cannot_see_another_workspaces_support_history()
    {
        Panel.GivenUser(fixture.WorkspaceId, "acc-admin2@example.com", SystemRole.Owner);
        var (theirs, theirCustomer) = GivenCustomer("acc-tenant-2a", "acc-customer2a@example.com");
        var (mine, _) = GivenCustomer("acc-tenant-2b", "acc-customer2b@example.com");

        var theirSession = await GivenSupportSession(
            "203.0.113.133", "acc-admin2@example.com", theirs, theirCustomer.Id, "Their problem.");

        // Finds its own: the first direction, without which the second proves nothing.
        var theirClient = await Panel.SignedInAs("203.0.113.134", "acc-customer2a@example.com");
        (await ListedSessionsAsync(await theirClient.GetAsync("/workspaces/support-access")))
            .Should().Contain(theirSession.ToString());

        // Cannot reach the other's: the direction that matters.
        var myClient = await Panel.SignedInAs("203.0.113.135", "acc-customer2b@example.com");
        var myPage = await myClient.GetAsync("/workspaces/support-access");

        myPage.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ListedSessionsAsync(myPage)).Should().NotContain(theirSession.ToString());
        (await myPage.Content.ReadAsStringAsync()).Should().NotContain("Their problem.");
        _ = mine;
    }

    [Fact]
    public async Task A_session_that_ran_out_reads_differently_from_one_that_was_ended()
    {
        Panel.GivenUser(fixture.WorkspaceId, "acc-admin3@example.com", SystemRole.Owner);
        var (workspaceId, customer) = GivenCustomer("acc-tenant-3", "acc-customer3@example.com");

        var sessionId = await GivenSupportSession(
            "203.0.113.136", "acc-admin3@example.com", workspaceId, customer.Id, "Left it running.");

        // Closed the way an unattended session closes, rather than by the button.
        Panel.Seed(db =>
        {
            var row = db.SupportSessions.IgnoreQueryFilters().Single(s => s.Id == sessionId);
            row.EndedAt = row.ExpiresAt;
            row.EndedBy = SupportSessionEnding.Expired;
        });

        var client = await Panel.SignedInAs("203.0.113.137", "acc-customer3@example.com");
        var page = await client.GetAsync("/workspaces/support-access");
        var document = await new HtmlParser().ParseDocumentAsync(await page.Content.ReadAsStreamAsync());

        var row = document.QuerySelector($"[data-support-session-row='{sessionId}']");
        row.Should().NotBeNull();
        // Asserted through the shared pill's tone class rather than its word: this panel renders
        // Persian by default, and "expired" would be the wrong thing to look for either way.
        row!.InnerHtml.Should().Contain("bg-idle-soft", "a finished session is not a live one");
        row.InnerHtml.Should().NotContain("data-support-session=\"");
    }
}
