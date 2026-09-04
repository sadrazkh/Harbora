namespace Harbora.Application.Abstractions;

/// <summary>
/// Orchestrates a deployment end-to-end (checkout → build → run → wire proxy → health check
/// → mark active). Runs on a background worker so the request thread returns immediately.
/// </summary>
public interface IDeploymentEngine
{
    /// <summary>
    /// Queue a deployment and return its id. Progress streams over <see cref="IDeploymentLogStream"/>.
    ///
    /// <para>
    /// 5.2 (2026-09 market-gaps round two): when the target app's environment is protected, the
    /// returned deployment is not queued at all — it is created
    /// <see cref="Domain.Common.DeploymentStatus.PendingApproval"/>, with no <c>Job</c> behind it,
    /// and stays there until <see cref="ApproveAsync"/> or <see cref="RejectAsync"/> settles it (or
    /// the expiry sweep does). One exception: a workspace with nobody else eligible to approve this
    /// app deploys immediately, exactly as if unprotected — see
    /// <c>DeploymentApprovalPlan.AutoApproveForLackOfSecondApprover</c> for why.
    /// </para>
    /// </summary>
    Task<Guid> QueueDeploymentAsync(DeploymentRequest request, CancellationToken ct);

    Task CancelAsync(Guid deploymentId, CancellationToken ct);

    /// <summary>
    /// Approves a deployment that is waiting on a protected environment's gate, and — if the same
    /// checks an ordinary deploy passes at queue time still pass now — enqueues it. Throws
    /// <see cref="InvalidOperationException"/> naming the reason when the deployment is not pending
    /// approval, when its approval was already decided, or when <paramref name="approverUserId"/> is
    /// the person who requested it.
    /// </summary>
    Task ApproveAsync(Guid deploymentId, Guid approverUserId, CancellationToken ct);

    /// <summary>
    /// Rejects a deployment that is waiting on a protected environment's gate. The deployment ends
    /// <see cref="Domain.Common.DeploymentStatus.Cancelled"/>; the reason is what the panel and the
    /// audit log both read to say this was a rejection rather than an ordinary cancel. Throws
    /// <see cref="InvalidOperationException"/> for the same reasons <see cref="ApproveAsync"/> does,
    /// plus an empty <paramref name="reason"/>.
    /// </summary>
    Task RejectAsync(Guid deploymentId, Guid approverUserId, string reason, CancellationToken ct);
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
    string? ImageOverride = null,
    /// <summary>
    /// Skip the build cache entirely: no previous image is named as a cache source and the engine's
    /// own layer cache is bypassed too. Stamped onto the queued <c>Deployment</c> row as-is (the
    /// pipeline runs on a background worker with only the row's id, not this request) and read back
    /// from there. Ignored for a rollback, which never rebuilds.
    /// </summary>
    bool ForceRebuild = false);

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
