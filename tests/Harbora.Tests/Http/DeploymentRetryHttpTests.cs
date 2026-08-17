using System.Net;
using FluentAssertions;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Deployments;
using Harbora.Domain.Servers;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// P6's second half (2026-08-17 app-environment-management design): a retry action on a failed
/// deployment. <c>DeploymentsController</c> had four POSTs — cancel, promote, the two assistant
/// actions — and no retry, even though <c>_DeployProgress.cshtml</c> has told a failed deployment's
/// reader "you can retry" all along (the retry it meant was the ordinary Deploy button on a
/// different page, which redeploys the branch tip <i>as it is now</i>, not what actually failed).
///
/// <para>
/// §7 Q4's answer, chosen here: the retry re-uses the failed deployment's own recorded
/// <see cref="Deployment.GitRef"/> — the exact ref that attempt used, which can differ from the
/// app's current default (a webhook deploy of a feature branch, a manual override) — and nothing
/// else. It never re-uses <see cref="Deployment.SourceArchivePath"/> (deleted by
/// <c>DeploymentPipeline.MaterialiseSourceAsync</c> the moment it is read, so a second reference to
/// it would either be stale or point at nothing) and never re-uses
/// <see cref="Deployment.ImageTag"/> (a retry rebuilds; skipping the build is what Promote is for).
/// Environment variables, volumes and instance size are not part of a <c>DeploymentRequest</c> at
/// all — the pipeline always reads those off the app as it is <i>now</i>, snapshot or no snapshot,
/// so there is nothing here to carry over even if the retry wanted to.
/// </para>
///
/// <para>
/// <c>IDeploymentEngine</c> is replaced by <c>HarboraWebFactory.Deployments</c>
/// (<c>RecordingDeploymentEngine</c>) in this harness, so every assertion below reads the exact
/// <c>DeploymentRequest</c> the controller built — proving what the retry promises, not just that it
/// redirects.
/// </para>
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class DeploymentRetryHttpTests(HarboraHttpFixture fixture)
{
    private HarboraWebFactory Panel => fixture.Panel;

    private App GivenApp(string slug, string gitRef = "main")
    {
        var app = new App
        {
            WorkspaceId = fixture.WorkspaceId,
            ServerId = Guid.CreateVersion7(),
            Name = slug,
            Slug = slug,
            Kind = ServiceKind.Web,
            ContainerPort = 8080,
            SourceType = AppSourceType.GitRepository,
            GitRef = gitRef,
            Status = AppStatus.Running
        };
        Panel.Seed(db => db.Apps.Add(app));
        return app;
    }

    private Deployment GivenDeployment(
        App app, DeploymentStatus status, int number = 1, string? gitRef = null,
        string? sourceArchivePath = null, string? imageTag = null)
    {
        var deployment = new Deployment
        {
            AppId = app.Id,
            WorkspaceId = app.WorkspaceId,
            Number = number,
            Status = status,
            Trigger = DeploymentTrigger.Manual,
            GitRef = gitRef,
            SourceArchivePath = sourceArchivePath,
            ImageTag = imageTag,
            CreatedAt = DateTimeOffset.UtcNow
        };
        Panel.Seed(db => db.Deployments.Add(deployment));
        return deployment;
    }

    [Fact]
    public async Task Retrying_a_failed_deployment_queues_a_new_deployment_with_the_same_git_ref()
    {
        // The app's OWN current default branch differs from what this attempt actually deployed —
        // the case a bare "click Deploy" would get wrong.
        var app = GivenApp("retry-gitref", gitRef: "main");
        var failed = GivenDeployment(app, DeploymentStatus.Failed, number: 3, gitRef: "hotfix-branch");
        Panel.GivenUser(fixture.WorkspaceId, "retry-gitref@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.240", "retry-gitref@example.com");

        var token = await client.AntiforgeryTokenFrom($"/deployments/details/{failed.Id}");
        var response = await client.PostFormAsync($"/deployments/{failed.Id}/retry", token);

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        var queued = Panel.Deployments.Queued.Should().ContainSingle(r => r.AppId == app.Id).Subject;
        queued.GitRef.Should().Be("hotfix-branch",
            "the retry must reuse the failed attempt's own ref, not the app's current default");
    }

    [Fact]
    public async Task Retry_never_carries_over_the_uploaded_archive_or_a_prior_image()
    {
        var app = GivenApp("retry-no-carry");
        var failed = GivenDeployment(app, DeploymentStatus.Failed, gitRef: "main",
            sourceArchivePath: "/var/harbora/staging/deleted-already.tar.gz",
            imageTag: "harbora/retry-no-carry:build-1");
        Panel.GivenUser(fixture.WorkspaceId, "retry-no-carry@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.241", "retry-no-carry@example.com");

        var token = await client.AntiforgeryTokenFrom($"/deployments/details/{failed.Id}");
        await client.PostFormAsync($"/deployments/{failed.Id}/retry", token);

        var queued = Panel.Deployments.Queued.Should().ContainSingle(r => r.AppId == app.Id).Subject;
        queued.SourceArchivePath.Should().BeNull(
            "the archive was deleted the moment the failed attempt read it; a second reference would be dead");
        queued.ImageOverride.Should().BeNull(
            "a retry rebuilds from source — reusing the image is what Promote is for, not Retry");
    }

    [Fact]
    public async Task Retry_mints_a_new_row_rather_than_touching_the_failed_one()
    {
        var app = GivenApp("retry-immutable");
        var failed = GivenDeployment(app, DeploymentStatus.Failed, number: 5, gitRef: "main");
        Panel.GivenUser(fixture.WorkspaceId, "retry-immutable@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.242", "retry-immutable@example.com");

        var token = await client.AntiforgeryTokenFrom($"/deployments/details/{failed.Id}");
        await client.PostFormAsync($"/deployments/{failed.Id}/retry", token);

        Panel.Read(db => db.Deployments.Single(d => d.Id == failed.Id)).Status.Should().Be(
            DeploymentStatus.Failed, "the failed row is history; a retry never rewrites it in place");
    }

    [Theory]
    [InlineData(DeploymentStatus.Succeeded)]
    [InlineData(DeploymentStatus.Queued)]
    [InlineData(DeploymentStatus.Building)]
    [InlineData(DeploymentStatus.Cancelled)]
    public async Task Retry_is_refused_for_anything_that_has_not_failed(DeploymentStatus status)
    {
        var app = GivenApp("retry-gate-" + status);
        var deployment = GivenDeployment(app, status, gitRef: "main");
        Panel.GivenUser(fixture.WorkspaceId, $"retry-gate-{status}@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs($"203.0.113.{160 + (int)status}", $"retry-gate-{status}@example.com");

        var token = await client.AntiforgeryTokenFrom($"/deployments/details/{deployment.Id}");
        var response = await client.PostFormAsync($"/deployments/{deployment.Id}/retry", token);

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        Panel.Deployments.Queued.Should().NotContain(r => r.AppId == app.Id,
            $"a {status} deployment offers nothing to retry — only Failed does");
    }

    /// <summary>The same cross-tenant guard every other action on this controller already has.</summary>
    [Fact]
    public async Task Retry_refuses_another_workspaces_deployment()
    {
        var otherWorkspaceId = Guid.CreateVersion7();
        Panel.Seed(db => db.Workspaces.Add(new Harbora.Domain.Identity.Workspace
        {
            Id = otherWorkspaceId, Name = "Retry Victim Co", Slug = "retry-victim-co-" + otherWorkspaceId
        }));
        var victimsApp = new App
        {
            WorkspaceId = otherWorkspaceId, ServerId = Guid.CreateVersion7(),
            Name = "victim", Slug = "retry-victim", Kind = ServiceKind.Web, ContainerPort = 8080,
            SourceType = AppSourceType.GitRepository, GitRef = "main"
        };
        Panel.Seed(db => db.Apps.Add(victimsApp));
        var victimsDeployment = GivenDeployment(victimsApp, DeploymentStatus.Failed, gitRef: "main");

        Panel.GivenUser(fixture.WorkspaceId, "retry-attacker@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.243", "retry-attacker@example.com");

        // No page of their own to take a token from — the antiforgery cookie alone is enough to
        // post, so this proves the workspace guard rather than the token check.
        var token = await client.AntiforgeryTokenFrom("/deployments");
        var response = await client.PostFormAsync($"/deployments/{victimsDeployment.Id}/retry", token);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        Panel.Deployments.Queued.Should().NotContain(r => r.AppId == victimsApp.Id);
    }

    [Fact]
    public async Task A_failed_deployments_page_offers_a_retry_button()
    {
        var app = GivenApp("retry-button");
        var failed = GivenDeployment(app, DeploymentStatus.Failed, gitRef: "main");
        Panel.GivenUser(fixture.WorkspaceId, "retry-button@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.244", "retry-button@example.com");

        var html = await (await client.GetAsync($"/deployments/details/{failed.Id}")).Content.ReadAsStringAsync();

        html.Should().Contain($"action=\"/deployments/{failed.Id}/retry\"");
    }

    /// <summary>A succeeded deployment's own page must not offer the button at all — the same
    /// "offering a control that always refuses teaches people to ignore it" rule the promote and
    /// cancel controls already follow.</summary>
    [Fact]
    public async Task A_succeeded_deployments_page_offers_no_retry_button()
    {
        var app = GivenApp("retry-no-button");
        var succeeded = GivenDeployment(app, DeploymentStatus.Succeeded, gitRef: "main");
        Panel.GivenUser(fixture.WorkspaceId, "retry-no-button@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.245", "retry-no-button@example.com");

        var html = await (await client.GetAsync($"/deployments/details/{succeeded.Id}")).Content.ReadAsStringAsync();

        html.Should().NotContain($"action=\"/deployments/{succeeded.Id}/retry\"");
    }
}
