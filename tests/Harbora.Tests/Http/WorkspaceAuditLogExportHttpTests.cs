using System.Net;
using System.Text;
using System.Text.Json;
using AngleSharp.Html.Parser;
using FluentAssertions;
using Harbora.Domain.Auditing;
using Harbora.Domain.Common;
using Harbora.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// CSV and JSON export of the workspace's own audit log, at
/// <c>/workspaces/audit-log/export.csv</c> and <c>/workspaces/audit-log/export.json</c>.
///
/// <para>
/// The rule this exists to hold: an export must return exactly the rows the page
/// (<see cref="WorkspaceAuditLogPageHttpTests"/>) would have shown — same capability check, same
/// workspace scope, same absence of any other filter — never more (a tenancy bug) and never fewer (a
/// silent lie). Both directions of the tenancy check are pinned here independently of the page's own
/// tests, because the export is a second HTTP action with its own <c>WorkspaceId ==</c> guard, not a
/// code path the page's tests happen to also exercise.
/// </para>
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class WorkspaceAuditLogExportHttpTests(HarboraHttpFixture fixture)
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

    private Guid GivenAuditRow(Guid? workspaceId, string action, string actorEmail, string? targetId = "abc123", string? ip = "198.51.100.9")
    {
        var row = new AuditLog
        {
            WorkspaceId = workspaceId,
            Action = action,
            ActorEmail = actorEmail,
            TargetType = "app",
            TargetId = targetId,
            IpAddress = ip,
            CreatedAt = DateTimeOffset.UtcNow
        };
        Panel.Seed(db => db.AuditLogs.Add(row));
        return row.Id;
    }

    private static string CsvTextOf(HttpResponseMessage response, byte[] bytes)
    {
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/csv");
        bytes.Take(3).Should().Equal(Encoding.UTF8.GetPreamble(), "Excel needs the BOM to read Persian actor names correctly");
        return Encoding.UTF8.GetString(bytes.Skip(3).ToArray());
    }

    [Fact]
    public async Task CSV_export_lists_the_workspaces_own_row()
    {
        var (workspaceId, _) = GivenCustomer("audx-tenant-1", "audx-c1@example.com");
        var rowId = GivenAuditRow(workspaceId, "app.deploy", "audx-c1@example.com");

        var client = await Panel.SignedInAs("198.51.100.220", "audx-c1@example.com");
        var response = await client.GetAsync("/workspaces/audit-log/export.csv");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var text = CsvTextOf(response, await response.Content.ReadAsByteArrayAsync());
        text.Should().Contain("id,timestamp,actor,action,targetType,targetId,ipAddress");
        text.Should().Contain(rowId.ToString());
        text.Should().Contain("app.deploy");
        text.Should().Contain("audx-c1@example.com");
    }

    [Fact]
    public async Task CSV_export_excludes_another_workspaces_rows()
    {
        var (theirs, _) = GivenCustomer("audx-tenant-2a", "audx-c2a@example.com");
        var (mine, _) = GivenCustomer("audx-tenant-2b", "audx-c2b@example.com");
        var theirRowId = GivenAuditRow(theirs, "app.delete", "audx-c2a@example.com");
        var myRowId = GivenAuditRow(mine, "app.deploy", "audx-c2b@example.com");

        // Finds its own first -- without this direction, the second proves nothing.
        var theirClient = await Panel.SignedInAs("198.51.100.221", "audx-c2a@example.com");
        var theirResponse = await theirClient.GetAsync("/workspaces/audit-log/export.csv");
        var theirText = CsvTextOf(theirResponse, await theirResponse.Content.ReadAsByteArrayAsync());
        theirText.Should().Contain(theirRowId.ToString());

        var myClient = await Panel.SignedInAs("198.51.100.222", "audx-c2b@example.com");
        var myResponse = await myClient.GetAsync("/workspaces/audit-log/export.csv");
        var myText = CsvTextOf(myResponse, await myResponse.Content.ReadAsByteArrayAsync());

        myText.Should().Contain(myRowId.ToString());
        myText.Should().NotContain(theirRowId.ToString());
        myText.Should().NotContain("app.delete");
        myText.Should().NotContain("audx-c2a@example.com");
    }

    [Fact]
    public async Task JSON_export_lists_the_workspaces_own_row()
    {
        var (workspaceId, _) = GivenCustomer("audx-tenant-3", "audx-c3@example.com");
        var rowId = GivenAuditRow(workspaceId, "app.deploy", "audx-c3@example.com", targetId: "app-9", ip: "198.51.100.10");

        var client = await Panel.SignedInAs("198.51.100.223", "audx-c3@example.com");
        var response = await client.GetAsync("/workspaces/audit-log/export.json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        payload.GetProperty("truncated").GetBoolean().Should().BeFalse();
        payload.GetProperty("totalMatchingRows").GetInt32().Should().Be(1);
        var entry = payload.GetProperty("entries")[0];
        entry.GetProperty("id").GetGuid().Should().Be(rowId);
        entry.GetProperty("action").GetString().Should().Be("app.deploy");
        entry.GetProperty("actorEmail").GetString().Should().Be("audx-c3@example.com");
        entry.GetProperty("targetId").GetString().Should().Be("app-9");
        entry.GetProperty("ipAddress").GetString().Should().Be("198.51.100.10");
    }

    [Fact]
    public async Task JSON_export_excludes_another_workspaces_rows()
    {
        var (theirs, _) = GivenCustomer("audx-tenant-4a", "audx-c4a@example.com");
        var (mine, _) = GivenCustomer("audx-tenant-4b", "audx-c4b@example.com");
        var theirRowId = GivenAuditRow(theirs, "app.delete", "audx-c4a@example.com");
        var myRowId = GivenAuditRow(mine, "app.deploy", "audx-c4b@example.com");

        var theirClient = await Panel.SignedInAs("198.51.100.224", "audx-c4a@example.com");
        var theirResponse = await theirClient.GetAsync("/workspaces/audit-log/export.json");
        var theirPayload = JsonDocument.Parse(await theirResponse.Content.ReadAsStringAsync()).RootElement;
        theirPayload.GetProperty("entries")[0].GetProperty("id").GetGuid().Should().Be(theirRowId);

        var myClient = await Panel.SignedInAs("198.51.100.225", "audx-c4b@example.com");
        var myResponse = await myClient.GetAsync("/workspaces/audit-log/export.json");
        var myPayload = JsonDocument.Parse(await myResponse.Content.ReadAsStringAsync()).RootElement;

        var myIds = myPayload.GetProperty("entries").EnumerateArray()
            .Select(e => e.GetProperty("id").GetGuid()).ToList();
        myIds.Should().Contain(myRowId);
        myIds.Should().NotContain(theirRowId);
    }

    [Fact]
    public async Task CSV_export_correctly_escapes_a_value_carrying_a_comma_a_quote_and_a_newline()
    {
        var (workspaceId, _) = GivenCustomer("audx-tenant-5", "audx-c5@example.com");
        const string awkwardActor = "Vahid, \"the ops guy\"\nsecond line <audx-c5@example.com>";
        GivenAuditRow(workspaceId, "app.deploy", awkwardActor);

        var client = await Panel.SignedInAs("198.51.100.226", "audx-c5@example.com");
        var response = await client.GetAsync("/workspaces/audit-log/export.csv");
        var text = CsvTextOf(response, await response.Content.ReadAsByteArrayAsync());

        text.Should().Contain("\"Vahid, \"\"the ops guy\"\"\nsecond line <audx-c5@example.com>\"",
            "the comma, the quote and the newline must all survive inside one properly escaped field");
    }

    [Fact]
    public async Task Export_returns_rows_beyond_the_pages_own_window_because_pagination_is_not_a_filter()
    {
        var (workspaceId, _) = GivenCustomer("audx-tenant-6", "audx-c6@example.com");
        // More rows than one page (AuditLogPageSize = 50) shows, so an export that only reused the
        // page's Skip/Take would silently drop the older ones.
        var ids = Enumerable.Range(0, 60).Select(i => GivenAuditRow(workspaceId, $"app.action{i}", "audx-c6@example.com")).ToList();

        var client = await Panel.SignedInAs("198.51.100.227", "audx-c6@example.com");
        var response = await client.GetAsync("/workspaces/audit-log/export.json");
        var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        payload.GetProperty("returnedRows").GetInt32().Should().Be(60);
        payload.GetProperty("truncated").GetBoolean().Should().BeFalse();
        var returnedIds = payload.GetProperty("entries").EnumerateArray()
            .Select(e => e.GetProperty("id").GetGuid()).ToHashSet();
        returnedIds.Should().BeEquivalentTo(ids);
    }

    [Fact]
    public async Task An_export_request_with_no_active_workspace_is_challenged_the_same_as_the_page()
    {
        var client = Panel.ClientFrom("198.51.100.228");

        var csv = await client.GetAsync("/workspaces/audit-log/export.csv");
        var json = await client.GetAsync("/workspaces/audit-log/export.json");

        csv.StatusCode.Should().Be(HttpStatusCode.Found);
        csv.RedirectPath().Should().Be("/account/login");
        json.StatusCode.Should().Be(HttpStatusCode.Found);
        json.RedirectPath().Should().Be("/account/login");
    }

    [Fact]
    public async Task The_page_shows_export_links_and_no_truncation_notice_when_under_the_bound()
    {
        var (workspaceId, _) = GivenCustomer("audx-tenant-7", "audx-c7@example.com");
        GivenAuditRow(workspaceId, "app.deploy", "audx-c7@example.com");

        var client = await Panel.SignedInAs("198.51.100.229", "audx-c7@example.com");
        var page = await client.GetAsync("/workspaces/audit-log");

        page.StatusCode.Should().Be(HttpStatusCode.OK);
        var document = await new HtmlParser().ParseDocumentAsync(await page.Content.ReadAsStreamAsync());
        document.QuerySelector("[data-audit-export='csv']")!.GetAttribute("href").Should().Be("/workspaces/audit-log/export.csv");
        document.QuerySelector("[data-audit-export='json']")!.GetAttribute("href").Should().Be("/workspaces/audit-log/export.json");
        document.QuerySelector("[data-export-truncation-notice]").Should().BeNull(
            "a workspace nowhere near the export bound must not be warned about truncation");
    }
}
