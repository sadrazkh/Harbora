using System.Net;
using FluentAssertions;
using Harbora.Domain.Apps;
using Harbora.Domain.Authorization;
using Harbora.Domain.Common;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The capability policies, exercised as requests rather than as a permission matrix.
///
/// <para>
/// <c>RolePermissionsTests</c> already proves the matrix itself and
/// <c>ProjectAccessServiceTests</c> proves the placement lookup. Neither can tell whether a route is
/// actually wired to the policy it claims — that is the difference between a rule and a locked door,
/// and it only shows when a request goes through <c>UseAuthorization</c> with a real cookie on it.
/// </para>
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class CapabilityPolicyHttpTests(HarboraHttpFixture fixture)
{
    private HarboraWebFactory Panel => fixture.Panel;

    /// <summary>
    /// EnvironmentId is required (P2, 2026-08-17 app-environment-management design). Left null, a
    /// project and environment of their own are seeded for this one app — uniquely slugged, since
    /// this collection fixture is shared across every test in the class.
    /// </summary>
    private Guid GivenApp(string slug, Guid? environmentId = null)
    {
        var app = new App
        {
            WorkspaceId = fixture.WorkspaceId,
            Name = slug,
            Slug = slug,
            SourceType = AppSourceType.Upload,
            EnvironmentId = environmentId ?? SeedEnvironment()
        };
        Panel.Seed(db => db.Apps.Add(app));
        return app.Id;
    }

    private Guid SeedEnvironment()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var projectId = Guid.CreateVersion7();
        var environmentId = Guid.CreateVersion7();

        Panel.Seed(db =>
        {
            db.Projects.Add(new Harbora.Domain.Projects.Project
            {
                Id = projectId, WorkspaceId = fixture.WorkspaceId, Name = "Shop", Slug = "cap-" + suffix
            });
            db.Environments.Add(new Harbora.Domain.Projects.Environment
            {
                Id = environmentId, WorkspaceId = fixture.WorkspaceId, ProjectId = projectId,
                Name = "Production", Slug = "production", IsDefault = true
            });
        });

        return environmentId;
    }

    /// <summary>A deploy submitted the way the panel's own button submits it.</summary>
    private static async Task<HttpResponseMessage> DeployAsync(HttpClient client, Guid appId)
    {
        var token = await client.AntiforgeryTokenFrom("/apps");
        return await client.PostFormAsync($"/Apps/Deploy/{appId}", token, ("gitRef", "main"));
    }

    [Fact]
    public async Task An_anonymous_request_for_a_panel_page_is_sent_to_the_login_form()
    {
        var response = await Panel.ClientFrom("203.0.113.40").GetAsync("/apps");

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.RedirectPath().Should().Be("/account/login");
    }

    [Fact]
    public async Task An_owner_may_deploy_through_the_form()
    {
        Panel.GivenUser(fixture.WorkspaceId, "cap-owner@example.com", SystemRole.Owner);
        var appId = GivenApp("cap-owner-app");
        var client = await Panel.SignedInAs("203.0.113.41", "cap-owner@example.com");

        var response = await DeployAsync(client, appId);

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.RedirectPath().Should().StartWith("/Deployments/Details");
        Panel.Deployments.Queued.Should().Contain(r => r.AppId == appId);
    }

    [Fact]
    public async Task A_viewer_is_refused_the_same_deploy_by_the_policy()
    {
        Panel.GivenUser(fixture.WorkspaceId, "cap-viewer@example.com", SystemRole.Viewer);
        var appId = GivenApp("cap-viewer-app");
        var client = await Panel.SignedInAs("203.0.113.42", "cap-viewer@example.com");

        var response = await DeployAsync(client, appId);

        // The cookie scheme turns a forbidden result into a redirect to its AccessDeniedPath, which
        // is itself pipeline behaviour: the action never ran and nothing was queued.
        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.RedirectPath().Should().Be("/account/denied");
        Panel.Deployments.Queued.Should().NotContain(r => r.AppId == appId);
    }

    [Fact]
    public async Task An_operator_may_run_a_job_but_may_not_deploy()
    {
        // Two capabilities, one role, one route group: the policies are registered per capability
        // rather than "is this person important enough". Run-now is the apps.operate route chosen
        // here because it answers from the action itself — restart/stop/start reach
        // AppOperationsService, which writes with ExecuteUpdate and so cannot run on this lane's
        // provider (see the report).
        Panel.GivenUser(fixture.WorkspaceId, "cap-operator@example.com", SystemRole.Operator);
        var appId = GivenApp("cap-operator-app");
        var client = await Panel.SignedInAs("203.0.113.43", "cap-operator@example.com");
        var token = await client.AntiforgeryTokenFrom("/apps");

        var runNow = await client.PostFormAsync($"/Apps/RunNow/{appId}", token);
        var deploy = await client.PostFormAsync($"/Apps/Deploy/{appId}", token, ("gitRef", "main"));

        runNow.StatusCode.Should().Be(HttpStatusCode.Found);
        runNow.RedirectPath().Should().StartWith("/Apps/Details",
            "apps.operate is an operator's capability, so the action itself answered");
        deploy.StatusCode.Should().Be(HttpStatusCode.Found);
        deploy.RedirectPath().Should().Be("/account/denied");
    }

    [Fact]
    public async Task A_developer_may_read_apps_but_not_the_platform_administration_pages()
    {
        Panel.GivenUser(fixture.WorkspaceId, "cap-developer@example.com", SystemRole.Member);
        var client = await Panel.SignedInAs("203.0.113.44", "cap-developer@example.com");

        var apps = await client.GetAsync("/apps");
        var audit = await client.GetAsync("/audit");
        var servers = await client.GetAsync("/servers");

        apps.StatusCode.Should().Be(HttpStatusCode.OK);
        audit.StatusCode.Should().Be(HttpStatusCode.Found, "platform.manage is not a developer's");
        audit.RedirectPath().Should().Be("/account/denied");
        servers.StatusCode.Should().Be(HttpStatusCode.Found, "servers.manage is not a developer's either");
        servers.RedirectPath().Should().Be("/account/denied");
    }

    [Fact]
    public async Task A_developer_scoped_to_one_project_cannot_reach_another_projects_app()
    {
        // The capability policy passes — a Member holds apps.deploy — and the refusal comes from the
        // placement check inside the action. Both have to hold for the door to be locked, and this
        // is the only place the two are asked in the same breath.
        var developer = Panel.GivenUser(
            fixture.WorkspaceId, "cap-scoped@example.com", SystemRole.Member, scopedToProjects: true);

        var theirProject = Guid.CreateVersion7();
        var theirEnvironment = Guid.CreateVersion7();
        var otherProject = Guid.CreateVersion7();
        var otherEnvironment = Guid.CreateVersion7();

        Panel.Seed(db =>
        {
            db.Projects.AddRange(
                new Harbora.Domain.Projects.Project
                { Id = theirProject, WorkspaceId = fixture.WorkspaceId, Name = "Shop", Slug = "cap-shop" },
                new Harbora.Domain.Projects.Project
                { Id = otherProject, WorkspaceId = fixture.WorkspaceId, Name = "Payroll", Slug = "cap-payroll" });

            db.Environments.AddRange(
                new Harbora.Domain.Projects.Environment
                {
                    Id = theirEnvironment, WorkspaceId = fixture.WorkspaceId,
                    ProjectId = theirProject, Name = "Production", Slug = "production"
                },
                new Harbora.Domain.Projects.Environment
                {
                    Id = otherEnvironment, WorkspaceId = fixture.WorkspaceId,
                    ProjectId = otherProject, Name = "Production", Slug = "production"
                });

            db.ProjectGrants.Add(new ProjectGrant
            {
                WorkspaceId = fixture.WorkspaceId,
                UserId = developer.Id,
                ProjectId = theirProject,
                Role = SystemRole.Member
            });
        });

        var theirApp = GivenApp("cap-shop-api", theirEnvironment);
        var otherApp = GivenApp("cap-payroll-api", otherEnvironment);
        var client = await Panel.SignedInAs("203.0.113.45", "cap-scoped@example.com");

        var allowed = await DeployAsync(client, theirApp);
        var refused = await DeployAsync(client, otherApp);

        allowed.StatusCode.Should().Be(HttpStatusCode.Found);
        allowed.RedirectPath().Should().StartWith("/Deployments/Details");

        refused.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "another project's app answers the same as one that is not there");
        Panel.Deployments.Queued.Should().NotContain(r => r.AppId == otherApp);
    }

    // ---- 5.1 (per-app and per-service grants, HARBORA-0035) ----

    /// <summary>
    /// The narrowing one level past project scoping: named to one app, not to the project it lives
    /// in. Deliberately no project- or environment-level grant at all here — the whole point is that
    /// the app-level grant reaches on its own.
    /// </summary>
    [Fact]
    public async Task A_developer_scoped_to_one_app_cannot_reach_a_sibling_app_in_the_same_project()
    {
        var developer = Panel.GivenUser(
            fixture.WorkspaceId, "cap-app-scoped@example.com", SystemRole.Member, scopedToProjects: true);

        var sharedProject = Guid.CreateVersion7();
        var sharedEnvironment = Guid.CreateVersion7();

        Panel.Seed(db =>
        {
            db.Projects.Add(new Harbora.Domain.Projects.Project
            { Id = sharedProject, WorkspaceId = fixture.WorkspaceId, Name = "Shop", Slug = "cap-app-shop" });
            db.Environments.Add(new Harbora.Domain.Projects.Environment
            {
                Id = sharedEnvironment, WorkspaceId = fixture.WorkspaceId,
                ProjectId = sharedProject, Name = "Production", Slug = "production"
            });
        });

        var theirApp = GivenApp("cap-marketing-site", sharedEnvironment);
        var siblingApp = GivenApp("cap-payroll-api", sharedEnvironment);

        Panel.Seed(db => db.ProjectGrants.Add(new ProjectGrant
        {
            WorkspaceId = fixture.WorkspaceId,
            UserId = developer.Id,
            ProjectId = sharedProject,
            AppId = theirApp,
            Role = SystemRole.Member
        }));

        var client = await Panel.SignedInAs("198.51.100.190", "cap-app-scoped@example.com");

        var allowed = await DeployAsync(client, theirApp);
        var refused = await DeployAsync(client, siblingApp);

        allowed.StatusCode.Should().Be(HttpStatusCode.Found,
            "the app-level grant reaches this app with no project-wide grant behind it at all");
        allowed.RedirectPath().Should().StartWith("/Deployments/Details");

        refused.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "a sibling app in the same project answers the same as one that is not there — " +
            "the grant named one app, not the project it happens to live in");
        Panel.Deployments.Queued.Should().NotContain(r => r.AppId == siblingApp);
    }

    /// <summary>
    /// Listing has to agree with acting: a member granted one app must not be shown — and so learn
    /// the existence and name of — a sibling app in the same project that nobody granted them.
    /// </summary>
    [Fact]
    public async Task A_developer_scoped_to_one_app_does_not_see_a_sibling_app_in_the_apps_list()
    {
        var developer = Panel.GivenUser(
            fixture.WorkspaceId, "cap-app-list@example.com", SystemRole.Member, scopedToProjects: true);

        var sharedProject = Guid.CreateVersion7();
        var sharedEnvironment = Guid.CreateVersion7();

        Panel.Seed(db =>
        {
            db.Projects.Add(new Harbora.Domain.Projects.Project
            { Id = sharedProject, WorkspaceId = fixture.WorkspaceId, Name = "Shop", Slug = "cap-app-list-shop" });
            db.Environments.Add(new Harbora.Domain.Projects.Environment
            {
                Id = sharedEnvironment, WorkspaceId = fixture.WorkspaceId,
                ProjectId = sharedProject, Name = "Production", Slug = "production"
            });
        });

        var visibleApp = GivenApp("cap-list-visible", sharedEnvironment);
        var hiddenApp = GivenApp("cap-list-hidden", sharedEnvironment);

        Panel.Seed(db => db.ProjectGrants.Add(new ProjectGrant
        {
            WorkspaceId = fixture.WorkspaceId,
            UserId = developer.Id,
            ProjectId = sharedProject,
            AppId = visibleApp,
            Role = SystemRole.Member
        }));

        var client = await Panel.SignedInAs("198.51.100.191", "cap-app-list@example.com");

        var html = await (await client.GetAsync("/apps")).Content.ReadAsStringAsync();

        html.Should().Contain("cap-list-visible", "the granted app must still appear in the list");
        html.Should().NotContain("cap-list-hidden",
            "a sibling app in the same project that nobody granted them must not be named on the page");
    }
}
