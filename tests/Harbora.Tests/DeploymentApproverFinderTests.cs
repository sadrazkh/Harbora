using FluentAssertions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Authorization;
using Harbora.Domain.Common;
using Harbora.Domain.Identity;
using Harbora.Infrastructure.Deployments;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Who besides one named person could approve a deploy to one app (5.2, 2026-09 market-gaps round
/// two) — <see cref="DeploymentApproverFinder"/>, the workspace-parameterised twin of
/// <c>ProjectAccessService</c> that a webhook or a background sweep can ask, not only a signed-in
/// caller.
/// </summary>
public class DeploymentApproverFinderTests : IDisposable
{
    private readonly HarboraDbContext _db;
    private readonly Guid _workspace = Guid.NewGuid();
    private readonly Guid _requester = Guid.NewGuid();
    private readonly Guid _project = Guid.NewGuid();
    private readonly Guid _environment = Guid.NewGuid();
    private readonly Guid _appId = Guid.NewGuid();

    public DeploymentApproverFinderTests()
    {
        _db = new HarboraDbContext(new DbContextOptionsBuilder<HarboraDbContext>()
            .UseInMemoryDatabase("approver-finder-" + Guid.NewGuid()).Options);

        _db.Projects.Add(new Harbora.Domain.Projects.Project { Id = _project, WorkspaceId = _workspace, Name = "shop", Slug = "shop" });
        _db.Environments.Add(new Harbora.Domain.Projects.Environment
        { Id = _environment, WorkspaceId = _workspace, ProjectId = _project, Name = "production", Slug = "production", IsProtected = true });
        _db.Apps.Add(new App { Id = _appId, WorkspaceId = _workspace, EnvironmentId = _environment, ServerId = Guid.NewGuid(), Name = "web", Slug = "web" });
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private DeploymentApproverFinder Finder() => new(_db);

    private void AddMember(Guid userId, WorkspaceRole role, bool scoped, bool active = true,
        (Guid Project, Guid? Environment, Guid? App, SystemRole GrantRole)? grant = null)
    {
        _db.Users.Add(new User { Id = userId, Email = $"{userId}@example.com", DisplayName = "Someone", PasswordHash = "x", IsActive = active });
        _db.WorkspaceMembers.Add(new WorkspaceMember { WorkspaceId = _workspace, UserId = userId, Role = role, ScopedToProjects = scoped });
        if (grant is { } g)
            _db.ProjectGrants.Add(new Harbora.Domain.Authorization.ProjectGrant
            {
                WorkspaceId = _workspace, UserId = userId, ProjectId = g.Project,
                EnvironmentId = g.Environment, AppId = g.App, Role = g.GrantRole
            });
        _db.SaveChanges();
    }

    [Fact]
    public async Task An_unscoped_admin_is_eligible()
    {
        var admin = Guid.NewGuid();
        AddMember(admin, WorkspaceRole.Admin, scoped: false);

        var eligible = await Finder().EligibleApproversAsync(_appId, _workspace, Capabilities.AppsDeploy, _requester, default);

        eligible.Should().Contain(admin);
    }

    [Fact]
    public async Task A_scoped_member_with_no_grant_on_this_project_is_not_eligible()
    {
        var scopedNobody = Guid.NewGuid();
        AddMember(scopedNobody, WorkspaceRole.Member, scoped: true); // no ProjectGrant at all

        var eligible = await Finder().EligibleApproversAsync(_appId, _workspace, Capabilities.AppsDeploy, _requester, default);

        eligible.Should().NotContain(scopedNobody);
    }

    [Fact]
    public async Task A_scoped_member_granted_this_exact_app_is_eligible()
    {
        var scopedButGranted = Guid.NewGuid();
        AddMember(scopedButGranted, WorkspaceRole.Member, scoped: true,
            grant: (_project, null, _appId, SystemRole.Member));

        var eligible = await Finder().EligibleApproversAsync(_appId, _workspace, Capabilities.AppsDeploy, _requester, default);

        eligible.Should().Contain(scopedButGranted);
    }

    [Fact]
    public async Task A_viewer_never_qualifies_even_if_granted()
    {
        var viewer = Guid.NewGuid();
        AddMember(viewer, WorkspaceRole.Viewer, scoped: false);

        var eligible = await Finder().EligibleApproversAsync(_appId, _workspace, Capabilities.AppsDeploy, _requester, default);

        eligible.Should().NotContain(viewer, "RolePermissions never hands AppsDeploy to a Viewer");
    }

    [Fact]
    public async Task A_deactivated_account_is_excluded()
    {
        var deactivated = Guid.NewGuid();
        AddMember(deactivated, WorkspaceRole.Admin, scoped: false, active: false);

        var eligible = await Finder().EligibleApproversAsync(_appId, _workspace, Capabilities.AppsDeploy, _requester, default);

        eligible.Should().NotContain(deactivated);
    }

    [Fact]
    public async Task The_excluded_user_is_never_returned_even_if_otherwise_eligible()
    {
        AddMember(_requester, WorkspaceRole.Admin, scoped: false); // the requester happens to be an admin too

        var eligible = await Finder().EligibleApproversAsync(_appId, _workspace, Capabilities.AppsDeploy, _requester, default);

        eligible.Should().NotContain(_requester);
    }

    [Fact]
    public async Task Nobody_at_all_besides_the_requester_yields_an_empty_list()
    {
        var eligible = await Finder().EligibleApproversAsync(_appId, _workspace, Capabilities.AppsDeploy, _requester, default);

        eligible.Should().BeEmpty();
    }

    [Fact]
    public async Task A_grant_scoped_to_a_different_app_does_not_count()
    {
        var otherApp = Guid.NewGuid();
        var scopedElsewhere = Guid.NewGuid();
        AddMember(scopedElsewhere, WorkspaceRole.Member, scoped: true,
            grant: (_project, null, otherApp, SystemRole.Member));

        var eligible = await Finder().EligibleApproversAsync(_appId, _workspace, Capabilities.AppsDeploy, _requester, default);

        eligible.Should().NotContain(scopedElsewhere);
    }

    [Fact]
    public async Task An_app_that_does_not_exist_yields_an_empty_list_rather_than_throwing()
    {
        var eligible = await Finder().EligibleApproversAsync(Guid.NewGuid(), _workspace, Capabilities.AppsDeploy, _requester, default);

        eligible.Should().BeEmpty();
    }
}
