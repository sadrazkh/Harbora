using FluentAssertions;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Deployments;
using Harbora.Domain.Networking;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// One claim, made before <c>Views/Apps/Details.cshtml</c> is split into tabs across Tasks 2-5: every
/// landmark a customer can see on that 914-line page today is still reachable afterwards.
///
/// <para>
/// A pure refactor is exactly the failure mode this guards against: a section is carried into no tab
/// and no test fails, because nothing ever claimed it was there in the first place. This test is the
/// claim. It is green from the moment it is written — it only describes what already exists — and its
/// value is entirely in what happens later: each assertion below must be moved to point at whichever
/// tab its landmark lands on, and forgetting to move one is the signal that the split dropped it.
/// </para>
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class AppDetailTabsHttpTests(HarboraHttpFixture fixture)
{
    private HarboraWebFactory Panel => fixture.Panel;

    [Fact]
    public async Task The_app_page_still_shows_everything_it_showed_before_the_split()
    {
        var appId = SeedAppWithEverything();
        Panel.GivenUser(fixture.WorkspaceId, "app-detail-owner@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.180", "app-detail-owner@example.com");

        var html = await (await client.GetAsync($"/apps/details/{appId}")).Content.ReadAsStringAsync();

        html.Should().Contain("SEEDED_ENV_KEY", "environment variables are on the page today");
        html.Should().Contain("seeded.example.com", "domains are on the page today");
        html.Should().Contain("/data/seeded", "the volume's mount path is on the page today");
        // The rollback link's label is @T["Rollback"] — translated, and this panel's default
        // language is Persian — so the English word would never appear on the page. The route it
        // points at is not translated, and only renders for a succeeded, non-active deployment, so
        // it names the same feature without asserting on a string a Persian UI would never render.
        html.Should().Contain("/apps/confirmrollback/", "a succeeded deployment offers rollback today");
    }

    /// <summary>
    /// One app carrying one of each landmark the assertions above check for: an environment variable,
    /// a domain, a volume, and a deployment that has succeeded (so the rollback link the deployment
    /// list draws for a non-active successful deployment actually renders).
    /// </summary>
    private Guid SeedAppWithEverything()
    {
        var app = new App
        {
            WorkspaceId = fixture.WorkspaceId,
            ServerId = Guid.CreateVersion7(),
            Name = "seeded-app",
            Slug = "seeded-app",
            SourceType = AppSourceType.PrebuiltImage,
            PrebuiltImage = "ghcr.io/example/seeded:1.0",
            Status = AppStatus.Running
        };

        Panel.Seed(db =>
        {
            db.Apps.Add(app);

            db.EnvironmentVariables.Add(new EnvironmentVariable
            {
                AppId = app.Id,
                Key = "SEEDED_ENV_KEY",
                Value = "seeded-value",
                IsSecret = false
            });

            db.Domains.Add(new DomainName
            {
                AppId = app.Id,
                Host = "seeded.example.com"
            });

            db.Volumes.Add(new Volume
            {
                AppId = app.Id,
                Name = "seeded-volume",
                MountPath = "/data/seeded"
            });

            // Succeeded and not the app's ActiveDeploymentId (left null here), which is exactly the
            // condition the view's deployment list uses to decide whether to draw a Rollback link.
            db.Deployments.Add(new Deployment
            {
                AppId = app.Id,
                WorkspaceId = fixture.WorkspaceId,
                Number = 1,
                Status = DeploymentStatus.Succeeded,
                Trigger = DeploymentTrigger.Manual,
                TriggeredByUserId = Guid.CreateVersion7()
            });
        });

        return app.Id;
    }
}
