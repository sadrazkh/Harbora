using System.Net;
using FluentAssertions;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// HARBORA-0033 at the HTTP boundary: the single-volume delete and the app delete, both reached the
/// way a person actually reaches them — through the panel's own routes, its own antiforgery token,
/// its own <see cref="Fakes.FakeDockerEngine"/> standing in for the node.
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class VolumeProtectionHttpTests(HarboraHttpFixture fixture)
{
    private HarboraWebFactory Panel => fixture.Panel;

    private (App App, Volume Volume) SeedAppWithVolume(string slug, bool isProtected)
    {
        var app = new App
        {
            WorkspaceId = fixture.WorkspaceId, EnvironmentId = fixture.DefaultEnvironmentId,
            ServerId = Guid.CreateVersion7(), Name = slug, Slug = slug,
            SourceType = AppSourceType.PrebuiltImage, PrebuiltImage = "ghcr.io/example/" + slug + ":1.0"
        };
        var volume = new Volume
        {
            AppId = app.Id, Name = slug + "-data", MountPath = "/data", Protected = isProtected
        };
        Panel.Seed(db =>
        {
            db.Apps.Add(app);
            db.Volumes.Add(volume);
        });
        return (app, volume);
    }

    // ---- single-volume delete (AppsController.RemoveVolume) ----

    [Fact]
    public async Task Deleting_data_from_a_protected_volume_is_refused_and_the_volume_survives()
    {
        var (app, volume) = SeedAppWithVolume("protected-remove", isProtected: true);
        Panel.GivenUser(fixture.WorkspaceId, "protected-remove@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.150", "protected-remove@example.com");

        var token = await client.AntiforgeryTokenFrom($"/apps/{app.Id}/volumes");
        var response = await client.PostFormAsync(
            $"/apps/{app.Id}/volumes/{volume.Id}/remove", token, ("deleteData", "true"));

        response.RedirectPath().Should().Be($"/apps/{app.Id}/volumes");
        Panel.Docker.OperationsOn(volume.Name).Should().NotContain("RemoveVolumeAsync",
            "a Protected volume's data must never reach the docker engine's removal call");
        Panel.Read(db => db.Volumes.Any(v => v.Id == volume.Id)).Should().BeTrue(
            "the volume row must still exist — the delete was refused, not half-applied");
    }

    [Fact]
    public async Task Unmounting_a_protected_volume_without_asking_for_its_data_still_works()
    {
        // Protected blocks destroying data, not detaching. This is the non-destructive half of the
        // same action, and it must not be swept into the refusal by mistake.
        var (app, volume) = SeedAppWithVolume("protected-unmount-only", isProtected: true);
        Panel.GivenUser(fixture.WorkspaceId, "protected-unmount-only@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.151", "protected-unmount-only@example.com");

        var token = await client.AntiforgeryTokenFrom($"/apps/{app.Id}/volumes");
        var response = await client.PostFormAsync($"/apps/{app.Id}/volumes/{volume.Id}/remove", token);

        response.RedirectPath().Should().Be($"/apps/{app.Id}/volumes");
        Panel.Docker.OperationsOn(volume.Name).Should().NotContain("RemoveVolumeAsync");
        Panel.Read(db => db.Volumes.Any(v => v.Id == volume.Id)).Should().BeFalse(
            "the row is gone — only its data was ever protected from deletion");
    }

    [Fact]
    public async Task Deleting_data_from_an_unprotected_volume_still_works()
    {
        var (app, volume) = SeedAppWithVolume("unprotected-remove", isProtected: false);
        Panel.GivenUser(fixture.WorkspaceId, "unprotected-remove@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.152", "unprotected-remove@example.com");

        var token = await client.AntiforgeryTokenFrom($"/apps/{app.Id}/volumes");
        var response = await client.PostFormAsync(
            $"/apps/{app.Id}/volumes/{volume.Id}/remove", token, ("deleteData", "true"));

        response.RedirectPath().Should().Be($"/apps/{app.Id}/volumes");
        Panel.Docker.OperationsOn(volume.Name).Should().Contain("RemoveVolumeAsync");
        Panel.Read(db => db.Volumes.Any(v => v.Id == volume.Id)).Should().BeFalse();
    }

    /// <summary>
    /// The pre-existing defect this whole feature is closing, reproduced directly: a Docker removal
    /// that throws used to be logged and then reported to the person as "Deleted" anyway.
    /// </summary>
    [Fact]
    public async Task A_docker_removal_that_fails_is_reported_as_failed_not_as_deleted()
    {
        var (app, volume) = SeedAppWithVolume("removal-fails", isProtected: false);
        Panel.Docker.UnremovableVolumes.Add(volume.Name);
        Panel.GivenUser(fixture.WorkspaceId, "removal-fails@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.153", "removal-fails@example.com");

        var token = await client.AntiforgeryTokenFrom($"/apps/{app.Id}/volumes");
        var response = await client.PostFormAsync(
            $"/apps/{app.Id}/volumes/{volume.Id}/remove", token, ("deleteData", "true"));

        response.RedirectPath().Should().Be($"/apps/{app.Id}/volumes");
        var html = await (await client.GetAsync(response.RedirectPath())).Content.ReadAsStringAsync();
        html.Should().Contain("bg-danger-soft",
            "a data deletion that failed must render as an error, not the ordinary success message");
    }

    // ---- protecting / unprotecting (AppsController.SetVolumeProtection) ----

    [Fact]
    public async Task Turning_protection_on_then_off_is_reflected_in_the_database()
    {
        var (app, volume) = SeedAppWithVolume("toggle", isProtected: false);
        Panel.GivenUser(fixture.WorkspaceId, "toggle@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.154", "toggle@example.com");

        var onToken = await client.AntiforgeryTokenFrom($"/apps/{app.Id}/volumes");
        await client.PostFormAsync($"/apps/{app.Id}/volumes/{volume.Id}/protect", onToken, ("protect", "true"));
        Panel.Read(db => db.Volumes.Single(v => v.Id == volume.Id).Protected).Should().BeTrue();

        var offToken = await client.AntiforgeryTokenFrom($"/apps/{app.Id}/volumes");
        await client.PostFormAsync($"/apps/{app.Id}/volumes/{volume.Id}/protect", offToken, ("protect", "false"));
        Panel.Read(db => db.Volumes.Single(v => v.Id == volume.Id).Protected).Should().BeFalse();
    }

    [Fact]
    public async Task The_volumes_tab_shows_the_protected_badge_and_no_delete_data_checkbox_for_a_protected_volume()
    {
        var (app, _) = SeedAppWithVolume("badge-check", isProtected: true);
        Panel.GivenUser(fixture.WorkspaceId, "badge-check@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.155", "badge-check@example.com");

        var html = await (await client.GetAsync($"/apps/{app.Id}/volumes")).Content.ReadAsStringAsync();

        html.Should().Contain("data-volume-protected=\"true\"");
        html.Should().NotContain("name=\"deleteData\"",
            "a protected volume must not offer the control that would ask to destroy its data");
    }

    // ---- whole-app delete (AppsController.Delete) ----

    [Fact]
    public async Task Deleting_an_app_with_a_protected_volume_and_removeVolumes_is_refused_and_the_app_survives()
    {
        var (app, volume) = SeedAppWithVolume("app-delete-protected", isProtected: true);
        Panel.GivenUser(fixture.WorkspaceId, "app-delete-protected@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.156", "app-delete-protected@example.com");

        var token = await client.AntiforgeryTokenFrom($"/apps/details/{app.Id}");
        var response = await client.PostFormAsync($"/apps/delete/{app.Id}", token, ("removeVolumes", "true"));

        // Conventional routing, not attribute routing: AppsController carries no [Route] of its own,
        // so RedirectToAction generates the URL from the literal C# names (nameof(Details) -> "Details"),
        // and ASP.NET Core does not lowercase route-generated URLs by default. Route MATCHING is
        // case-insensitive, so a browser following this redirect reaches the same page either way.
        response.RedirectPath().Should().Be($"/Apps/Details/{app.Id}",
            "a refused delete must land back on the app, not on the (still populated) list");
        Panel.Read(db => db.Apps.IgnoreQueryFilters().Any(a => a.Id == app.Id)).Should().BeTrue(
            "the app must still be there — nothing about the delete was allowed to proceed");
        Panel.Docker.OperationsOn(volume.Name).Should().BeEmpty(
            "the guard refuses before the container is even resolved, so the volume's data was never touched");
    }

    [Fact]
    public async Task Deleting_an_app_with_a_protected_volume_but_not_asking_for_removeVolumes_still_works()
    {
        var (app, _) = SeedAppWithVolume("app-delete-keep-data", isProtected: true);
        Panel.GivenUser(fixture.WorkspaceId, "app-delete-keep-data@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.157", "app-delete-keep-data@example.com");

        var token = await client.AntiforgeryTokenFrom($"/apps/details/{app.Id}");
        var response = await client.PostFormAsync($"/apps/delete/{app.Id}", token);

        response.RedirectPath().Should().Be("/Apps",
            "with no data destruction requested, Protected has nothing to refuse");
        Panel.Read(db => db.Apps.IgnoreQueryFilters().Any(a => a.Id == app.Id)).Should().BeFalse();
    }
}
