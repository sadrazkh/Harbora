using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using Harbora.Domain.Apps;
using Harbora.Domain.Authorization;
using Harbora.Domain.Common;
using Harbora.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Sub-project D4, Task 2: a button on the data tab that mints a temporary download link, and the
/// route that redeems it — the one route in the whole panel that is deliberately reachable by a
/// client that has never signed in, because a link that needed a session would not answer the
/// request. Everything that makes that acceptable was proved in Task 1
/// (<c>VolumeDownloadTokenTests</c>); these tests prove the HTTP surface built on top of it.
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class VolumeDownloadLinkHttpTests(HarboraHttpFixture fixture)
{
    private HarboraWebFactory Panel => fixture.Panel;

    private static readonly Regex LinkAttribute =
        new("data-download-link=\"(?<link>[^\"]+)\"", RegexOptions.Compiled);
    private static readonly Regex ExpiryAttribute =
        new("data-download-link-expires=\"(?<expires>[^\"]*)\"", RegexOptions.Compiled);

    private (App App, Volume Volume) SeedAppWithVolume(string slug, Guid? workspaceId = null)
    {
        var app = new App
        {
            WorkspaceId = workspaceId ?? fixture.WorkspaceId,
            ServerId = Guid.CreateVersion7(),
            Name = slug,
            Slug = slug,
            SourceType = AppSourceType.PrebuiltImage,
            PrebuiltImage = "ghcr.io/example/seeded:1.0",
            Status = AppStatus.Running
        };
        var volume = new Volume { AppId = app.Id, Name = slug + "-data", MountPath = "/data" };

        Panel.Seed(db =>
        {
            db.Apps.Add(app);
            db.Volumes.Add(volume);
        });

        return (app, volume);
    }

    /// <summary>Mints a link through the real form POST and returns what the tab shows for it.</summary>
    private async Task<(string Link, string Token, string ExpiresAt)> MintAsync(
        HttpClient client, Guid appId, Guid volumeId, string path)
    {
        var token = await client.AntiforgeryTokenFrom($"/apps/{appId}/data");

        var response = await client.PostFormAsync($"/apps/{appId}/data/download-link", token,
            ("volumeId", volumeId.ToString()), ("path", path));

        response.StatusCode.Should().Be(HttpStatusCode.Found, "minting redirects back to the data tab");

        var html = await (await client.GetAsync(response.RedirectPath())).Content.ReadAsStringAsync();

        var linkMatch = LinkAttribute.Match(html);
        linkMatch.Success.Should().BeTrue("the tab must show the link it just minted");
        var expiresMatch = ExpiryAttribute.Match(html);
        expiresMatch.Success.Should().BeTrue("the tab must show when the link stops working");

        var link = linkMatch.Groups["link"].Value;
        var slash = link.LastIndexOf('/');
        return (link, link[(slash + 1)..], expiresMatch.Groups["expires"].Value);
    }

    // ---- minting shows the link, when it expires, and that it is one-shot ------------------------

    [Fact]
    public async Task Minting_a_download_link_shows_it_and_its_expiry_on_the_data_tab()
    {
        var (app, volume) = SeedAppWithVolume("dl-mint");
        Panel.GivenUser(fixture.WorkspaceId, "dl-mint@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.240", "dl-mint@example.com");

        var (link, token, expiresAt) = await MintAsync(client, app.Id, volume.Id, "report.csv");

        link.Should().Contain("/dl/", "the redemption route is deliberately outside every app route");
        token.Should().NotBeNullOrWhiteSpace();
        expiresAt.Should().NotBeNullOrWhiteSpace("the tab has to say when the one-shot link stops working");

        var stored = Panel.Read(db => db.VolumeDownloadTokens.Single(t => t.AppId == app.Id));
        stored.AppId.Should().Be(app.Id);
        stored.VolumeId.Should().Be(volume.Id);
        stored.Path.Should().Be("report.csv");
    }

    // ---- the one test that matters most: it works with no panel session at all --------------------

    [Fact]
    public async Task The_minted_link_serves_the_file_to_a_client_that_has_never_signed_in()
    {
        var (app, volume) = SeedAppWithVolume("dl-noauth");
        Panel.GivenUser(fixture.WorkspaceId, "dl-noauth@example.com", SystemRole.Owner);
        var owner = await Panel.SignedInAs("203.0.113.241", "dl-noauth@example.com");

        var (_, token, _) = await MintAsync(owner, app.Id, volume.Id, "report.csv");

        var fileBytes = Encoding.UTF8.GetBytes("the file this link names");
        Panel.Docker.OneOffOutput.Add(Convert.ToBase64String(fileBytes));
        Panel.Docker.OneOffExitCode = 0;

        // A client that has never called SignedInAs — no cookie, no session, nothing. This is the
        // client the feature exists for: somebody with `curl` and a link, not another browser tab.
        var stranger = Panel.ClientFrom("203.0.113.242");

        var response = await stranger.GetAsync($"/dl/{token}");

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "the whole point of the link is that it works without a panel session");
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/octet-stream");
        (await response.Content.ReadAsByteArrayAsync()).Should().Equal(fileBytes);
        response.Content.Headers.ContentDisposition!.FileName!.Trim('"').Should().Be("report.csv");
    }

    [Fact]
    public async Task Redeeming_the_same_link_a_second_time_404s()
    {
        var (app, volume) = SeedAppWithVolume("dl-oneshot");
        Panel.GivenUser(fixture.WorkspaceId, "dl-oneshot@example.com", SystemRole.Owner);
        var owner = await Panel.SignedInAs("203.0.113.243", "dl-oneshot@example.com");

        var (_, token, _) = await MintAsync(owner, app.Id, volume.Id, "report.csv");

        Panel.Docker.OneOffOutput.Add(Convert.ToBase64String(Encoding.UTF8.GetBytes("content")));
        Panel.Docker.OneOffExitCode = 0;

        var stranger = Panel.ClientFrom("203.0.113.244");
        var first = await stranger.GetAsync($"/dl/{token}");
        var second = await stranger.GetAsync($"/dl/{token}");

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        second.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "a shareable link can be forwarded, so 'used once' is what bounds where it ends up");
    }

    [Fact]
    public async Task A_link_past_its_hour_404s()
    {
        var (app, volume) = SeedAppWithVolume("dl-expired");
        Panel.GivenUser(fixture.WorkspaceId, "dl-expired@example.com", SystemRole.Owner);
        var owner = await Panel.SignedInAs("203.0.113.245", "dl-expired@example.com");

        var (_, token, _) = await MintAsync(owner, app.Id, volume.Id, "report.csv");

        // The row existed and was minted correctly; it is only pushed back in time, the same way
        // VolumeBackupHttpTests backdates a completed snapshot to prove "recent" rather than assuming
        // an empty table already reads as "none".
        Panel.Seed(db =>
        {
            var stored = db.VolumeDownloadTokens.Single(t => t.AppId == app.Id);
            stored.CreatedAt -= AdminerSession.Lifetime + TimeSpan.FromMinutes(1);
        });

        var stranger = Panel.ClientFrom("203.0.113.246");
        var response = await stranger.GetAsync($"/dl/{token}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "past the lifetime it is refused whether or not it was ever used");
    }

    [Fact]
    public async Task An_unknown_token_404s_rather_than_leaking_which_reason_it_failed_for()
    {
        var stranger = Panel.ClientFrom("203.0.113.247");

        var response = await stranger.GetAsync("/dl/not-a-real-token-at-all");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "expired, spent and never-existed are one answer to the caller");
    }

    // ---- resolved through the app, never through a volume id read bare off the route --------------

    [Fact]
    public async Task An_app_in_another_workspace_cannot_mint_a_link_for_it()
    {
        var mine = SeedAppWithVolume("dl-cross-mine").App;
        var otherWorkspaceId = Guid.CreateVersion7();
        var (theirs, theirVolume) = SeedAppWithVolume("dl-cross-theirs", otherWorkspaceId);

        Panel.GivenUser(fixture.WorkspaceId, "dl-cross@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.248", "dl-cross@example.com");

        // A token from a page this caller CAN see — antiforgery is tied to the session, not the
        // route, the same way VolumeBackupHttpTests spends one app's token on a neighbouring one.
        var token = await client.AntiforgeryTokenFrom($"/apps/{mine.Id}/data");

        var response = await client.PostFormAsync($"/apps/{theirs.Id}/data/download-link", token,
            ("volumeId", theirVolume.Id.ToString()), ("path", "report.csv"));

        response.StatusCode.Should().NotBe(HttpStatusCode.OK);
        Panel.Read(db => db.VolumeDownloadTokens.IgnoreQueryFilters()
            .Any(t => t.AppId == theirs.Id)).Should().BeFalse(
            "an app in a workspace the caller cannot see must mint nothing, whatever status the refusal reads as");
    }
}
