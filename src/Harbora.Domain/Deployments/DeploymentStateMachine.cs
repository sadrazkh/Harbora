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
///
/// <para>
/// 5.2 (2026-09 market-gaps round two): a deploy to a protected environment starts at
/// <see cref="DeploymentStatus.PendingApproval"/> instead of <see cref="DeploymentStatus.Queued"/>
/// and sits there — no <c>Job</c> exists for it yet — until an approval turns it into an ordinary
/// Queued deployment, or a rejection or an expiry turns it into a Cancelled one.
/// </para>
/// </summary>
public static class DeploymentStateMachine
{
    private static readonly IReadOnlyDictionary<DeploymentStatus, DeploymentStatus[]> Allowed =
        new Dictionary<DeploymentStatus, DeploymentStatus[]>
        {
            // 5.2: approved moves it into the ordinary queue; rejected or expired moves it to
            // Cancelled — the same terminal status a requester's own Cancel already uses, because
            // both mean the same thing to everything downstream ("this did not run"). What
            // distinguishes a rejection or an expiry from an ordinary cancel is the DeploymentApproval
            // row, which is what the panel reads to say which one actually happened.
            [DeploymentStatus.PendingApproval] = [DeploymentStatus.Queued, DeploymentStatus.Cancelled],
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

    /// <summary>
    /// In-flight (non-terminal) states that a process restart can strand mid-work — the set
    /// <c>DeploymentReconciler</c> sweeps on startup. Deliberately excludes
    /// <see cref="DeploymentStatus.PendingApproval"/>: nothing is working on it (no <c>Job</c> exists
    /// for it yet) and a restart must never fail a deployment that is correctly, deliberately, doing
    /// nothing while it waits on a person. See <see cref="Unsettled"/> for the wider set that
    /// includes it.
    /// </summary>
    public static readonly IReadOnlySet<DeploymentStatus> InFlight = new HashSet<DeploymentStatus>
    {
        DeploymentStatus.Queued, DeploymentStatus.Building, DeploymentStatus.Pushing,
        DeploymentStatus.Deploying, DeploymentStatus.HealthChecking
    };

    /// <summary>The statuses a deployment is actually over in. Kept as its own set rather than
    /// "not <see cref="InFlight"/>" so that <see cref="DeploymentStatus.PendingApproval"/> — neither
    /// stranded by a restart nor finished — can be neither without becoming both.</summary>
    private static readonly IReadOnlySet<DeploymentStatus> Terminal = new HashSet<DeploymentStatus>
    {
        DeploymentStatus.Succeeded, DeploymentStatus.Failed,
        DeploymentStatus.Cancelled, DeploymentStatus.RolledBack
    };

    /// <summary>
    /// <see cref="InFlight"/> plus <see cref="DeploymentStatus.PendingApproval"/> — every status that
    /// still has a future, for the one question that has to ask about both: "does this app already
    /// have an unsettled deployment". A second deploy request arriving while one sits in
    /// PendingApproval must be coalesced or refused the same way one arriving while another is mid
    /// -build already is, or a protected environment could accumulate a queue of parallel approval
    /// requests for the same app instead of the one at a time every other status already enforces.
    /// </summary>
    public static readonly IReadOnlySet<DeploymentStatus> Unsettled =
        new HashSet<DeploymentStatus>(InFlight) { DeploymentStatus.PendingApproval };

    public static bool IsTerminal(DeploymentStatus status) => Terminal.Contains(status);

    public static bool IsInFlight(DeploymentStatus status) => InFlight.Contains(status);

    /// <summary>Whether a deployment still has a future — in flight, or waiting on approval.</summary>
    public static bool IsUnsettled(DeploymentStatus status) => Unsettled.Contains(status);

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
