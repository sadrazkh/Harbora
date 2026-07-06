using Harbora.Application.Abstractions;
using Harbora.Domain.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Harbora.Web.Realtime;

/// <summary>
/// Live log/status feed for a deployment. Clients join the group for a deployment id and
/// receive "log" and "status" events; the pipeline pushes through <see cref="SignalRDeploymentLogStream"/>.
/// </summary>
[Authorize]
public sealed class DeploymentHub : Hub
{
    public Task Subscribe(string deploymentId) =>
        Groups.AddToGroupAsync(Context.ConnectionId, Group(deploymentId));

    public Task Unsubscribe(string deploymentId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, Group(deploymentId));

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
