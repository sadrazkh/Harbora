namespace Harbora.Application.Abstractions;

/// <summary>
/// Works out — before anything is queued — whether a rollback to a given deployment can actually
/// happen, and what the user would be rolling back to. Artifact rollback re-releases a stored image
/// (ADR-006), so it can be blocked by retention having pruned that image; finding that out
/// mid-deploy is a bad experience precisely when something is already broken.
/// </summary>
public interface IRollbackPlanner
{
    Task<RollbackPlan> PrepareAsync(Guid appId, Guid targetDeploymentId, CancellationToken ct);
}

/// <param name="CanRollback">False when the rollback must not be offered; <paramref name="Reason"/> says why.</param>
/// <param name="Reason">Human-readable blocker, or null when the rollback is possible.</param>
/// <param name="TargetNumber">Deployment number being rolled back to.</param>
/// <param name="ImageTag">The artifact that would be re-released.</param>
/// <param name="CurrentNumber">The deployment currently serving traffic, if any.</param>
public record RollbackPlan(
    bool CanRollback,
    string? Reason,
    int TargetNumber,
    string? ImageTag,
    string? CommitSha,
    string? CommitMessage,
    string? CommitAuthor,
    DateTimeOffset? DeployedAt,
    int? CurrentNumber,
    string? CurrentCommitSha);
