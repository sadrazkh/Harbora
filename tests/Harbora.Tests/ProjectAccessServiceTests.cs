using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Authorization;
using Harbora.Domain.Common;
using Harbora.Domain.Identity;
using Harbora.Domain.Services;
using Harbora.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The enforcement, against a real database.
///
/// The rule is tested on its own; this is the part that decides where a resource sits and what the
/// caller was granted. It is the half that actually keeps someone out, so getting the placement
/// lookup wrong would leave a rule that is correct and never consulted properly.
/// </summary>
public class ProjectAccessServiceTests : IDisposable
{
    private readonly HarboraDbContext _db;
    private readonly Guid _workspace = Guid.NewGuid();
    private readonly Guid _user = Guid.NewGuid();
    private readonly Guid _shopProject = Guid.NewGuid();
    private readonly Guid _shopProduction = Guid.NewGuid();
    private readonly Guid _secretProject = Guid.NewGuid();
    private readonly Guid _secretEnvironment = Guid.NewGuid();

    public ProjectAccessServiceTests()
    {
        _db = new HarboraDbContext(new DbContextOptionsBuilder<HarboraDbContext>()
            .UseInMemoryDatabase("access-" + Guid.NewGuid()).Options);

        _db.Projects.AddRange(
            new Harbora.Domain.Projects.Project { Id = _shopProject, WorkspaceId = _workspace, Name = "Shop", Slug = "shop" },
            new Harbora.Domain.Projects.Project { Id = _secretProject, WorkspaceId = _workspace, Name = "Secret", Slug = "secret" });

        _db.Environments.AddRange(
            new Harbora.Domain.Projects.Environment
            { Id = _shopProduction, WorkspaceId = _workspace, ProjectId = _shopProject, Name = "Production", Slug = "production" },
            new Harbora.Domain.Projects.Environment
            { Id = _secretEnvironment, WorkspaceId = _workspace, ProjectId = _secretProject, Name = "Production", Slug = "production" });

        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private sealed class Caller(Guid userId, Guid workspaceId) : ICurrentUser
    {
        public Guid? UserId => userId;
        public string? Email => "someone@example.com";
        public bool IsAuthenticated => true;
        public Guid? WorkspaceId => workspaceId;
    }

    private ProjectAccessService Service() => new(_db, new Caller(_user, _workspace));

    private void GivenUser(SystemRole role, bool scoped, params (Guid Project, Guid? Environment, SystemRole Role)[] grants)
    {
        _db.Users.Add(new User
        {
            Id = _user, Email = "someone@example.com", DisplayName = "Someone",
            Role = role, ScopedToProjects = scoped
        });
        _db.WorkspaceMembers.Add(new WorkspaceMember
        {
            WorkspaceId = _workspace,
            UserId = _user,
            Role = role switch
            {
                SystemRole.Owner or SystemRole.Admin => WorkspaceRole.Admin,
                SystemRole.Operator => WorkspaceRole.Operator,
                SystemRole.Viewer => WorkspaceRole.Viewer,
                _ => WorkspaceRole.Member
            },
            ScopedToProjects = scoped
        });

        foreach (var (project, environment, grantRole) in grants)
            _db.ProjectGrants.Add(new ProjectGrant
            {
                WorkspaceId = _workspace, UserId = _user,
                ProjectId = project, EnvironmentId = environment, Role = grantRole
            });

        _db.SaveChanges();
    }

    private Guid GivenApp(Guid? environmentId)
    {
        var app = new App
        {
            WorkspaceId = _workspace, EnvironmentId = environmentId,
            Name = "app", Slug = "app-" + Guid.NewGuid().ToString("N")[..6]
        };
        _db.Apps.Add(app);
        _db.SaveChanges();
        return app.Id;
    }

    private Guid GivenDatabase(Guid? environmentId)
    {
        var service = new ManagedService
        {
            WorkspaceId = _workspace, EnvironmentId = environmentId,
            Name = "db", Type = ManagedServiceType.PostgreSql, ContainerName = "harbora-svc-db"
        };
        _db.ManagedServices.Add(service);
        _db.SaveChanges();
        return service.Id;
    }

    [Fact]
    public async Task A_scoped_member_cannot_touch_an_app_in_a_project_they_were_not_given()
    {
        GivenUser(SystemRole.Member, scoped: true, (_shopProject, null, SystemRole.Member));
        var theirs = GivenApp(_shopProduction);
        var other = GivenApp(_secretEnvironment);

        var service = Service();

        (await service.CanTouchAppAsync(theirs, Capabilities.AppsDeploy, default)).Should().BeTrue();
        (await service.CanTouchAppAsync(other, Capabilities.AppsDeploy, default)).Should().BeFalse();
    }

    [Fact]
    public async Task Batch_app_authorization_returns_only_rows_the_caller_can_operate()
    {
        GivenUser(SystemRole.Member, scoped: true, (_shopProject, null, SystemRole.Member));
        var permitted = GivenApp(_shopProduction);
        var hidden = GivenApp(_secretEnvironment);

        var result = await Service().TouchableAppIdsAsync(
            [permitted, hidden], Capabilities.AppsOperate, default);

        result.Should().BeEquivalentTo([permitted]);
    }

    [Fact]
    public async Task Batch_app_authorization_does_not_turn_visibility_into_an_action_permission()
    {
        GivenUser(SystemRole.Viewer, scoped: true, (_shopProject, null, SystemRole.Viewer));
        var visible = GivenApp(_shopProduction);

        var result = await Service().TouchableAppIdsAsync(
            [visible], Capabilities.AppsOperate, default);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task The_same_holds_for_a_database()
    {
        GivenUser(SystemRole.Member, scoped: true, (_shopProject, null, SystemRole.Member));
        var theirs = GivenDatabase(_shopProduction);
        var other = GivenDatabase(_secretEnvironment);

        var service = Service();

        (await service.CanTouchServiceAsync(theirs, Capabilities.DatabasesManage, default)).Should().BeTrue();
        (await service.CanTouchServiceAsync(other, Capabilities.DatabasesManage, default)).Should().BeFalse();
    }

    [Fact]
    public async Task Someone_elses_grant_does_not_apply_to_you()
    {
        // Without the caller in the filter, one grant anywhere in the workspace would open that
        // project to everybody in it — and every test with a single user would still pass.
        GivenUser(SystemRole.Member, scoped: true, (_shopProject, null, SystemRole.Member));

        var colleague = Guid.NewGuid();
        _db.ProjectGrants.Add(new ProjectGrant
        {
            WorkspaceId = _workspace, UserId = colleague,
            ProjectId = _secretProject, Role = SystemRole.Member
        });
        await _db.SaveChangesAsync();

        var theirProject = GivenApp(_secretEnvironment);

        (await Service().CanTouchAppAsync(theirProject, Capabilities.AppsDeploy, default)).Should().BeFalse();
        (await Service().VisibleProjectIdsAsync(default)).Should().BeEquivalentTo([_shopProject]);
    }

    [Fact]
    public async Task A_viewer_on_a_project_can_read_it_but_not_act_on_it()
    {
        // Found on the live server: gating the page on an action capability locked a viewer out of
        // something the list was still showing them. Reading follows visibility so the two agree.
        GivenUser(SystemRole.Viewer, scoped: true, (_shopProject, null, SystemRole.Viewer));
        var app = GivenApp(_shopProduction);

        var service = Service();

        (await service.CanSeeAppAsync(app, default)).Should().BeTrue();
        (await service.CanTouchAppAsync(app, Capabilities.AppsDeploy, default)).Should().BeFalse();
    }

    [Fact]
    public async Task Reading_still_stops_at_the_projects_they_were_given()
    {
        // The guard on the line above: "viewers may read" must not become "viewers may read
        // everything".
        GivenUser(SystemRole.Viewer, scoped: true, (_shopProject, null, SystemRole.Viewer));

        (await Service().CanSeeAppAsync(GivenApp(_secretEnvironment), default)).Should().BeFalse();
    }

    [Fact]
    public async Task An_unscoped_member_is_unaffected()
    {
        // Everyone is this until someone deliberately limits them, so it must behave exactly as before.
        GivenUser(SystemRole.Member, scoped: false);
        var app = GivenApp(_secretEnvironment);

        (await Service().CanTouchAppAsync(app, Capabilities.AppsDeploy, default)).Should().BeTrue();
    }

    [Fact]
    public async Task Workspace_role_wins_over_the_accounts_platform_role()
    {
        GivenUser(SystemRole.Admin, scoped: false);
        var membership = await _db.WorkspaceMembers.SingleAsync();
        membership.Role = WorkspaceRole.Viewer;
        await _db.SaveChangesAsync();

        var app = GivenApp(_shopProduction);

        (await Service().CanSeeAppAsync(app, default)).Should().BeTrue();
        (await Service().CanTouchAppAsync(app, Capabilities.AppsDeploy, default)).Should().BeFalse(
            "being a platform administrator must not silently make this membership a workspace administrator");
    }

    [Fact]
    public async Task An_app_belonging_to_no_project_is_out_of_reach_when_scoped()
    {
        GivenUser(SystemRole.Member, scoped: true, (_shopProject, null, SystemRole.Member));
        var orphan = GivenApp(environmentId: null);

        (await Service().CanTouchAppAsync(orphan, Capabilities.AppsDeploy, default)).Should().BeFalse();
    }

    [Fact]
    public async Task A_resource_in_another_workspace_is_not_found_rather_than_refused()
    {
        // Telling someone which of the two it is tells them the resource exists.
        GivenUser(SystemRole.Admin, scoped: false);
        var stranger = new App { WorkspaceId = Guid.NewGuid(), Name = "theirs", Slug = "theirs" };
        _db.Apps.Add(stranger);
        await _db.SaveChangesAsync();

        (await Service().CanTouchAppAsync(stranger.Id, Capabilities.AppsDeploy, default)).Should().BeFalse();
    }

    [Fact]
    public async Task A_claim_with_no_user_behind_it_gets_nothing()
    {
        // A deleted account whose cookie is still valid must not keep working.
        var app = GivenApp(_shopProduction);

        (await Service().CanTouchAppAsync(app, Capabilities.AppsDeploy, default)).Should().BeFalse();
    }

    [Fact]
    public async Task Visible_projects_are_every_project_for_someone_who_is_not_scoped()
    {
        // Null, not an empty list: a caller filtering on an empty list would hide everything.
        GivenUser(SystemRole.Member, scoped: false);

        (await Service().VisibleProjectIdsAsync(default)).Should().BeNull();
    }

    [Fact]
    public async Task Visible_projects_are_only_the_granted_ones_when_scoped()
    {
        GivenUser(SystemRole.Member, scoped: true, (_shopProject, null, SystemRole.Member));

        (await Service().VisibleProjectIdsAsync(default)).Should().BeEquivalentTo([_shopProject]);
    }

    [Fact]
    public async Task An_admin_is_never_narrowed_even_with_the_flag_set()
    {
        GivenUser(SystemRole.Admin, scoped: true);

        (await Service().VisibleProjectIdsAsync(default)).Should().BeNull();
        (await Service().CanTouchAppAsync(GivenApp(_secretEnvironment), Capabilities.AppsDeploy, default))
            .Should().BeTrue();
    }

    [Fact]
    public async Task A_workspace_wide_rule_is_out_of_reach_of_a_scoped_member()
    {
        // What routing is: a rule that covers every project, so it belongs to someone who can see
        // every project.
        GivenUser(SystemRole.Member, scoped: true, (_shopProject, null, SystemRole.Member));

        (await Service().AllowsAsync(new ResourcePlacement(null, null), Capabilities.RoutesManage, default))
            .Should().BeFalse();
    }
}
