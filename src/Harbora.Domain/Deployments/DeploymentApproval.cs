using Harbora.Domain.Common;

namespace Harbora.Domain.Deployments;

/// <summary>What has happened to one approval request. Persisted by value — never renumber.</summary>
public enum DeploymentApprovalDecision
{
    /// <summary>Waiting on a second person, or on the expiry sweep.</summary>
    Pending = 0,
    Approved = 1,
    Rejected = 2,

    /// <summary>Nobody answered before <see cref="DeploymentApproval.ExpiresAt"/>. Distinct from
    /// <see cref="Rejected"/>: nobody said no, nobody said anything.</summary>
    Expired = 3
}

/// <summary>
/// One protected-environment deploy's approval request (5.2, 2026-09 market-gaps round two,
/// "approval gate on deploying to a protected environment").
///
/// <para>
/// One row per <see cref="Deployment"/>, not per app: <c>Deployment</c> is immutable history and a
/// retry or a fresh request mints a new row (<c>DeploymentsController.Retry</c>'s own doc gives the
/// same reasoning for why a retry does not reopen the deployment it retries), so a second request
/// for the same app gets its own approval cycle rather than reanimating a decided one.
/// </para>
///
/// <para>
/// The requester is not a column here — it is <c>Deployment.TriggeredByUserId</c>, which already
/// exists and already means exactly that. Recording it twice would be two sources of truth for one
/// fact, and the trap this codebase's own audit doc warns about is exactly that kind of drift.
/// </para>
/// </summary>
public class DeploymentApproval : BaseEntity
{
    public Guid DeploymentId { get; set; }
    public Deployment? Deployment { get; set; }

    /// <summary>Denormalised from the deployment, the same reason <see cref="Deployment.WorkspaceId"/>
    /// is — a direct comparison rather than a join that can hide a row whose parent is momentarily
    /// absent.</summary>
    public Guid WorkspaceId { get; set; }

    public DateTimeOffset RequestedAt { get; set; }

    /// <summary>
    /// When this stops waiting on its own. Read by the expiry sweep and shown on the pending-approval
    /// banner before it happens — a deadline nobody can see coming is not a safeguard, it is a
    /// surprise.
    /// </summary>
    public DateTimeOffset ExpiresAt { get; set; }

    public DeploymentApprovalDecision Decision { get; set; } = DeploymentApprovalDecision.Pending;

    /// <summary>Null for <see cref="DeploymentApprovalDecision.Expired"/> — nobody decided, the
    /// clock did.</summary>
    public Guid? DecidedByUserId { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }

    /// <summary>Why. Required for a rejection; optional for an approval.</summary>
    public string? ReasonText { get; set; }

    /// <summary>
    /// True when the workspace had nobody eligible to approve this besides the requester, so the
    /// gate approved it itself rather than block the deploy for ever or let the requester approve
    /// their own release — see <c>DeploymentApprovalPlan.SoleApproverOutcome</c> for the choice this
    /// records. Never true together with a human <see cref="DecidedByUserId"/>.
    /// </summary>
    public bool AutoApprovedNoSecondApprover { get; set; }

    /// <summary>
    /// True when <see cref="Deployment.GitRef"/> was rewritten from a branch/tag name to the exact
    /// commit it resolved to at the moment this request was made, so approving it — however long
    /// that takes — releases the commit that was reviewed, never a later push to the same branch.
    /// False for a deployment with no Git repository to pin (an image, an upload, a static bundle —
    /// already pinned to one artifact by construction) or when the resolution itself failed, in
    /// which case the deployment still deploys, but whatever <see cref="Deployment.GitRef"/> names
    /// at the moment it actually builds — stated on the same banner, not left to be discovered after
    /// the fact.
    /// </summary>
    public bool CommitPinned { get; set; }
}
