using Harbora.Domain.Billing;

namespace Harbora.Application.Abstractions;

/// <summary>
/// The one place that decides whether a workspace may start a workload right now.
///
/// <para>
/// One implementation, one interface, and a test that reads the source to find out who asks. Two
/// copies of a placement rule have already drifted apart silently in this codebase; a billing rule
/// with a second copy is a customer getting free hosting through whichever copy nobody updated.
/// </para>
///
/// <para>
/// It is asked at the LAST place before a container runs — the deployment pipeline, the app's
/// start route, the managed-service engine, the cron job runner — and not at the button that asked
/// for it. There are already eleven call sites that queue a deployment and five that queue a
/// provision, and the number only grows; a gate installed on the buttons would be a gate a new
/// button can be written without.
/// </para>
///
/// <para>
/// The answer is a <see cref="QuotaCheck"/> rather than a bool or a type of its own, so a refusal
/// for money renders exactly where a refusal for quota already does.
/// </para>
/// </summary>
public interface IBillingGate
{
    Task<QuotaCheck> CanStartAsync(Guid workspaceId, CancellationToken ct);

    /// <summary>
    /// Resource-aware form used by workload start paths. Implementations that do not distinguish a
    /// prepaid first hour retain the workspace-level rule; the production gate uses the identity to
    /// let exactly the already-paid resource start when its debit left the wallet at zero.
    /// </summary>
    Task<QuotaCheck> CanStartAsync(
        Guid workspaceId, BilledResourceType resourceType, Guid resourceId, CancellationToken ct) =>
        CanStartAsync(workspaceId, ct);
}
