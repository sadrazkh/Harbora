using System.Net;
using FluentAssertions;
using Harbora.Domain.Common;
using Harbora.Domain.Servers;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// HARBORA-0033's disk-side half, reached the only way it can run: through the booted panel, not
/// <c>AdminCommands</c> — see <c>DiskVolumeOrphanReport</c>'s own class remarks for why it needs the
/// full <c>IServerEngineFactory</c> DI graph rather than the break-glass path
/// <c>VolumeOrphanReportTests</c> exercises directly.
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class ServersDiskVolumeReportHttpTests(HarboraHttpFixture fixture)
{
    private HarboraWebFactory Panel => fixture.Panel;

    private async Task<HttpClient> OwnerClientAsync(string email, string ip)
    {
        Panel.GivenUser(fixture.WorkspaceId, email, SystemRole.Owner);
        return await Panel.SignedInAs(ip, email);
    }

    /// <summary>
    /// A local-flagged server of this test's own, rather than reusing whichever one
    /// <c>DbSeeder</c> or an earlier test in this shared HTTP collection already left behind —
    /// every <c>IsLocal</c> row resolves to the identical shared <see cref="HarboraWebFactory.Docker"/>
    /// fake, but "the" local row is not this test's to assume; other files in this collection add
    /// their own (see e.g. <c>AppSpecificsHttpTests</c>).
    /// </summary>
    private Guid AddLocalServerAsync(string name)
    {
        var server = new Server { Name = name, Hostname = "localhost", IsLocal = true };
        Panel.Seed(db => db.Servers.Add(server));
        return server.Id;
    }

    [Fact]
    public async Task An_orphan_left_on_a_local_servers_disk_is_named_in_the_report()
    {
        // Present on disk, but nobody's app owns it — the shared fake engine every IsLocal row
        // resolves to, so seeding a volume here is exactly "left behind by an unmount". Every other
        // IsLocal-flagged server accumulated in this shared HTTP fixture answers from that identical
        // engine too, so this only proves the orphan is FOUND and NAMED end-to-end through the real
        // route — the per-server scoping itself is proven precisely by DiskVolumeOrphanReportTests,
        // where each server gets a genuinely separate fake engine.
        AddLocalServerAsync("disk-report-host");
        Panel.Docker.SeedVolume("harbora-vol-disk-report-gone-data");

        var client = await OwnerClientAsync("disk-report-owner@example.com", "203.0.113.170");
        var response = await client.GetAsync("/servers/disk-volume-report");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/plain");
        var text = await response.Content.ReadAsStringAsync();

        text.Should().Contain("harbora-vol-disk-report-gone-data", "the orphan must be named, not just counted");
        text.Should().Contain("disk-report-host", "at least the server this test added must be named");
    }

    [Fact]
    public async Task A_server_with_no_agent_endpoint_is_named_rather_than_silently_dropped()
    {
        var stranded = new Server { Name = "disk-report-stranded", Hostname = "10.0.9.9", IsLocal = false };
        Panel.Seed(db => db.Servers.Add(stranded));

        var client = await OwnerClientAsync("disk-report-owner2@example.com", "203.0.113.171");
        var response = await client.GetAsync("/servers/disk-volume-report");

        response.StatusCode.Should().Be(HttpStatusCode.OK, "an unreachable server is reported, not a 500");
        var text = await response.Content.ReadAsStringAsync();

        text.Should().Contain("disk-report-stranded");
        text.Should().Contain("unreachable");
        text.Should().Contain("Servers NOT checked");
    }

    [Fact]
    public async Task A_viewer_may_not_read_the_disk_volume_report()
    {
        Panel.GivenUser(fixture.WorkspaceId, "disk-report-viewer@example.com", SystemRole.Viewer);
        var client = await Panel.SignedInAs("203.0.113.172", "disk-report-viewer@example.com");

        var response = await client.GetAsync("/servers/disk-volume-report");

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.RedirectPath().Should().Be("/account/denied");
    }
}
