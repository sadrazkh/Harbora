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
    Guid? RollbackToDeploymentId = null,
    /// <summary>Set when the source was pushed from a developer's machine rather than pulled from Git.</summary>
    string? SourceArchivePath = null,
    /// <summary>Release this exact image instead of building anything (`harbora deploy --image`).</summary>
    string? ImageOverride = null);

/// <summary>Publishes live log lines + status changes to subscribers (SignalR hub, CLI stream).</summary>
public interface IDeploymentLogStream
{
    Task PublishLogAsync(Guid deploymentId, Domain.Common.LogStream stream, string line, CancellationToken ct);
    Task PublishStatusAsync(Guid deploymentId, Domain.Common.DeploymentStatus status, CancellationToken ct);
}

/// <summary>
/// A deploy was refused at queue time because the node it would run on does not have enough free
/// disk left — P7 (2026-08-17 app-environment-management design), the owner's answer to §7 Q5:
/// this refuses rather than warns, because a warning at this figure already exists
/// (<c>MetricsCollector</c>'s <c>DiskWarnRatio</c> path) and would deliver nothing new.
///
/// <para>
/// Names both numbers, not just the fact of the refusal: <see cref="FreeBytes"/> is what the node
/// reported free, <see cref="ThresholdBytes"/> is <c>MonitoringOptions.DeployMinFreeDiskBytes</c> —
/// the figure it refused against, kept on the exception rather than only in the message string so a
/// test (or an operator's tooling) can assert on the reason and not merely on the fact that some
/// reason fired.
/// </para>
/// </summary>
public sealed class LowDiskRefusedException(long freeBytes, long thresholdBytes, string reason, string? reasonFa = null)
    : InvalidOperationException(reason)
{
    public long FreeBytes { get; } = freeBytes;
    public long ThresholdBytes { get; } = thresholdBytes;

    /// <summary>The same refusal in Persian.</summary>
    public string? ReasonFa { get; } = reasonFa;
}
