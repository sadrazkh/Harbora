using FluentAssertions;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Servers;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// P6 (2026-08-17 app-environment-management design): the door onto a feature that was already
/// built. <c>EnvironmentVariable.AvailableAtBuild</c> flows through the deployment pipeline into
/// <c>DockerBuildRequest.BuildArgs</c> — see <see cref="BuildArgsTests"/> for that half — and
/// <c>AppsController.AddEnv</c> has bound an <c>availableAtBuild</c> parameter since before this
/// change. What did not exist was a control on <c>Views/Apps/Details.cshtml</c>'s add-variable form
/// that could ever set it, so the parameter always bound its default, <c>false</c>.
///
/// <para>
/// This drives the real form over HTTP and reads the row back, rather than asserting on the
/// checkbox's markup — a checkbox that renders but posts under the wrong name would pass a
/// view-only test and still leave every variable created through the panel unable to reach a build.
/// </para>
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class BuildArgCheckboxHttpTests(HarboraHttpFixture fixture)
{
    private HarboraWebFactory Panel => fixture.Panel;

    /// <summary>
    /// A project and environment of its own — uniquely slugged, since this collection fixture is
    /// shared across every test in the class. 5.1 (per-app grants, HARBORA-0035): AddEnv now asks
    /// ProjectAccessService whether the caller reaches this app, and that lookup requires a real
    /// Environment behind EnvironmentId (a required FK per the app-environment-management design) —
    /// an app seeded without one answers "not found" for every caller, Owner included, the same way
    /// <c>CapabilityPolicyHttpTests.GivenApp</c> already seeds one for exactly this reason.
    /// </summary>
    private Guid SeedEnvironment()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var projectId = Guid.CreateVersion7();
        var environmentId = Guid.CreateVersion7();

        Panel.Seed(db =>
        {
            db.Projects.Add(new Harbora.Domain.Projects.Project
            {
                Id = projectId, WorkspaceId = fixture.WorkspaceId, Name = "Shop", Slug = "buildarg-" + suffix
            });
            db.Environments.Add(new Harbora.Domain.Projects.Environment
            {
                Id = environmentId, WorkspaceId = fixture.WorkspaceId, ProjectId = projectId,
                Name = "Production", Slug = "production", IsDefault = true
            });
        });

        return environmentId;
    }

    private App GivenApp(string slug)
    {
        var app = new App
        {
            WorkspaceId = fixture.WorkspaceId,
            ServerId = Guid.CreateVersion7(),
            EnvironmentId = SeedEnvironment(),
            Name = slug,
            Slug = slug,
            Kind = ServiceKind.Web,
            ContainerPort = 8080,
            SourceType = AppSourceType.PrebuiltImage,
            PrebuiltImage = "ghcr.io/example/seeded:1.0",
            Status = AppStatus.Running
        };
        Panel.Seed(db => db.Apps.Add(app));
        return app;
    }

    [Fact]
    public async Task Ticking_the_build_checkbox_sets_available_at_build_on_the_saved_variable()
    {
        var app = GivenApp("buildarg-on");
        Panel.GivenUser(fixture.WorkspaceId, "buildarg-on@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.230", "buildarg-on@example.com");

        var token = await client.AntiforgeryTokenFrom($"/apps/details/{app.Id}");
        var response = await client.PostFormAsync($"/apps/{app.Id}/env", token,
            ("key", "BUILD_FLAG"), ("value", "yes"), ("availableAtBuild", "true"));

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.Found);
        var saved = Panel.Read(db => db.EnvironmentVariables
            .Single(e => e.AppId == app.Id && e.Key == "BUILD_FLAG"));
        saved.AvailableAtBuild.Should().BeTrue(
            "the checkbox posts availableAtBuild=true, and the controller has bound that parameter all along");
    }

    /// <summary>The default the form has always produced, still honest after the checkbox exists:
    /// leaving it unticked must not silently mark a variable available at build.</summary>
    [Fact]
    public async Task Leaving_the_build_checkbox_unticked_leaves_the_variable_runtime_only()
    {
        var app = GivenApp("buildarg-off");
        Panel.GivenUser(fixture.WorkspaceId, "buildarg-off@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.231", "buildarg-off@example.com");

        var token = await client.AntiforgeryTokenFrom($"/apps/details/{app.Id}");
        var response = await client.PostFormAsync($"/apps/{app.Id}/env", token,
            ("key", "RUNTIME_ONLY"), ("value", "yes"));

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.Found);
        var saved = Panel.Read(db => db.EnvironmentVariables
            .Single(e => e.AppId == app.Id && e.Key == "RUNTIME_ONLY"));
        saved.AvailableAtBuild.Should().BeFalse();
    }

    [Fact]
    public async Task The_apps_page_renders_a_build_checkbox_in_the_add_variable_form()
    {
        var app = GivenApp("buildarg-form");
        Panel.GivenUser(fixture.WorkspaceId, "buildarg-form@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.232", "buildarg-form@example.com");

        var html = await (await client.GetAsync($"/apps/details/{app.Id}")).Content.ReadAsStringAsync();

        html.Should().Contain("data-build-arg-checkbox");
        html.Should().Contain("name=\"availableAtBuild\"");
    }
}
