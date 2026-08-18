using System.Text.Json;
using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Identity;
using Harbora.Infrastructure.Billing;
using Harbora.Infrastructure.Deployments;
using Harbora.Infrastructure.Nodes;
using Harbora.Infrastructure.Security;
using Harbora.Tests.Fakes;
using Harbora.Web.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using Environment = Harbora.Domain.Projects.Environment;
using Project = Harbora.Domain.Projects.Project;

namespace Harbora.Tests;

/// <summary>
/// A project-wide log search reaching a workspace that is not the caller's.
///
/// <para>
/// <c>Project</c> and <c>Environment</c> carry no global query filter (unlike <c>App</c>) — see
/// <c>HarboraDbContext</c>'s <c>HasQueryFilter</c> calls, which never mention either — so
/// <c>LogsController</c>'s own explicit <c>WorkspaceId ==</c> predicates are the <i>only</i> thing
/// standing between a project id typed into the URL and another workspace's logs. These tests pin
/// both directions: that a search finds what belongs to the caller (the positive a filter that is
/// too strict would also pass by accident), and that it cannot reach what does not — including with
/// the ambient App query filter deliberately disabled, so the guard being pinned is the controller's
/// own explicit check and not incidentally the filter underneath it.
/// </para>
/// </summary>
public class LogsControllerTenancyTests
{
    private static readonly Guid Owner = Guid.CreateVersion7();
    private static readonly Guid Intruder = Guid.CreateVersion7();

    private sealed record Fixture(
        HarboraDbContext Db, LogsController Controller, Guid ProjectId, Guid AppId, FakeDockerEngine Docker);

    /// <summary>
    /// One workspace with a project, one environment, and one running app with a matching log line —
    /// and a second, entirely separate workspace whose own session drives the controller under test.
    /// </summary>
    private static Fixture Build(Guid callerWorkspace, IWorkspaceScope? scope = null)
    {
        var store = "log-search-tenancy-" + Guid.NewGuid();
        var db = new HarboraDbContext(
            new DbContextOptionsBuilder<HarboraDbContext>().UseInMemoryDatabase(store).Options,
            scope ?? new FixedWorkspaceScope(callerWorkspace));

        db.Workspaces.Add(new Workspace { Id = Owner, Name = "Acme", Slug = "acme" });
        db.Workspaces.Add(new Workspace { Id = Intruder, Name = "Other", Slug = "other" });

        var callerUserId = Guid.CreateVersion7();
        db.Users.Add(new User { Id = callerUserId, Email = "me@example.com", DisplayName = "Tester" });
        // An unscoped admin membership in the CALLER's own workspace, so ProjectAccessService lets
        // them see everything that workspace owns — the search itself, not this grant, is what must
        // refuse to reach the other workspace.
        db.WorkspaceMembers.Add(new WorkspaceMember
        {
            WorkspaceId = callerWorkspace, UserId = callerUserId,
            Role = WorkspaceRole.Admin, ScopedToProjects = false
        });

        var projectId = Guid.CreateVersion7();
        var environmentId = Guid.CreateVersion7();
        db.Projects.Add(new Project { Id = projectId, WorkspaceId = Owner, Name = "Shop", Slug = "shop" });
        db.Environments.Add(new Environment
        {
            Id = environmentId, WorkspaceId = Owner, ProjectId = projectId,
            Name = "Production", Slug = "production", IsDefault = true
        });

        var appId = Guid.CreateVersion7();
        db.Apps.Add(new App
        {
            Id = appId, WorkspaceId = Owner, EnvironmentId = environmentId, ServerId = Guid.CreateVersion7(),
            Name = "api", Slug = "api", Status = AppStatus.Running
        });
        db.SaveChanges();

        var docker = new FakeDockerEngine();
        var containerId = docker.SeedContainer("harbora-api-1", "api", workspaceId: Owner);
        docker.ContainerLogsById[containerId] = "ERROR could not reach the payment provider";

        var ingress = new NodeIngressRegistry(
            Options.Create(new NodeAgentControlPlaneOptions()), NullLogger<NodeIngressRegistry>.Instance);
        var ops = new AppOperationsService(
            db, new FakeServerEngineFactory(docker), new RecordingProxyEngine(() => []),
            new BillingGate(db, Options.Create(new BillingOptions())),
            new HostPortAllocator(db, ingress, NullLogger<HostPortAllocator>.Instance),
            NullLogger<AppOperationsService>.Instance);

        var currentUser = new Caller(callerWorkspace, callerUserId);
        var access = new ProjectAccessService(db, currentUser);
        var controller = new LogsController(db, ops, access, currentUser);

        return new Fixture(db, controller, projectId, appId, docker);
    }

