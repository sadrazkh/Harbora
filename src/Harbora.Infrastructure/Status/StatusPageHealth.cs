using Harbora.Domain.Common;
using Harbora.Domain.Monitoring;

namespace Harbora.Infrastructure.Status;

/// <summary>
/// What the public status page may say about one component — deliberately four words, not the six
/// <see cref="AppStatus"/> itself carries, because a customer's own users do not need (and were never
/// promised) the panel's internal vocabulary.
/// </summary>
public enum PublicAppState
{
    /// <summary>Serving traffic — including mid a zero-downtime deploy, where the previous release still is.</summary>
    Operational,

    /// <summary>Known, and not fully up: deliberately stopped, crashed, or failed to start.</summary>
    Degraded,

    /// <summary>
    /// The owner has deliberately put this app in maintenance (<c>App.MaintenanceMode</c>, P5) —
    /// outranks every other signal, since it says more about what a visitor sees right now than any
    /// combination of <see cref="AppStatus"/> and deployment history could.
    /// </summary>
    Maintenance,

    /// <summary>Nothing has ever been observed running. Never rendered as a comfortable dot.</summary>
    Unknown
}

/// <summary>
/// Turns <see cref="AppStatus"/> — the exact column <c>AppsController.Index</c> reads for the panel's
/// own Apps list, reconciled locally and on remote nodes alike by <c>MetricsCollector</c>'s
/// <c>ReconcileAppStatusesAsync</c> (via <c>IWorkloadEngine.ListContainersAsync</c>, which works on a
/// v2 node) — into the public page's four-word vocabulary.
///
/// <para>
/// Deliberately <b>not</b> a live per-request Docker/node inspection. <c>AppsController.Overview</c>'s
/// "specifics" card asks a container directly (<c>TryInspectAsync</c>), and that path is honestly
/// unreliable for a remote v2 node today — <c>NodeWorkloadEngine.InspectAsync</c> returns null for
/// every call because the node command contract has no inspect verb yet, which is exactly the
/// "unknown health" case this sub-project was warned about. Building a second, less reliable
/// derivation of "is it up" next to the one <c>MetricsCollector</c> already reconciles would be the
/// drift the plan explicitly forbids — one source, asked the same way the Apps list already does.
/// </para>
/// </summary>
public static class StatusPageHealth
{
    /// <param name="status">The app's own <see cref="AppStatus"/> column.</param>
    /// <param name="hasEverServed">
    /// True once the app has had an <c>ActiveDeploymentId</c> — the same "has this shipped a working
    /// release" fact <c>AppsController.Overview</c> already falls back on to name its own container.
    /// An app on its very first, still-running deploy has never served anything: the honest answer is
    /// Unknown, not a green dot for a deploy that has not finished.
    /// </param>
    /// <param name="maintenanceMode">
    /// <c>App.MaintenanceMode</c> — written only after the proxy apply that turns it on has actually
    /// succeeded (that entity's own doc), so trusting it here carries the same guarantee the panel's
    /// own Apps/Details page trusts it with. Checked first: a deliberately maintaining app is not
    /// "degraded" because its containers are momentarily unobserved, and is not "operational" because
    /// AppStatus still says Running underneath the redirect.
    /// </param>
    /// <param name="latestProbeOutcome">
    /// 2.1 (2026-09 market-gaps round two): the app's own <c>UptimeCheck.LastOutcome</c>, when one is
    /// configured and has run at least once — null otherwise (no check configured, or configured but
    /// never yet run). <b>This is the fix for the exact gap 2.1 was written to close</b>: before this
    /// parameter existed, every state below came from <paramref name="status"/> alone — what Harbora
    /// believes it started, never from anything that actually answered a request. When this is not
    /// null it overrides everything below except maintenance mode, because an outside-in probe result is
    /// strictly more informative than a deploy-time status column about "is this thing answering
    /// visitors right now" — that is the one question a status page exists to answer.
    /// <see cref="UptimeCheckOutcome.CouldNotRun"/> maps to <see cref="PublicAppState.Unknown"/>,
    /// deliberately — never <see cref="PublicAppState.Operational"/> (a green dot for a probe that
    /// never fired) and never <see cref="PublicAppState.Degraded"/> (blaming the app for a question the
    /// checker never managed to ask it).
    /// </param>
    public static PublicAppState Resolve(
        AppStatus status, bool hasEverServed, bool maintenanceMode, UptimeCheckOutcome? latestProbeOutcome = null)
    {
        if (maintenanceMode) return PublicAppState.Maintenance;

        if (latestProbeOutcome is { } probe)
            return probe switch
            {
                UptimeCheckOutcome.Up => PublicAppState.Operational,
                UptimeCheckOutcome.Down => PublicAppState.Degraded,
                _ => PublicAppState.Unknown
            };

        return status switch
        {
            AppStatus.Created => PublicAppState.Unknown,
            AppStatus.Deploying => hasEverServed ? PublicAppState.Operational : PublicAppState.Unknown,
            AppStatus.Running => PublicAppState.Operational,
            AppStatus.Stopped => PublicAppState.Degraded,
            AppStatus.Failed => PublicAppState.Degraded,
            AppStatus.Crashed => PublicAppState.Degraded,
            _ => PublicAppState.Unknown
        };
    }
}
