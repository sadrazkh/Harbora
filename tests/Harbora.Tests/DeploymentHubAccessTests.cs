using System.Security.Claims;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Authorization;
using Harbora.Domain.Deployments;
using Harbora.Domain.Identity;
using Harbora.Infrastructure.Security;
using Harbora.Web.Realtime;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

public class DeploymentHubAccessTests
{
    [Theory]
    [InlineData("own", true)]
    [InlineData("foreign", false)]
    [InlineData("scoped", false)]
    [InlineData("granted", true)]
    [InlineData("missing", false)]
    [InlineData("invalid", false)]
    [InlineData("removed", false)]
    public async Task Subscription_requires_workspace_and_app_visibility(string scenario, bool allowed)
    {
        await using var db = new HarboraDbContext(new DbContextOptionsBuilder<HarboraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var workspace = Guid.NewGuid();
        var user = new Caller(workspace);
        var app = new App { WorkspaceId = scenario == "foreign" ? Guid.NewGuid() : workspace, Name = "app", Slug = "app" };
        var deployment = new Deployment { App = app, AppId = app.Id, WorkspaceId = app.WorkspaceId };
        db.Apps.Add(app);
        db.Deployments.Add(deployment);
        if (scenario != "removed") db.WorkspaceMembers.Add(new WorkspaceMember
        {
            WorkspaceId = workspace, UserId = user.UserId!.Value,
            Role = WorkspaceRole.Member, ScopedToProjects = scenario is "scoped" or "granted"
        });
        if (scenario == "granted") db.ProjectGrants.Add(new ProjectGrant
        {
            WorkspaceId = workspace, UserId = user.UserId!.Value, AppId = app.Id
        });
        await db.SaveChangesAsync();
        var groups = new Groups();
        var hub = new DeploymentHub(db, user, new ProjectAccessService(db, user))
        { Context = new Context(), Groups = groups };
        var id = scenario == "invalid" ? "not-a-guid" : scenario == "missing" ? Guid.NewGuid().ToString() : deployment.Id.ToString().ToUpperInvariant();
        if (allowed)
        {
            await hub.Subscribe(id);
            Assert.Equal(DeploymentHub.Group(deployment.Id.ToString()), Assert.Single(groups.Joined));
            await hub.Unsubscribe(id);
            Assert.Equal(groups.Joined[0], Assert.Single(groups.Left));
        }
        else
        {
            var error = await Assert.ThrowsAsync<HubException>(() => hub.Subscribe(id));
            Assert.Equal("Deployment not found.", error.Message);
            Assert.Empty(groups.Joined);
        }
    }

    private sealed class Caller(Guid workspace) : ICurrentUser
    {
        public Guid? UserId { get; } = Guid.NewGuid();
        public Guid? WorkspaceId => workspace;
        public string? Email => "test@example.com";
        public bool IsAuthenticated => true;
    }
    private sealed class Groups : IGroupManager
    {
        public List<string> Joined { get; } = [];
        public List<string> Left { get; } = [];
        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
        { Joined.Add(groupName); return Task.CompletedTask; }
        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
        { Left.Add(groupName); return Task.CompletedTask; }
    }
    private sealed class Context : HubCallerContext
    {
        public override string ConnectionId => "test";
        public override string? UserIdentifier => "test";
        public override ClaimsPrincipal? User => new();
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted => CancellationToken.None;
        public override void Abort() { }
    }
}