    // ---- finds what it should ----

    [Fact]
    public async Task A_project_search_finds_the_callers_own_apps_matching_line()
    {
        var f = Build(callerWorkspace: Owner);

        var result = await f.Controller.SearchData(f.ProjectId, null, "provider", false, 0, default);

        var json = ReadJson(result);
        json.GetProperty("appsSearched").GetInt32().Should().Be(1);
        json.GetProperty("appsReached").GetInt32().Should().Be(1);
        var hits = json.GetProperty("hits").EnumerateArray().ToList();
        hits.Should().ContainSingle();
        hits[0].GetProperty("appId").GetGuid().Should().Be(f.AppId);
        hits[0].GetProperty("line").GetString().Should().Contain("payment provider");
    }

    // ---- cannot reach what it must not ----

    [Fact]
    public async Task A_project_search_for_another_workspaces_project_finds_nothing_and_touches_no_container()
    {
        var f = Build(callerWorkspace: Intruder);

        var result = await f.Controller.SearchData(f.ProjectId, null, "provider", false, 0, default);

        result.Should().BeOfType<NotFoundResult>(
            "the project belongs to a different workspace than the caller's session");
        f.Docker.Calls.Should().BeEmpty(
            "a search that cannot even confirm the project is refused before any container is asked about");
    }

    [Fact]
    public async Task The_project_page_itself_refuses_another_workspaces_project_the_same_way()
    {
        var f = Build(callerWorkspace: Intruder);

        var result = await f.Controller.Search(f.ProjectId, null, default);

        result.Should().BeOfType<NotFoundResult>();
    }

    /// <param name="scopeIsInert">
    /// Both halves of one claim, and the second is why the first means anything. Scoped to the
    /// intruder's own workspace, both the ambient App query filter (App carries one; Project does
    /// not) and the controller's explicit predicate would refuse this on their own, so a passing test
    /// says nothing about which one actually did. Asked over an unscoped context, App's global filter
    /// is inert and only LogsController's own <c>WorkspaceId ==</c> checks are left — proving the
    /// refusal does not quietly depend on a filter one layer down that this feature did not add.
    /// </param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Another_workspaces_project_stays_out_of_reach_even_with_the_apps_filter_disabled(
        bool scopeIsInert)
    {
        var f = Build(callerWorkspace: Intruder, scope: scopeIsInert ? SystemWorkspaceScope.Instance : null);

        var result = await f.Controller.SearchData(f.ProjectId, null, null, false, 0, default);

        result.Should().BeOfType<NotFoundResult>();
        f.Docker.Calls.Should().BeEmpty();
    }

    // ---- environment narrowing ----

    [Fact]
    public async Task Narrowing_to_one_environment_excludes_a_sibling_environments_app()
    {
        var f = Build(callerWorkspace: Owner);

        var otherEnvironmentId = Guid.CreateVersion7();
        f.Db.Environments.Add(new Environment
        {
            Id = otherEnvironmentId, WorkspaceId = Owner, ProjectId = f.ProjectId,
            Name = "Staging", Slug = "staging"
        });
        var otherAppId = Guid.CreateVersion7();
        f.Db.Apps.Add(new App
        {
            Id = otherAppId, WorkspaceId = Owner, EnvironmentId = otherEnvironmentId, ServerId = Guid.CreateVersion7(),
            Name = "api-staging", Slug = "api-staging", Status = AppStatus.Running
        });
        f.Db.SaveChanges();
        var stagingContainer = f.Docker.SeedContainer("harbora-api-staging-1", "api-staging", workspaceId: Owner);
        f.Docker.ContainerLogsById[stagingContainer] = "ERROR staging only line";

        var productionEnvironmentId = await f.Db.Environments.AsNoTracking()
            .Where(e => e.ProjectId == f.ProjectId && e.IsDefault).Select(e => e.Id).SingleAsync();

        var result = await f.Controller.SearchData(
            f.ProjectId, productionEnvironmentId, "error", false, 0, default);

        var json = ReadJson(result);
        json.GetProperty("appsSearched").GetInt32().Should().Be(1,
            "the staging app is out of scope once the search is narrowed to the default environment");
    }

    // ---- helpers ----

    private static JsonElement ReadJson(IActionResult result)
    {
        var json = result.Should().BeOfType<JsonResult>().Which;
        return JsonDocument.Parse(JsonSerializer.Serialize(json.Value)).RootElement;
    }

    private sealed class Caller(Guid workspaceId, Guid userId) : ICurrentUser
    {
        public Guid? UserId { get; } = userId;
        public string? Email => "me@example.com";
        public bool IsAuthenticated => true;
        public Guid? WorkspaceId { get; } = workspaceId;
    }
}
