using Harbora.Domain.Deployments;

namespace Harbora.Infrastructure.Deployments;

/// <summary>
/// The rules of one approval decision, isolated from the database so they can be read and tested on
/// their own (5.2, 2026-09 market-gaps round two, "approval gate on deploying to a protected
/// environment") — the same reason <see cref="PromotionPlan"/> is its own static class rather than
/// inline in a controller.
/// </summary>
public static class DeploymentApprovalPlan
{
    /// <summary>
    /// Why this decision cannot be recorded, or null when it can.
    ///
    /// <para>
    /// The self-approval refusal is the whole point of the feature: a requester approving their own
    /// deploy is not a second person having looked at it, it is the first person clicking twice. No
    /// role bypasses this — an Owner approving their own request has the identical problem an Owner
    /// approving anyone else's does not.
    /// </para>
    /// </summary>
    public static string? RefuseDecision(
        Guid requesterUserId, Guid deciderUserId, DeploymentApprovalDecision current)
    {
        if (current != DeploymentApprovalDecision.Pending)
            return $"This deployment's approval was already {current} — there is nothing left to decide.";

        if (requesterUserId == deciderUserId)
            return "You requested this deployment, so you cannot also approve or reject it — " +
                   "a second person has to.";

        return null;
    }

    /// <summary>
    /// What happens when nobody else in the workspace could ever approve this.
    ///
    /// <para>
    /// <b>The choice, and why.</b> Silently letting the requester approve their own release defeats
    /// the feature by name — the one thing this gate exists to stop. Blocking the deploy for ever is
    /// almost as bad: a solo workspace or a contractor scoped alone to one environment would turn on
    /// protection and discover their own deploys can never ship again, with no error to explain why
    /// and no second person who could ever fix it by approving. The gate exists to make someone else
    /// look before production changes, not to make production unreachable when there is no one else
    /// to ask — so when <paramref name="eligibleApproverCount"/> is zero, this deploys immediately,
    /// exactly as if the environment were unprotected, and says so loudly rather than quietly: the
    /// audit row and the panel both name this deployment as self-approved for lack of anyone else,
    /// which is the one thing a silent bypass would never do.
    /// </para>
    /// </summary>
    public static bool AutoApproveForLackOfSecondApprover(int eligibleApproverCount) =>
        eligibleApproverCount <= 0;
}
