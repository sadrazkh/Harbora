using FluentAssertions;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The volume screen at <c>apps/{id}/data</c>, and whether it tells the truth about a listing that
/// stopped short.
///
/// <c>CapturingProgress</c> bounds a remote one-off's output at 1 MiB and appends a marker line when
/// it cuts one off. <c>VolumeFileCommands.ParseListing</c> used to treat that marker as just another
/// line it could not make sense of and skip it — so the one screen the whole fix was for still went
/// quiet exactly where it used to. These tests pin the two ends of that guarantee: the screen says so
/// when the marker is there, and does not say so when it is not.
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class AppDataHttpTests(HarboraHttpFixture fixture)
{
    private HarboraWebFactory Panel => fixture.Panel;

    private (Guid AppId, Guid VolumeId) SeedAppWithVolume(string slug)
    {
        var app = new App
        {
            WorkspaceId = fixture.WorkspaceId,
            ServerId = Guid.CreateVersion7(),
            Name = slug,
            Slug = slug,
            Kind = ServiceKind.Web,
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

        return (app.Id, volume.Id);
    }

    [Fact]
    public async Task The_data_screen_says_a_listing_is_incomplete_when_the_helper_truncated_its_output()
    {
        var (appId, _) = SeedAppWithVolume("data-cut-short");

        // What a remote one-off returns once CapturingProgress hits its bound: the lines it kept,
        // then the marker — see CapturingProgress.TruncationMarkerPrefix.
        Panel.Docker.OneOffOutput.Add("f|10|1700000000|kept.txt");
        Panel.Docker.OneOffOutput.Add("... [output truncated: exceeded 1048576 characters]");
        Panel.Docker.OneOffExitCode = 0;

        Panel.GivenUser(fixture.WorkspaceId, "data-cut-short@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.230", "data-cut-short@example.com");

        var html = await (await client.GetAsync($"/apps/{appId}/data")).Content.ReadAsStringAsync();

        html.Should().Contain("data-listing-state=\"truncated\"",
            "the parser saw the truncation marker and the page has to carry that forward");
        html.Should().Contain("kept.txt", "the entries the helper did manage to print are still shown");
        // The panel renders Persian by default in tests — this is the encoded form of "ناقص" ("incomplete").
        html.Should().Contain("&#x646;&#x627;&#x642;&#x635;",
            "the page says the listing is incomplete, in the language it renders by default");
    }

    [Fact]
    public async Task The_data_screen_does_not_say_incomplete_when_the_listing_finished_on_its_own()
    {
        // The other half of the same guarantee: a screen that always claims to be partial is no more
        // honest than one that never does.
        var (appId, _) = SeedAppWithVolume("data-finished");

        Panel.Docker.OneOffOutput.Add("f|10|1700000000|whole.txt");
        Panel.Docker.OneOffExitCode = 0;

        Panel.GivenUser(fixture.WorkspaceId, "data-finished@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.231", "data-finished@example.com");

        var html = await (await client.GetAsync($"/apps/{appId}/data")).Content.ReadAsStringAsync();

        html.Should().Contain("data-listing-state=\"complete\"");
        html.Should().Contain("whole.txt");
        html.Should().NotContain("data-listing-state=\"truncated\"");
        html.Should().NotContain("&#x646;&#x627;&#x642;&#x635;",
            "nothing on this page should call a complete listing incomplete");
    }
}
