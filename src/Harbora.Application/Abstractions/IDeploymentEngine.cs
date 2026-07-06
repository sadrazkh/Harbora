namespace Harbora.Application.Abstractions;

/// <summary>
/// Orchestrates a deployment end-to-end (checkout → build → run → wire proxy → health check
/// → mark active). Runs on a background worker so the request thread returns immediately.
/// </summary>
public interface IDeploymentEngine
{
    /// <summary>Queue a deployment and return its id. Progress streams over <see cref="IDeploymentLogStream"/>.</summary>
    Task<Guid> QueueDeploymentAsync(DeploymentRequest request, CancellationToken ct);

    Task CancelAsync(Guid deploymentId, CancellationToken ct);
}

public record DeploymentRequest(
    Guid AppId,
    Domain.Common.DeploymentTrigger Trigger,
    Guid TriggeredByUserId,
    string? GitRef = null,
    string? CommitSha = null,
    Guid? RollbackToDeploymentId = null);

/// <summary>Publishes live log lines + status changes to subscribers (SignalR hub, CLI stream).</summary>
public interface IDeploymentLogStream
{
    Task PublishLogAsync(Guid deploymentId, Domain.Common.LogStream stream, string line, CancellationToken ct);
    Task PublishStatusAsync(Guid deploymentId, Domain.Common.DeploymentStatus status, CancellationToken ct);
}
