using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using Harbora.Domain.Common;
using Harbora.Domain.Servers;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The disk report page and the Clean up disk button, as an operator reaches them: the dangling-images
/// line the page was missing, and the result message the button now gives. Through the booted panel,
/// for the same reason <see cref="MonitoringDiskReportHttpTests"/> is — the report needs the full
/// per-server engine factory graph.
///
/// <para>
/// The engine behind these requests is <see cref="Harbora.Tests.Fakes.FakeDockerEngine"/>: there is no
/// Docker on this machine, so what is proven is what the page says about what the engine answered.
/// </para>
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class MonitoringDanglingHttpTests(HarboraHttpFixture fixture)
{
    private HarboraWebFactory Panel => fixture.Panel;

    private void AddLocalServer(string name) =>
        Panel.Seed(db => db.Servers.Add(new Server { Name = name, Hostname = "localhost", IsLocal = true }));

    /// <summary>Razor writes every non-ASCII character as a numeric reference; decode before asserting.</summary>
    private static async Task<string> RenderedTextAsync(HttpResponseMessage response) =>
        WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

    private async Task<HttpClient> OwnerAsync(string email, string ip, bool english)
    {
        Panel.GivenUser(fixture.WorkspaceId, email, SystemRole.Owner);

        if (!english) return await Panel.SignedInAs(ip, email);

        // The panel's default culture is Persian; an English request has to ask for it before signing in.
        var client = Panel.ClientFrom(ip);
        client.DefaultRequestHeaders.AcceptLanguage.Add(new StringWithQualityHeaderValue("en"));
        var token = await client.AntiforgeryTokenFrom("/account/login");
        await client.PostFormAsync("/account/login", token,
            ("Email", email), ("Password", HarboraWebFactory.TestPassword));
        return client;
    }

    [Fact]
    public async Task The_disk_report_shows_the_dangling_count_and_size_and_says_it_is_an_upper_bound()
    {
        AddLocalServer("dangling-report-host");
        Panel.Docker.SeedDangling(count: 7, bytesEach: 1L << 30);
        try
        {
            var client = await OwnerAsync("dangling-report-owner@example.com", "100.64.77.1", english: true);

            var html = await RenderedTextAsync(await client.GetAsync("/monitoring/disk-report"));

            html.Should().Contain("dangling-report-host");
            html.Should().Contain("Dangling images");
            html.Should().Contain("7 · 7 GB", "the count and the size, per server");
            html.Should().Contain("upper bound",
                "dangling layers can be shared too, so the size is stated as a ceiling, as the total already is");
        }
        finally
        {
            Panel.Docker.ForgetDangling();
        }
    }

    [Fact]
    public async Task The_dangling_line_is_in_Persian_by_default()
    {
        AddLocalServer("dangling-report-host-fa");
        Panel.Docker.SeedDangling(count: 2, bytesEach: 1L << 20);
        try
        {
            var client = await OwnerAsync("dangling-report-owner-fa@example.com", "100.64.77.2", english: false);

            var html = await RenderedTextAsync(await client.GetAsync("/monitoring/disk-report"));

            html.Should().Contain("ایمیج‌های بی‌برچسب");
            html.Should().Contain("2 · 2 MB");
        }
        finally
        {
            Panel.Docker.ForgetDangling();
        }
    }

    [Fact]
    public async Task A_server_whose_dangling_images_could_not_be_read_says_not_examined_and_not_zero()
    {
        AddLocalServer("dangling-report-host-unread");
        var previous = Panel.Docker.DanglingThrows;
        Panel.Docker.DanglingThrows = new Harbora.Application.Abstractions.ImageSweepUnavailableException(
            "this agent is too old to be swept (test)");
        try
        {
            var client = await OwnerAsync("dangling-report-owner-unread@example.com", "100.64.77.3", english: true);

            var html = await RenderedTextAsync(await client.GetAsync("/monitoring/disk-report"));

            html.Should().Contain("this agent is too old to be swept (test)");
            html.Should().Contain("Not examined:");
            html.Should().Contain("does not include the dangling images",
                "the reclaimable total excludes what could not be read, and the page says so above the servers");
        }
        finally
        {
            Panel.Docker.DanglingThrows = previous;
        }
    }

    [Fact]
    public async Task Clean_up_disk_reports_the_dangling_images_it_pruned_with_the_daemons_own_figure()
    {
        AddLocalServer("dangling-cleanup-host");
        Panel.Docker.SeedDangling(count: 7, bytesEach: 1L << 30);
        try
        {
            var client = await OwnerAsync("dangling-cleanup-owner@example.com", "100.64.77.4", english: true);
            var token = await client.AntiforgeryTokenFrom("/monitoring");

            var response = await client.PostFormAsync("/monitoring/cleanup", token);

            response.StatusCode.Should().Be(HttpStatusCode.Found);
            response.RedirectPath().Should().Be("/monitoring");

            var html = await RenderedTextAsync(await client.GetAsync("/monitoring"));
            html.Should().Contain("pruned 7 dangling image(s)");
            html.Should().Contain("the daemon reported 7 GB",
                "the daemon's own figure, labelled as the daemon's, beside the disk's measured 'freed'");
            Panel.Docker.DanglingCount.Should().Be(0);
        }
        finally
        {
            Panel.Docker.ForgetDangling();
        }
    }

    [Fact]
    public async Task The_cleanup_button_no_longer_claims_to_touch_only_build_images()
    {
        var client = await OwnerAsync("dangling-banner-owner@example.com", "100.64.77.5", english: true);

        var html = await RenderedTextAsync(await client.GetAsync("/monitoring"));

        html.Should().Contain("dangling",
            "the button prunes dangling images now, and the words next to it must say what it removes");
        html.Should().NotContain("Only Harbora's own build images");
    }
}
