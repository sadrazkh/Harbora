using Harbora.Domain.Common;

namespace Harbora.Domain.Deployments;

/// <summary>
/// The single source of truth for legal <see cref="DeploymentStatus"/> transitions (ADR-004).
/// Deployment status must only change through <see cref="Transition"/> so the lifecycle stays
/// observable, testable, and recoverable — no ad-hoc field writes scattered across the pipeline.
///
/// Flow: Queued → Building → (Pushing) → Deploying → HealthChecking → Succeeded, with the health
/// step skipped for services that have no container to check (scheduled jobs, release tasks).
/// Any in-flight state may go to Failed or Cancelled. A Succeeded deployment may later be marked
/// RolledBack when a rollback re-releases a different image.
/// </summary>
public static class DeploymentStateMachine
{
    private static readonly IReadOnlyDictionary<DeploymentStatus, DeploymentStatus[]> Allowed =
        new Dictionary<DeploymentStatus, DeploymentStatus[]>
        {
            [DeploymentStatus.Queued]         = [DeploymentStatus.Building, DeploymentStatus.Cancelled, DeploymentStatus.Failed],
            [DeploymentStatus.Building]       = [DeploymentStatus.Pushing, DeploymentStatus.Deploying, DeploymentStatus.Failed, DeploymentStatus.Cancelled],
            [DeploymentStatus.Pushing]        = [DeploymentStatus.Deploying, DeploymentStatus.Failed, DeploymentStatus.Cancelled],
            // Succeeded is reachable directly from Deploying for a service with no long-running
            // container — a scheduled job or a release task. There is nothing to health-check, and
            // passing through HealthChecking would record a check that never happened.
            [DeploymentStatus.Deploying]      = [DeploymentStatus.HealthChecking, DeploymentStatus.Succeeded, DeploymentStatus.Failed, DeploymentStatus.Cancelled],
            [DeploymentStatus.HealthChecking] = [DeploymentStatus.Succeeded, DeploymentStatus.Failed, DeploymentStatus.Cancelled],
            [DeploymentStatus.Succeeded]      = [DeploymentStatus.RolledBack],
            [DeploymentStatus.Failed]         = [],
            [DeploymentStatus.Cancelled]      = [],
            [DeploymentStatus.RolledBack]     = [],
        };

    /// <summary>In-flight (non-terminal) states — the ones a restart can strand.</summary>
    public static readonly IReadOnlySet<DeploymentStatus> InFlight = new HashSet<DeploymentStatus>
    {
        DeploymentStatus.Queued, DeploymentStatus.Building, DeploymentStatus.Pushing,
        DeploymentStatus.Deploying, DeploymentStatus.HealthChecking
    };

    public static bool IsTerminal(DeploymentStatus status) => !InFlight.Contains(status);

    public static bool IsInFlight(DeploymentStatus status) => InFlight.Contains(status);

    /// <summary>
    /// Terminal states the deployment reached by <b>succeeding</b> — the release it was asked to
    /// make happened and is live. <see cref="DeploymentStatus.RolledBack"/> is one of them: it is
    /// what a succeeded deployment becomes when a later one supersedes it, and it says nothing
    /// about this deployment having failed.
    ///
    /// <para>
    /// Asked by anything that runs <i>after</i> the success transition and might otherwise report a
    /// failure: from that point the database already records that this deployment worked, and a
    /// fault in what follows — image retention, the terminal log line, whatever is added beside
    /// them — is a fact about that work and not about the release. Reporting it as the
    /// deployment's failure makes the live status contradict the stored row, which is the platform
    /// lying about a deployment.
    /// </para>
    /// </summary>
    public static bool IsSuccessful(DeploymentStatus status) =>
        status is DeploymentStatus.Succeeded or DeploymentStatus.RolledBack;

    public static bool CanTransition(DeploymentStatus from, DeploymentStatus to) =>
        from != to && Allowed.TryGetValue(from, out var next) && Array.IndexOf(next, to) >= 0;

    /// <summary>
    /// Validate and apply a transition on a deployment, stamping timestamps. Throws
    /// <see cref="InvalidOperationException"/> for an illegal transition.
    /// </summary>
    public static void Transition(Deployment deployment, DeploymentStatus to, DateTimeOffset now)
    {
        var from = deployment.Status;
        if (!CanTransition(from, to))
            throw new InvalidOperationException(
                $"Illegal deployment transition {from} → {to} (deployment #{deployment.Number}).");

        if (from == DeploymentStatus.Queued && to == DeploymentStatus.Building)
            deployment.StartedAt ??= now;

        if (IsTerminal(to))
            deployment.FinishedAt ??= now;

        deployment.Status = to;
    }
}
