using System.Net;
using FluentAssertions;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Features;
using Harbora.Domain.Functions;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The rendered half of defect 3: <c>FunctionRollbackPublishFlagTests</c> proves the real
/// <c>DeploymentPipeline</c> marks a rolled-back app's functions unpublished again
/// (<c>DeploymentPipeline.MarkFunctionsRolledBackAsync</c>); this proves the page a person actually
/// looks at renders that state honestly rather than the flattering one, on both screens that carry the
/// chip. Asserted on <c>data-fn-state</c> rather than the word "live" or "unpublished" — the panel
/// renders Persian by default in tests, and that attribute is the one thing on the chip that is
/// neither translated nor a colour a screenshot diff would have to interpret.
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class FunctionRollbackChipHttpTests(HarboraHttpFixture fixture)
{
    private HarboraWebFactory Panel => fixture.Panel;

    private Guid GivenFunctionApp(string slug, bool hasUnpublishedChanges)
    {
        var appId = Guid.CreateVersion7();

        Panel.Seed(db =>
        {
            db.FeatureGrants.Add(new FeatureGrant
            {
                Scope = FeatureScope.Workspace, TargetId = fixture.WorkspaceId,
                FeatureKey = PlatformFeatures.Functions, State = FeatureState.Enabled
            });

            db.Apps.Add(new App
            {
                Id = appId, WorkspaceId = fixture.WorkspaceId, EnvironmentId = fixture.DefaultEnvironmentId,
                Name = slug, Slug = slug, SourceType = AppSourceType.InlineCode,
                FunctionRuntime = FunctionRuntime.JavaScript,
                DockerfilePath = "Dockerfile.harbora",
                // A real rollback also leaves ActiveDeploymentId pointing at a real (rolled-back-to)
                // deployment — non-null is what makes the app "ever published" for the list chip.
                ActiveDeploymentId = Guid.CreateVersion7()
            });

            db.FunctionDefinitions.Add(new FunctionDefinition
            {
                AppId = appId, WorkspaceId = fixture.WorkspaceId,
                Name = "Hello", Slug = "hello-" + slug, Trigger = FunctionTrigger.Http,
                Code = "export default async () => ({ hello: 'world' });",
                // What DeploymentPipeline.MarkFunctionsRolledBackAsync leaves behind: nobody edited
                // this row, but it is no longer a fact that the running container was built from it.
                HasUnpublishedChanges = hasUnpublishedChanges
            });
        });

        return appId;
    }

    [Fact]
    public async Task The_app_page_shows_a_rolled_back_functions_row_as_unpublished_not_live()
    {
        var appId = GivenFunctionApp("fn-chip-rolled-back", hasUnpublishedChanges: true);
        Panel.GivenUser(fixture.WorkspaceId, "fn-chip-owner@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.160", "fn-chip-owner@example.com");

        var response = await client.GetAsync($"/functions/{appId}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var html = await response.Content.ReadAsStringAsync();
        html.Should().Contain("data-fn-state=\"unpublished\"",
            "a rollback never rebuilds, so these rows are not a fact about what the container is running");
        html.Should().NotContain("data-fn-state=\"live\"",
            "the chip must not call stale code live just because nobody has edited it since the rollback");
    }

    [Fact]
    public async Task The_functions_list_still_shows_a_freshly_published_app_as_live()
    {
        var appId = GivenFunctionApp("fn-chip-fresh", hasUnpublishedChanges: false);
        Panel.GivenUser(fixture.WorkspaceId, "fn-chip-fresh-owner@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.161", "fn-chip-fresh-owner@example.com");

        var html = await (await client.GetAsync("/functions")).Content.ReadAsStringAsync();

        html.Should().Contain("data-fn-state=\"live\"",
            "the fix must not make an honestly-published app read as unpublished");
        _ = appId;
    }
}
