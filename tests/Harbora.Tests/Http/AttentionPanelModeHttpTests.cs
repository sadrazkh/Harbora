using FluentAssertions;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Deployments;
using Harbora.Domain.Identity;

using Xunit;

namespace Harbora.Tests;

/// <summary>
/// How the dashboard's "things need you" block behaves in Simple mode.
///
/// <para>
/// Do-not-change item 23 is the PanelMode fold-never-remove principle. Simple folds the verbatim
/// technical line under each item — an exit code, npm output — and never removes it: the human
/// sentence above it and the action button beside it carry the story, and that button goes to the
/// log the folded line is the first line of.
/// </para>
///
/// <para>
/// Folded at every level, Critical included. Exempting Critical was the first design and the data
/// killed it: in <c>Attention.Build</c> every item carrying <c>DetailText</c> is Critical except one
/// certificate case, so the exemption would have left a fold that almost never folded.
/// </para>
///
/// <para>
/// Every assertion reads <c>data-detail</c> rather than a sentence: the panel renders Persian by
/// default in this harness, so an assertion on wording would only be true under English.
/// </para>
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class AttentionPanelModeHttpTests(HarboraHttpFixture fixture)
{
    private HarboraWebFactory Panel => fixture.Panel;

    /// <summary>
    /// An app whose most recent deployment failed with a build error — the case that puts a verbatim
    /// technical line on the dashboard in the first place.
    /// </summary>
    private void GivenAFailedDeployment(string slug, string error)
    {
        var appId = Guid.CreateVersion7();
        var deploymentId = Guid.CreateVersion7();

        Panel.Seed(db =>
        {
            db.Apps.Add(new App
            {
                Id = appId, WorkspaceId = fixture.WorkspaceId, EnvironmentId = fixture.DefaultEnvironmentId,
                Name = slug, Slug = slug, Kind = ServiceKind.Web,
                SourceType = AppSourceType.PrebuiltImage, PrebuiltImage = "ghcr.io/example/" + slug + ":1.0",
                Status = AppStatus.Failed
            });
            db.Deployments.Add(new Deployment
            {
                Id = deploymentId, AppId = appId, WorkspaceId = fixture.WorkspaceId,
                Number = 6, Status = DeploymentStatus.Failed, Trigger = DeploymentTrigger.Manual,
                TriggeredByUserId = Guid.CreateVersion7(),
                StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                FinishedAt = DateTimeOffset.UtcNow.AddMinutes(-4),
                ErrorMessage = error
            });
        });
    }

    /// <summary>
    /// <c>GivenUser</c> has no panel-mode parameter, so the row is written first and updated here —
    /// through the factory's own unscoped context, the same route every other test uses to arrange
    /// state a controller will later read.
    /// </summary>
    private void SetMode(Guid userId, PanelMode mode) =>
        Panel.Seed(db => db.Users.First(u => u.Id == userId).PanelMode = mode);

    [Fact]
    public async Task Simple_mode_folds_the_verbatim_technical_line_rather_than_dropping_it()
    {
        const string email = "attention-simple@example.com";
        var user = Panel.GivenUser(fixture.WorkspaceId, email, SystemRole.Owner);
        SetMode(user.Id, PanelMode.Simple);
        GivenAFailedDeployment("attention-simple-app", "npm ERR! missing script: build");

        var client = await Panel.SignedInAs("203.0.113.70", email);
        var html = await client.GetStringAsync("/");

        html.Should().Contain("data-detail=\"folded\"",
            "Simple hides the build log behind one click — it never removes it (do-not-change item 23)");
        html.Should().Contain("npm ERR! missing script: build",
            "folded means still in the document: a reader who opens the disclosure must find it there");
    }

    [Fact]
    public async Task Advanced_mode_leaves_the_technical_line_open()
    {
        const string email = "attention-advanced@example.com";
        var user = Panel.GivenUser(fixture.WorkspaceId, email, SystemRole.Owner);
        SetMode(user.Id, PanelMode.Advanced);
        GivenAFailedDeployment("attention-advanced-app", "npm ERR! missing script: build");

        var client = await Panel.SignedInAs("203.0.113.71", email);
        var html = await client.GetStringAsync("/");

        html.Should().Contain("data-detail=\"open\"");
        html.Should().NotContain("data-detail=\"folded\"");
    }

    [Fact]
    public async Task The_mute_control_is_gone_because_nothing_can_mute_anything_yet()
    {
        const string email = "attention-mute@example.com";
        Panel.GivenUser(fixture.WorkspaceId, email, SystemRole.Owner);
        GivenAFailedDeployment("attention-mute-app", "exit 1");

        var client = await Panel.SignedInAs("203.0.113.72", email);
        var html = await client.GetStringAsync("/");

        // The reference mockup drew a "Mute for 24h" control and it arrived here as inert text.
        // Offering it in words is the same claim as offering it as a button, and this block exists
        // precisely to stop the dashboard describing things the platform has not done.
        html.Should().NotContain("attention-header-mute");
    }
}
