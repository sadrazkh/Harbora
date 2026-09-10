using Harbora.Application.Abstractions;
using Harbora.Domain.Common;
using Harbora.Data;
using Harbora.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Harbora.Web.Realtime;

/// <summary>
/// Live log/status feed for a deployment. Clients join the group for a deployment id and
/// receive "log" and "status" events; the pipeline pushes through <see cref="SignalRDeploymentLogStream"/>.
/// </summary>
[Authorize]
public sealed class DeploymentHub(HarboraDbContext db, ICurrentUser currentUser, ProjectAccessService access) : Hub
{
    public async Task Subscribe(string deploymentId)
    {
        if (!currentUser.IsAuthenticated || currentUser.WorkspaceId is not { } workspaceId
            || !Guid.TryParse(deploymentId, out var id))
            throw new HubException("Deployment not found.");

        var ct = Context.ConnectionAborted;
        var appId = await db.Deployments.AsNoTracking()
            .Where(d => d.Id == id && d.App!.WorkspaceId == workspaceId)
            .Select(d => (Guid?)d.AppId).FirstOrDefaultAsync(ct);
        if (appId is null || !await access.CanSeeAppAsync(appId.Value, ct))
            throw new HubException("Deployment not found.");

        await Groups.AddToGroupAsync(Context.ConnectionId, Group(id.ToString()), ct);
    }

    public Task Unsubscribe(string deploymentId) => Guid.TryParse(deploymentId, out var id)
        ? Groups.RemoveFromGroupAsync(Context.ConnectionId, Group(id.ToString()), Context.ConnectionAborted)
        : Task.CompletedTask;

    public static string Group(string deploymentId) => $"deployment:{deploymentId}";
}

/// <summary>Publishes pipeline logs/status to the hub group for a deployment.</summary>
public sealed class SignalRDeploymentLogStream(IHubContext<DeploymentHub> hub) : IDeploymentLogStream
{
    public Task PublishLogAsync(Guid deploymentId, LogStream stream, string line, CancellationToken ct) =>
        hub.Clients.Group(DeploymentHub.Group(deploymentId.ToString()))
            .SendAsync("log", new { stream = stream.ToString(), line, ts = DateTimeOffset.UtcNow }, ct);

    public Task PublishStatusAsync(Guid deploymentId, DeploymentStatus status, CancellationToken ct) =>
        hub.Clients.Group(DeploymentHub.Group(deploymentId.ToString()))
            .SendAsync("status", new { status = status.ToString() }, ct);
}
