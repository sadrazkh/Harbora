using System.Net;
using FluentAssertions;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Deployments;
using Harbora.Domain.Identity;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// 5.2 (2026-09 market-gaps round two, "approval gate on deploying to a protected environment"): the
/// panel side of the gate — the Approve/Reject actions, capability gating, and what the Details page
/// shows a pending deployment's viewers.
///
/// <para>
/// <c>IDeploymentEngine</c> is <c>HarboraWebFactory.Deployments</c> (<c>RecordingDeploymentEngine</c>)
/// in this harness, so these tests prove the controller asks the engine the right question — not the
/// gate's own decision, which <c>DeploymentApprovalGateTests</c> proves against the real engine.
/// </para>
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class DeploymentApprovalHttpTests(HarboraHttpFixture fixture)
{
    private HarboraWebFactory Panel => fixture.Panel;

    private (App App, Guid EnvironmentId) GivenProtectedApp(string slug)
    {
        var projectId = Guid.CreateVersion7();
        var environmentId = Guid.CreateVersion7();
        Panel.Seed(db =>
        {
            db.Projects.Add(new Harbora.Domain.Projects.Project
            { Id = projectId, WorkspaceId = fixture.WorkspaceId, Name = slug, Slug = slug });
            db.Environments.Add(new Harbora.Domain.Projects.Environment
            {
                Id = environmentId, WorkspaceId = fixture.WorkspaceId, ProjectId = projectId,
                Name = "production", Slug = slug + "-production", IsProtected = true
            });
        });
        var app = new App
        {
            WorkspaceId = fixture.WorkspaceId, EnvironmentId = environmentId, ServerId = Guid.CreateVersion7(),
            Name = slug, Slug = slug, Kind = ServiceKind.Web, ContainerPort = 8080,
            SourceType = AppSourceType.GitRepository, GitRef = "main", Status = AppStatus.Running
        };
        Panel.Seed(db => db.Apps.Add(app));
        return (app, environmentId);
    }

    private Deployment GivenPendingDeployment(App app, Guid requesterId, int number = 1)
    {
        var deployment = new Deployment
        {
            AppId = app.Id, WorkspaceId = app.WorkspaceId, Number = number,
            Status = DeploymentStatus.PendingApproval, Trigger = DeploymentTrigger.Manual,
            GitRef = "main", TriggeredByUserId = requesterId, CreatedAt = DateTimeOffset.UtcNow
        };
        Panel.Seed(db => db.Deployments.Add(deployment));
        Panel.Seed(db => db.DeploymentApprovals.Add(new DeploymentApproval
        {
            DeploymentId = deployment.Id, WorkspaceId = app.WorkspaceId,
            RequestedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddHours(24),
            Decision = DeploymentApprovalDecision.Pending
        }));
        return deployment;
    }

    [Fact]
    public async Task Approving_asks_the_engine_and_shows_the_result()
    {
        var (app, _) = GivenProtectedApp("appr-happy");
        var requester = Panel.GivenUser(fixture.WorkspaceId, "appr-requester1@example.com", SystemRole.Member);
        var deployment = GivenPendingDeployment(app, requester.Id);

        Panel.GivenUser(fixture.WorkspaceId, "appr-approver1@example.com", SystemRole.Owner);
        var approver = await Panel.SignedInAs("198.51.100.43", "appr-approver1@example.com");

        var token = await approver.AntiforgeryTokenFrom($"/deployments/details/{deployment.Id}");
        var response = await approver.PostFormAsync($"/deployments/{deployment.Id}/approve", token);

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        Panel.Deployments.Approved.Should().Contain(deployment.Id);
    }

    [Fact]
    public async Task Rejecting_needs_a_reason_and_passes_it_through()
    {
        var (app, _) = GivenProtectedApp("appr-reject");
        var requester = Panel.GivenUser(fixture.WorkspaceId, "appr-requester2@example.com", SystemRole.Member);
        var deployment = GivenPendingDeployment(app, requester.Id);

        Panel.GivenUser(fixture.WorkspaceId, "appr-approver2@example.com", SystemRole.Owner);
        var approver = await Panel.SignedInAs("198.51.100.44", "appr-approver2@example.com");

        var emptyToken = await approver.AntiforgeryTokenFrom($"/deployments/details/{deployment.Id}");
        var emptyAttempt = await approver.PostFormAsync($"/deployments/{deployment.Id}/reject", emptyToken, ("reason", ""));
        emptyAttempt.StatusCode.Should().Be(HttpStatusCode.Found);
        Panel.Deployments.Rejected.Should().BeEmpty("an empty reason must never reach the engine");

        var token = await approver.AntiforgeryTokenFrom($"/deployments/details/{deployment.Id}");
        var response = await approver.PostFormAsync($"/deployments/{deployment.Id}/reject", token,
            ("reason", "The migration in this release is not reviewed yet."));

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        Panel.Deployments.Rejected.Should().ContainSingle(r =>
            r.DeploymentId == deployment.Id && r.Reason.Contains("migration"));
    }

    [Fact]
    public async Task The_engines_own_self_approval_refusal_reaches_the_page_as_a_banner()
    {
        // The domain refusal (DeploymentApprovalPlan) is proved directly against the real engine
        // elsewhere; this proves the controller surfaces whatever InvalidOperationException the
        // engine throws rather than swallowing it or 500ing.
        var (app, _) = GivenProtectedApp("appr-self");
        var requester = Panel.GivenUser(fixture.WorkspaceId, "appr-requester3@example.com", SystemRole.Owner);
        var deployment = GivenPendingDeployment(app, requester.Id);
        // Panel is shared across every test in this collection — must be undone before the next test
        // runs, or an unrelated Approve/Reject elsewhere starts throwing this same refusal.
        Panel.Deployments.RefuseDecisionWith =
            "You requested this deployment, so you cannot also approve or reject it — a second person has to.";
        try
        {
            var client = await Panel.SignedInAs("198.51.100.45", "appr-requester3@example.com");
            var token = await client.AntiforgeryTokenFrom($"/deployments/details/{deployment.Id}");
            var response = await client.PostFormAsync($"/deployments/{deployment.Id}/approve", token);

            response.StatusCode.Should().Be(HttpStatusCode.Found);
            var page = await (await client.GetAsync(response.RedirectPath())).Content.ReadAsStringAsync();
            page.Should().Contain("cannot also approve");
        }
        finally
        {
            Panel.Deployments.RefuseDecisionWith = null;
        }
    }

    [Fact]
    public async Task A_viewer_cannot_approve_or_reject()
    {
        var (app, _) = GivenProtectedApp("appr-viewer");
        var requester = Panel.GivenUser(fixture.WorkspaceId, "appr-requester4@example.com", SystemRole.Owner);
        var deployment = GivenPendingDeployment(app, requester.Id);

        Panel.GivenUser(fixture.WorkspaceId, "appr-viewer1@example.com", SystemRole.Viewer);
        var viewer = await Panel.SignedInAs("198.51.100.46", "appr-viewer1@example.com");

        var token = await viewer.AntiforgeryTokenFrom($"/deployments/details/{deployment.Id}");
        var response = await viewer.PostFormAsync($"/deployments/{deployment.Id}/approve", token);

        // The cookie scheme turns a forbidden result into a redirect to its AccessDeniedPath — the
        // same pipeline behaviour CapabilityPolicyHttpTests pins for every other capability-gated
        // route — so the action never ran and nothing was recorded as approved.
        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.RedirectPath().Should().Be("/account/denied");
        Panel.Deployments.Approved.Should().BeEmpty();
    }

    [Fact]
    public async Task Approve_refuses_another_workspaces_deployment()
    {
        var otherWorkspaceId = Guid.CreateVersion7();
        Panel.Seed(db => db.Workspaces.Add(new Workspace
        { Id = otherWorkspaceId, Name = "Approval Victim Co", Slug = "approval-victim-" + otherWorkspaceId }));
        var (app, _) = GivenProtectedApp("appr-tenant");
        // Force the app into the OTHER workspace after creation to keep GivenProtectedApp's own
        // environment/project seeding simple — only WorkspaceId decides visibility here.
        Panel.Seed(db =>
        {
            var row = db.Apps.Single(a => a.Id == app.Id);
            row.WorkspaceId = otherWorkspaceId;
        });
        var deployment = GivenPendingDeployment(app, Guid.CreateVersion7());
        Panel.Seed(db =>
        {
            var row = db.Deployments.Single(d => d.Id == deployment.Id);
            row.WorkspaceId = otherWorkspaceId;
        });

        Panel.GivenUser(fixture.WorkspaceId, "appr-attacker@example.com", SystemRole.Owner);
        var attacker = await Panel.SignedInAs("198.51.100.47", "appr-attacker@example.com");

        var token = await attacker.AntiforgeryTokenFrom("/deployments");
        var response = await attacker.PostFormAsync($"/deployments/{deployment.Id}/approve", token);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        Panel.Deployments.Approved.Should().BeEmpty();
    }

    [Fact]
    public async Task The_details_page_shows_the_pending_badge_and_the_decision_forms()
    {
        var (app, _) = GivenProtectedApp("appr-page");
        var requester = Panel.GivenUser(fixture.WorkspaceId, "appr-requester5@example.com", SystemRole.Member);
        var deployment = GivenPendingDeployment(app, requester.Id);

        Panel.GivenUser(fixture.WorkspaceId, "appr-approver5@example.com", SystemRole.Owner);
        var approver = await Panel.SignedInAs("198.51.100.48", "appr-approver5@example.com");

        var html = await (await approver.GetAsync($"/deployments/details/{deployment.Id}")).Content.ReadAsStringAsync();

        html.Should().Contain($"action=\"/deployments/{deployment.Id}/approve\"");
        html.Should().Contain($"action=\"/deployments/{deployment.Id}/reject\"");
        html.Should().Contain("data-approval-gate");
    }

    [Fact]
    public async Task The_requester_sees_no_decision_forms_on_their_own_request()
    {
        var (app, _) = GivenProtectedApp("appr-own-page");
        var requester = Panel.GivenUser(fixture.WorkspaceId, "appr-requester6@example.com", SystemRole.Owner);
        var deployment = GivenPendingDeployment(app, requester.Id);

        var client = await Panel.SignedInAs("198.51.100.49", "appr-requester6@example.com");

        var html = await (await client.GetAsync($"/deployments/details/{deployment.Id}")).Content.ReadAsStringAsync();

        html.Should().NotContain($"action=\"/deployments/{deployment.Id}/approve\"",
            "a requester must never be offered the button that approves their own deployment");
    }

    [Fact]
    public async Task An_unprotected_deployment_shows_no_approval_section_at_all()
    {
        var app = new App
        {
            WorkspaceId = fixture.WorkspaceId, EnvironmentId = fixture.DefaultEnvironmentId,
            ServerId = Guid.CreateVersion7(), Name = "appr-unprotected", Slug = "appr-unprotected",
            Kind = ServiceKind.Web, ContainerPort = 8080, SourceType = AppSourceType.GitRepository,
            GitRef = "main", Status = AppStatus.Running
        };
        Panel.Seed(db => db.Apps.Add(app));
        var deployment = new Deployment
        {
            AppId = app.Id, WorkspaceId = app.WorkspaceId, Number = 1, Status = DeploymentStatus.Queued,
            Trigger = DeploymentTrigger.Manual, GitRef = "main", CreatedAt = DateTimeOffset.UtcNow
        };
        Panel.Seed(db => db.Deployments.Add(deployment));

        Panel.GivenUser(fixture.WorkspaceId, "appr-unprotected@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("198.51.100.50", "appr-unprotected@example.com");

        var html = await (await client.GetAsync($"/deployments/details/{deployment.Id}")).Content.ReadAsStringAsync();

        html.Should().NotContain("data-approval-gate");
    }
}
