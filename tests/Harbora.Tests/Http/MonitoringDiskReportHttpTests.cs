using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using Harbora.Domain.Common;
using Harbora.Domain.Servers;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// A-brief (2026-09 r4 round): the page at <c>/monitoring/disk-report</c> — reached the only way it
/// can run, through the booted panel, since <see cref="Harbora.Infrastructure.Maintenance.DiskUsageReport"/>
/// needs the full <see cref="Harbora.Application.Abstractions.IServerEngineFactory"/> DI graph the way
/// <see cref="Harbora.Infrastructure.Maintenance.DiskCleanupService"/> and
/// <see cref="Harbora.Infrastructure.Storage.DiskVolumeOrphanReport"/> already do.
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class MonitoringDiskReportHttpTests(HarboraHttpFixture fixture)
{
    private HarboraWebFactory Panel => fixture.Panel;

    private async Task<HttpClient> OwnerClientAsync(string email, string ip)
    {
        Panel.GivenUser(fixture.WorkspaceId, email, SystemRole.Owner);
        return await Panel.SignedInAs(ip, email);
    }

    /// <summary>A local-flagged server of this test's own — every <c>IsLocal</c> row resolves to the
    /// same shared <see cref="HarboraWebFactory.Docker"/> fake, so seeding images on it here is exactly
    /// what a real cleanup run would see (same idiom <c>ServersDiskVolumeReportHttpTests</c> uses).</summary>
    private Guid AddLocalServerAsync(string name)
    {
        var server = new Server { Name = name, Hostname = "localhost", IsLocal = true };
        Panel.Seed(db => db.Servers.Add(server));
        return server.Id;
    }

    /// <summary>
    /// Razor's default <c>HtmlEncoder</c> writes every non-ASCII character — Persian included — as a
    /// numeric character reference (<c>&amp;#x62D;</c>) rather than the literal glyph, which is exactly
    /// right for the browser and exactly wrong for a test asserting a Persian substring against the raw
    /// response body. Decoding first makes the assertion check what a reader actually sees.
    /// </summary>
    private static async Task<string> RenderedTextAsync(HttpResponseMessage response) =>
        WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

    [Fact]
    public async Task An_owner_sees_the_reclaimable_total_and_the_orphan_it_is_made_of()
    {
        AddLocalServerAsync("disk-report-host");
        Panel.Docker.SeedImage("harbora/gone-app-disk-report:build-1");

        var client = await OwnerClientAsync("disk-report-owner@example.com", "203.0.113.180");
        var response = await client.GetAsync("/monitoring/disk-report");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync();

        html.Should().Contain("disk-report-host", "the server this test seeded must be named");
        html.Should().Contain("harbora/gone-app-disk-report:build-1",
            "an orphaned image is named, not just counted");
    }

    [Fact]
    public async Task A_server_with_no_agent_endpoint_reads_as_not_examined_not_as_clean()
    {
        var stranded = new Server { Name = "disk-report-stranded", Hostname = "10.0.9.9", IsLocal = false };
        Panel.Seed(db => db.Servers.Add(stranded));

        var client = await OwnerClientAsync("disk-report-owner2@example.com", "203.0.113.181");
        var response = await client.GetAsync("/monitoring/disk-report");

        response.StatusCode.Should().Be(HttpStatusCode.OK, "an unreachable server is reported, not a 500");
        var html = await RenderedTextAsync(response);

        html.Should().Contain("disk-report-stranded");
        // The default request culture here is Persian (Program.cs: DefaultRequestCulture = "fa"), so
        // the not-examined wording renders in Persian, not English.
        html.Should().Contain("بررسی نشد",
            "a server the factory refuses must be named as not examined, not folded in as clean");
    }

    [Fact]
    public async Task The_upper_bound_disclaimer_is_in_Persian_by_default()
    {
        var client = await OwnerClientAsync("disk-report-owner3@example.com", "203.0.113.182");

        var html = await RenderedTextAsync(await client.GetAsync("/monitoring/disk-report"));

        html.Should().Contain("سقف بالا",
            "the reclaimable figure must be labelled an upper bound in Persian, the panel's default culture");
    }

    [Fact]
    public async Task The_upper_bound_disclaimer_is_in_English_when_the_request_asks_for_it()
    {
        Panel.GivenUser(fixture.WorkspaceId, "disk-report-owner4@example.com", SystemRole.Owner);
        var client = Panel.ClientFrom("203.0.113.183");
        client.DefaultRequestHeaders.AcceptLanguage.Add(new StringWithQualityHeaderValue("en"));
        var token = await client.AntiforgeryTokenFrom("/account/login");
        await client.PostFormAsync("/account/login", token,
            ("Email", "disk-report-owner4@example.com"), ("Password", HarboraWebFactory.TestPassword));

        var html = await RenderedTextAsync(await client.GetAsync("/monitoring/disk-report"));

        html.Should().Contain("upper bound",
            "an English request must see the English half of the disclaimer, not the Persian one");
        html.Should().NotContain("سقف بالا");
    }

    [Fact]
    public async Task The_monitoring_page_links_to_the_disk_report_for_an_owner()
    {
        var client = await OwnerClientAsync("disk-report-owner5@example.com", "203.0.113.184");

        var html = await (await client.GetAsync("/monitoring")).Content.ReadAsStringAsync();

        html.Should().Contain("/monitoring/disk-report",
            "the report must be reachable from the disk banner / cleanup area, not just routable");
    }

    [Fact]
    public async Task A_viewer_may_not_read_the_disk_report()
    {
        Panel.GivenUser(fixture.WorkspaceId, "disk-report-viewer@example.com", SystemRole.Viewer);
        var client = await Panel.SignedInAs("203.0.113.185", "disk-report-viewer@example.com");

        var response = await client.GetAsync("/monitoring/disk-report");

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.RedirectPath().Should().Be("/account/denied");
    }

    [Fact]
    public async Task A_viewer_does_not_see_the_disk_report_link_on_the_monitoring_page()
    {
        Panel.GivenUser(fixture.WorkspaceId, "disk-report-viewer2@example.com", SystemRole.Viewer);
        var client = await Panel.SignedInAs("203.0.113.186", "disk-report-viewer2@example.com");

        var html = await (await client.GetAsync("/monitoring")).Content.ReadAsStringAsync();

        html.Should().NotContain("/monitoring/disk-report",
            "gated exactly like the cleanup button beside it — a viewer gets neither");
    }
}
