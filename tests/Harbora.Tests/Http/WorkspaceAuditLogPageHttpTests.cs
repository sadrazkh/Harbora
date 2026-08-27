using System.Net;
using AngleSharp.Html.Parser;
using FluentAssertions;
using Harbora.Domain.Auditing;
using Harbora.Domain.Common;
using Harbora.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The rest of HARBORA-0056: a workspace operator's own audit log, at <c>/workspaces/audit-log</c>.
///
/// <para>
/// <c>AuditLog</c> carries no ambient query filter at all (see <c>HarboraDbContext</c>'s remark on
/// <c>AuditLog.WorkspaceId</c>) — unlike <c>App</c>, which <c>LogsControllerTenancyTests</c> has to
/// deliberately unscope to prove its controller's own guard matters independently. Here there is no
/// ambient guard to disable: the explicit <c>WorkspaceId ==</c> comparison in
/// <c>WorkspacesController.AuditLog</c> is the *only* thing standing between one workspace's rows and
/// another's, and between a workspace's own rows and the platform-level ones that belong to none. Both
/// directions are pinned below, plus the third case this table introduces that <c>App</c> never had:
/// a row that belongs to nobody, which the page must neither show nor stay silent about.
/// </para>
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class WorkspaceAuditLogPageHttpTests(HarboraHttpFixture fixture)
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

    private void GivenAuditRow(Guid? workspaceId, string action, string actorEmail) =>
        Panel.Seed(db => db.AuditLogs.Add(new AuditLog
        {
            WorkspaceId = workspaceId,
            Action = action,
            ActorEmail = actorEmail,
            CreatedAt = DateTimeOffset.UtcNow
        }));

    /// <summary>The audit row ids the page actually lists, read from the DOM rather than the text.</summary>
    private static async Task<IReadOnlyList<string>> ListedRowsAsync(HttpResponseMessage page)
    {
        var document = await new HtmlParser().ParseDocumentAsync(await page.Content.ReadAsStreamAsync());
        return document.QuerySelectorAll("[data-audit-row]")
            .Select(e => e.GetAttribute("data-audit-row")!)
            .ToList();
    }

    private static Guid RowId(HarboraWebFactory panel, Guid workspaceId, string action) =>
        panel.Read(db => db.AuditLogs.IgnoreQueryFilters()
            .Where(a => a.WorkspaceId == workspaceId && a.Action == action)
            .OrderByDescending(a => a.CreatedAt).First().Id);

    [Fact]
    public async Task A_workspace_with_no_audit_history_says_so_rather_than_showing_a_blank_table()
    {
        var (_, _) = GivenCustomer("aud-tenant-0", "aud-customer0@example.com");
        var client = await Panel.SignedInAs("203.0.113.140", "aud-customer0@example.com");

        var page = await client.GetAsync("/workspaces/audit-log");

        page.StatusCode.Should().Be(HttpStatusCode.OK);
        var document = await new HtmlParser().ParseDocumentAsync(await page.Content.ReadAsStreamAsync());
        document.QuerySelectorAll("[data-audit-row]").Should().BeEmpty();
        document.QuerySelector("[data-lucide=shield-check]").Should().NotBeNull(
            "an empty list still owes the reader a sentence, the same idiom support-access uses");
    }

    [Fact]
    public async Task The_operator_sees_actions_recorded_for_their_own_workspace()
    {
        var (workspaceId, _) = GivenCustomer("aud-tenant-1", "aud-customer1@example.com");
        GivenAuditRow(workspaceId, "app.deploy", "aud-customer1@example.com");
        var rowId = RowId(Panel, workspaceId, "app.deploy");

        var client = await Panel.SignedInAs("203.0.113.141", "aud-customer1@example.com");
        var page = await client.GetAsync("/workspaces/audit-log");

        page.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ListedRowsAsync(page)).Should().ContainSingle().Which.Should().Be(rowId.ToString());
        (await page.Content.ReadAsStringAsync()).Should().Contain("app.deploy");
    }

    [Fact]
    public async Task One_workspace_cannot_see_another_workspaces_audit_rows()
    {
        var (theirs, _) = GivenCustomer("aud-tenant-2a", "aud-customer2a@example.com");
        var (mine, _) = GivenCustomer("aud-tenant-2b", "aud-customer2b@example.com");
        GivenAuditRow(theirs, "app.delete", "aud-customer2a@example.com");
        GivenAuditRow(mine, "app.deploy", "aud-customer2b@example.com");
        var theirRowId = RowId(Panel, theirs, "app.delete");
        var myRowId = RowId(Panel, mine, "app.deploy");

        // Finds its own: the first direction, without which the second proves nothing.
        var theirClient = await Panel.SignedInAs("203.0.113.142", "aud-customer2a@example.com");
        (await ListedRowsAsync(await theirClient.GetAsync("/workspaces/audit-log")))
            .Should().Contain(theirRowId.ToString());

        // Cannot reach the other's: the direction that matters. AuditLog has no ambient query filter
        // of its own (HarboraDbContext's remark explains why), so unlike an App-backed page there is
        // no second layer that could be quietly doing this instead — the controller's own
        // WorkspaceId == comparison is the entire guard, and this is what pins it.
        var myClient = await Panel.SignedInAs("203.0.113.143", "aud-customer2b@example.com");
        var myPage = await myClient.GetAsync("/workspaces/audit-log");

        myPage.StatusCode.Should().Be(HttpStatusCode.OK);
        var myRows = await ListedRowsAsync(myPage);
        myRows.Should().Contain(myRowId.ToString());
        myRows.Should().NotContain(theirRowId.ToString());
        (await myPage.Content.ReadAsStringAsync()).Should().NotContain("app.delete");
    }

    [Fact]
    public async Task Platform_level_rows_with_no_workspace_are_excluded_and_the_page_says_so()
    {
        var (workspaceId, _) = GivenCustomer("aud-tenant-3", "aud-customer3@example.com");
        GivenAuditRow(workspaceId, "app.deploy", "aud-customer3@example.com");
        var ownRowId = RowId(Panel, workspaceId, "app.deploy");
        // A platform-level act (no workspace at all) and, separately, a pre-column row would look the
        // same to this query: both are WorkspaceId == null. One row stands in for both.
        GivenAuditRow(null, "platform.name", "admin@harbora.local");

        var client = await Panel.SignedInAs("203.0.113.144", "aud-customer3@example.com");
        var page = await client.GetAsync("/workspaces/audit-log");

        page.StatusCode.Should().Be(HttpStatusCode.OK);
        var document = await new HtmlParser().ParseDocumentAsync(await page.Content.ReadAsStreamAsync());
        document.QuerySelectorAll("[data-audit-row]").Select(e => e.GetAttribute("data-audit-row")!)
            .Should().ContainSingle().Which.Should().Be(ownRowId.ToString(),
                "a row with no workspace must not be fabricated into this workspace's history");
        document.QuerySelector("[data-platform-events-excluded]").Should().NotBeNull(
            "the page must say platform-level events are excluded by design, not just quietly drop them");
    }
}
