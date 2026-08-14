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
        var (appId, _) = SeedAppWithEverything();
        Panel.GivenUser(fixture.WorkspaceId, "app-detail-owner@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.180", "app-detail-owner@example.com");

        var html = await (await client.GetAsync($"/apps/details/{appId}")).Content.ReadAsStringAsync();

        html.Should().Contain("SEEDED_ENV_KEY", "environment variables are on the page today");
        html.Should().Contain("seeded.example.com", "domains are on the page today");
        // The volume's mount path moved to the Volumes tab in Task 4 — see
        // The_volumes_tab_lists_the_apps_storage_and_can_still_add_and_remove — and Overview no
        // longer loads the Volumes collection at all, so it is not asserted here any more.
        // The deployment history, and the rollback link it offers, moved to the Deployments tab in
        // Task 5 — see The_deployments_tab_keeps_the_history_and_the_way_back — and Overview no
        // longer loads the Deployments collection at all, so it is not asserted here any more.
    }

    /// <summary>
    /// The shell built in Task 2 draws a strip with a link to every other tab, on the very page
    /// (<c>/apps/details/{id}</c>) that used to be the whole story. Nothing has moved out of Overview
    /// yet — Tasks 3-5 do that — so this only checks the strip itself is there and points somewhere.
    /// </summary>
    [Fact]
    public async Task Every_tab_of_an_app_is_reachable_from_the_page_itself()
    {
        var (appId, _) = SeedAppWithEverything();
        Panel.GivenUser(fixture.WorkspaceId, "app-tabs-owner@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.181", "app-tabs-owner@example.com");

        var html = await (await client.GetAsync($"/apps/details/{appId}")).Content.ReadAsStringAsync();

        // Route fragments rather than the tab labels: the labels are the isFa ternary's Persian
        // strings by default in this panel (same reasoning as the Rollback assertion above), so the
        // untranslated href is what identifies each tab regardless of which language rendered it.
        html.Should().Contain($"/apps/{appId}/usage", "the Usage tab must be reachable from Overview");
        html.Should().Contain($"/apps/{appId}/volumes", "the Volumes tab must be reachable from Overview");
        html.Should().Contain($"/apps/{appId}/deployments", "the Deployments tab must be reachable from Overview");
    }

    /// <summary>
    /// The Usage tab draws what used to be Overview's "Resources" panel: CPU, memory and disk against
    /// their limits, plus the same figures charted over time.
    ///
    /// <para>
    /// Task 1's preservation test has no usage-related assertion to move here — its four landmarks
    /// are the environment variable, the domain, the volume's mount path, and the rollback link; none
    /// of them are CPU/memory/disk. That is not a landmark this move dropped, since nothing ever
    /// claimed the figures were on the page in the first place (seeding a <c>MonitoringMetric</c>
    /// row was never part of <c>SeedAppWithEverything</c>). This test is written fresh instead,
    /// against the panel's default culture, the same way Task 1 settled on the rollback href rather
    /// than the translated word.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_usage_tab_shows_what_the_overview_used_to()
    {
        var (appId, _) = SeedAppWithEverything();
        Panel.GivenUser(fixture.WorkspaceId, "app-usage-owner@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.182", "app-usage-owner@example.com");

        var usage = await (await client.GetAsync($"/apps/{appId}/usage")).Content.ReadAsStringAsync();

        // Not the visible "CPU" label — the metrics-chart island's own data-name="cpu.percent" is
        // the one piece of this row that is neither translated nor differently cased, the same
        // reasoning Task 1 used for the rollback href over the translated word "Rollback".
        usage.Should().Contain("cpu", "the usage tab is where consumption lives now");
    }

    /// <summary>
    /// The Volumes tab draws what used to be Overview's "Persistent storage" panel: the mounted
    /// paths, and the forms that add or remove one.
    ///
    /// <para>
    /// Task 1's preservation test asserted the seeded volume's mount path on the Overview page.
    /// That assertion moves here rather than staying, because Overview no longer loads the Volumes
    /// collection at all — this tab's own query is now the only place that happens.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_volumes_tab_lists_the_apps_storage_and_can_still_add_and_remove()
    {
        var (appId, volumeId) = SeedAppWithEverything();
        Panel.GivenUser(fixture.WorkspaceId, "app-volumes-owner@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.184", "app-volumes-owner@example.com");

        var html = await (await client.GetAsync($"/apps/{appId}/volumes")).Content.ReadAsStringAsync();

        html.Should().Contain("/data/seeded", "the seeded mount path is this tab's subject");
        // Not the translated button labels ("Add storage" / "Unmount") — this panel's default
        // language is Persian, the same reasoning the Rollback and Usage assertions above use. The
        // Add form's field name and the Remove form's own route (which nothing else on this tab
        // renders) identify each form regardless of which language rendered the page.
        html.Should().Contain("name=\"mountPath\"", "adding storage must not be lost in the move");
        html.Should().Contain($"/apps/{appId}/volumes/{volumeId}/remove", "nor removing it");
    }

    /// <summary>
    /// The Deployments tab draws what used to be Overview's "Deployments" panel: the release
    /// history, and the rollback link a succeeded, inactive entry has always offered.
    ///
    /// <para>
    /// Task 1's preservation test asserted the rollback route on the Overview page. That assertion
    /// moves here rather than staying, because Overview no longer loads the Deployments collection
    /// at all — this tab's own query is now the only place that happens.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_deployments_tab_keeps_the_history_and_the_way_back()
    {
        var (appId, _) = SeedAppWithEverything();
        Panel.GivenUser(fixture.WorkspaceId, "app-deployments-owner@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.185", "app-deployments-owner@example.com");

        var html = await (await client.GetAsync($"/apps/{appId}/deployments")).Content.ReadAsStringAsync();

        // Not the translated word "Rollback" — this panel's default language is Persian, the same
        // reasoning Task 1 used. The route it points at is not translated, and only renders for a
        // succeeded, non-active deployment, so it names the same feature without asserting on a
        // string a Persian UI would never render.
        html.Should().Contain("/apps/confirmrollback/", "rollback is the reason this history is kept");
    }

    /// <summary>
    /// Every tab is a new entry point, and each one is a new chance to forget the ownership check
    /// that <c>AppsController.LoadHeaderAsync</c> exists to make impossible to skip.
    /// </summary>
    [Fact]
    public async Task A_tab_of_another_workspaces_app_is_not_found_rather_than_shown()
    {
        var foreignApp = new App
        {
            WorkspaceId = Guid.CreateVersion7(),
            ServerId = Guid.CreateVersion7(),
            Name = "not-yours",
            Slug = "not-yours",
            SourceType = AppSourceType.PrebuiltImage,
            PrebuiltImage = "ghcr.io/example/not-yours:1.0"
        };
        Panel.Seed(db => db.Apps.Add(foreignApp));
        Panel.GivenUser(fixture.WorkspaceId, "app-usage-foreign@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.183", "app-usage-foreign@example.com");

        (await client.GetAsync($"/apps/{foreignApp.Id}/usage")).StatusCode
            .Should().Be(System.Net.HttpStatusCode.NotFound);
    }

    /// <summary>
    /// One app carrying one of each landmark the assertions above check for: an environment variable,
    /// a domain, a volume, and a deployment that has succeeded (so the rollback link the deployment
    /// list draws for a non-active successful deployment actually renders).
    /// </summary>
    /// <returns>The app's id, and the seeded volume's id (the Volumes tab test needs both).</returns>
    private (Guid AppId, Guid VolumeId) SeedAppWithEverything()
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

        var volume = new Volume
        {
            AppId = app.Id,
            Name = "seeded-volume",
            MountPath = "/data/seeded"
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

            db.Volumes.Add(volume);

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

        return (app.Id, volume.Id);
    }
}
