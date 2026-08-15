using FluentAssertions;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Deployments;
using Harbora.Domain.Identity;
using Harbora.Domain.Settings;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Sub-project F (doc <c>2026-08-15-rollback-depth-design.md</c>): the Deployments tab marks which
/// history entries <c>DeploymentPlanning.ImagesToPrune</c> has kept an image for — an instant
/// rollback — and which have not, without a stored flag and without a second copy of the pruner's
/// windowing rule.
///
/// <para>
/// The panel renders Persian by default in this harness, so every assertion below reads a
/// <c>data-</c> attribute rather than a sentence in either language — the same reasoning
/// <see cref="AppDetailTabsHttpTests"/> uses for the Rollback href — and the marker is read off one
/// row at a time rather than off the page, so a page-wide flag could not make this pass by accident.
/// </para>
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class DeploymentsRollbackDepthHttpTests(HarboraHttpFixture fixture)
{
    private HarboraWebFactory Panel => fixture.Panel;

    [Fact]
    public async Task A_deployment_inside_the_default_retention_depth_is_marked_instant_and_one_outside_it_is_marked_for_redeploy()
    {
        var appId = SeedAppWithSevenDeployments(Panel, fixture.WorkspaceId, activeNumber: 7);
        Panel.GivenUser(fixture.WorkspaceId, "rollback-depth-default@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.210", "rollback-depth-default@example.com");

        var html = await (await client.GetAsync($"/apps/{appId}/deployments")).Content.ReadAsStringAsync();

        // HarboraRuntimeOptions.ImageRetentionCount defaults to 5: active (#7) plus the newest five
        // rollback targets protects #3 through #7, so #1 and #2 fall outside the window.
        RetentionNoteValue(html).Should().Be("5", "HarboraRuntimeOptions.ImageRetentionCount defaults to 5");
        RowHtml(html, 6).Should().Contain("data-rollback-depth=\"instant\"", "#6 is inside the newest five rollback targets");
        RowHtml(html, 3).Should().Contain("data-rollback-depth=\"instant\"", "#3 is the fifth-newest and still inside the window");
        RowHtml(html, 2).Should().Contain("data-rollback-depth=\"redeploy\"", "#2 fell outside the newest five");
        RowHtml(html, 1).Should().Contain("data-rollback-depth=\"redeploy\"", "#1 fell outside the newest five");
    }

    /// <summary>
    /// The one test the design doc insists on: the marked boundary follows the configured value, not
    /// the literal number 5. Rather than asserting a fixed row, this proves the same deployment (#3)
    /// that reads "instant" at the default depth in the sibling test above reads "needs a redeploy"
    /// once the configured depth narrows — the line itself has to move.
    /// </summary>
    [Fact]
    public async Task A_narrower_configured_retention_moves_the_marked_boundary_down()
    {
        await using var narrow = new HarboraWebFactory(imageRetentionCount: 2);
        var workspaceId = await SeedWorkspaceAndOwnerAsync(narrow, "rollback-depth-narrow@example.com");
        var appId = SeedAppWithSevenDeployments(narrow, workspaceId, activeNumber: 7);
        var client = await narrow.SignedInAs("203.0.113.211", "rollback-depth-narrow@example.com");

        var html = await (await client.GetAsync($"/apps/{appId}/deployments")).Content.ReadAsStringAsync();

        RetentionNoteValue(html).Should().Be("2", "the boundary line must read the configured value, not the default");
        RowHtml(html, 3).Should().Contain("data-rollback-depth=\"redeploy\"", "keep=2 no longer reaches #3 — it did at keep=5");
        RowHtml(html, 6).Should().Contain("data-rollback-depth=\"instant\"", "#6 is one of the newest two rollback targets");
    }

    // ---- fixtures --------------------------------------------------------------------------------

    /// <summary>Seven succeeded deployments on distinct images, numbered 1..7, so both a keep=5 and a
    /// keep=2 window carve a different, checkable line through the same history.</summary>
    private static Guid SeedAppWithSevenDeployments(HarboraWebFactory panel, Guid workspaceId, int activeNumber)
    {
        var app = new App
        {
            WorkspaceId = workspaceId,
            ServerId = Guid.CreateVersion7(),
            Name = "rollback-depth-app",
            Slug = "rollback-depth-app-" + Guid.NewGuid().ToString("N")[..8],
            SourceType = AppSourceType.PrebuiltImage,
            PrebuiltImage = "ghcr.io/example/rollback-depth:1.0",
            Status = AppStatus.Running
        };

        var deployments = Enumerable.Range(1, 7).Select(n => new Deployment
        {
            AppId = app.Id,
            WorkspaceId = workspaceId,
            Number = n,
            Status = DeploymentStatus.Succeeded,
            Trigger = DeploymentTrigger.Manual,
            TriggeredByUserId = Guid.CreateVersion7(),
            ImageTag = $"harbora/{app.Slug}:build-{n}"
        }).ToList();

        app.ActiveDeploymentId = deployments.Single(d => d.Number == activeNumber).Id;

        panel.Seed(db =>
        {
            db.Apps.Add(app);
            db.Deployments.AddRange(deployments);
        });

        return app.Id;
    }

    /// <summary>
    /// A freshly booted panel has no workspace and has never been set up — <see cref="HarboraHttpFixture"/>
    /// does that seeding for the shared collection panel, but the boundary-move test owns a panel of
    /// its own (it needs its own configured <c>ImageRetentionCount</c>), the same reason
    /// <c>SetupGuardHttpTests</c> and <c>RailLayoutHttpTests</c> seed by hand instead of using the fixture.
    /// </summary>
    private static async Task<Guid> SeedWorkspaceAndOwnerAsync(HarboraWebFactory panel, string email)
    {
        var workspaceId = Guid.CreateVersion7();

        panel.Seed(db =>
        {
            var planId = db.Plans.Where(p => p.IsDefault).Select(p => p.Id).FirstOrDefault();

            db.Workspaces.Add(new Workspace
            {
                Id = workspaceId,
                Name = "Harbora",
                Slug = "harbora-rollback-depth-" + workspaceId.ToString("N")[..8],
                IsDefault = true,
                PlanId = planId == Guid.Empty ? null : planId
            });

            db.Settings.Add(new Setting { Key = SettingKeys.SetupCompleted, Value = "true" });
        });

        panel.GivenUser(workspaceId, email, SystemRole.Owner);
        await Task.CompletedTask;
        return workspaceId;
    }

    /// <summary>The rendered slice for one deployment row, cut from its own <c>data-deployment-number</c>
    /// marker up to the next one — so an assertion on it cannot accidentally match a neighbouring row.</summary>
    private static string RowHtml(string html, int number)
    {
        var marker = $"data-deployment-number=\"{number}\"";
        var start = html.IndexOf(marker, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, $"deployment #{number}'s row must be on the page");
        var next = html.IndexOf("data-deployment-number=\"", start + marker.Length, StringComparison.Ordinal);
        return next < 0 ? html[start..] : html[start..next];
    }

    private static string RetentionNoteValue(string html)
    {
        const string marker = "data-image-retention-count=\"";
        var start = html.IndexOf(marker, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, "the boundary note must carry the configured depth");
        start += marker.Length;
        var end = html.IndexOf('"', start);
        return html[start..end];
    }
}
