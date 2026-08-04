using Harbora.NodeAgent.Auditing;
using Harbora.NodeAgent.Contracts;
using Harbora.NodeAgent.Observability;
using Harbora.NodeAgent.Runtime;
using Harbora.NodeAgent.State;
using Microsoft.Extensions.Logging;

namespace Harbora.NodeAgent.Updates;

/// <summary>
/// Takes a node out of service and puts it back.
///
/// <para>
/// Draining is a persisted flag, not a runtime one. A node that forgot it was draining after a
/// restart would accept the very deploy the operator drained it to avoid — and a reboot is the most
/// likely thing to happen to a node someone is draining before maintenance.
/// </para>
/// </summary>
public sealed class DrainCoordinator(
    JsonFileStore<NodeState> state,
    WorkloadRegistry registry,
    WorkloadDeployer deployer,
    NodeAuditLog audit,
    NodeMetrics metrics,
    INodeEventPublisher events,
    TimeProvider clock,
    ILogger<DrainCoordinator> log)
{
    public bool IsDraining => (state.Load() ?? new NodeState()).Draining;

    /// <summary>
    /// Stop accepting new work, and optionally stop what is already running.
    ///
    /// <para>
    /// The flag is set before anything is stopped: a deploy that arrives during the drain must be
    /// refused, and doing the slow part first would leave a window where it is accepted.
    /// </para>
    /// </summary>
    public async Task<DrainNodeResult> DrainAsync(
        bool stopWorkloads, TimeSpan timeout, string? reason, CancellationToken ct)
    {
        state.Update(s => (s ?? new NodeState()) with { Draining = true, DrainReason = reason });
        metrics.Draining(true);

        log.LogWarning("Node is draining{Reason}.", reason is null ? string.Empty : $" ({reason})");

        audit.Write(new NodeAuditEntry
        {
            Action = "node.drain",
            Outcome = "draining",
            Reason = reason,
            Detail = stopWorkloads ? "workloads will be stopped" : "existing workloads keep running",
        });

        await events.PublishAsync(new NodeEvent
        {
            Kind = NodeEventKinds.DrainStarted,
            Message = $"Node draining{(reason is null ? string.Empty : $": {reason}")}",
        }, ct);

        var workloads = registry.All();
        var stopped = 0;
        var deadline = clock.GetUtcNow() + timeout;
        var timedOut = false;

        if (stopWorkloads)
        {
            foreach (var record in workloads)
            {
                if (clock.GetUtcNow() >= deadline)
                {
                    // Reported rather than swallowed: an operator who asked for a drain and got a
                    // timeout needs to know which workloads are still up before they reboot.
                    timedOut = true;
                    log.LogWarning(
                        "Drain timed out after {Timeout}s with {Remaining} workload(s) still running.",
                        timeout.TotalSeconds, workloads.Count - stopped);
                    break;
                }

                try
                {
                    await deployer.StopAsync(record, ct);
                    stopped++;
                }
                catch (Exception e) when (e is ContainerRuntimeException or IOException)
                {
                    log.LogError(e, "Could not stop {Workload} while draining.", record.Name);
                }
            }
        }

        var remaining = workloads.Count - stopped;

        await events.PublishAsync(new NodeEvent
        {
            Kind = NodeEventKinds.DrainCompleted,
            Message = $"Drain finished: {stopped} stopped, {remaining} still running",
        }, ct);

        return new DrainNodeResult
        {
            Draining = true,
            WorkloadsStopped = stopped,
            WorkloadsRemaining = remaining,
            TimedOut = timedOut,
        };
    }

    /// <summary>
    /// Put the node back in service. Workloads stopped by a drain are not restarted automatically —
    /// the control plane knows what should be running here and the node only knows what was.
    /// </summary>
    public async Task<DrainNodeResult> UndrainAsync(CancellationToken ct)
    {
        if (!IsDraining)
            return new DrainNodeResult { Draining = false, WorkloadsRemaining = registry.All().Count };

        state.Update(s => (s ?? new NodeState()) with { Draining = false, DrainReason = null });
        metrics.Draining(false);

        log.LogInformation("Node is back in service.");

        audit.Write(new NodeAuditEntry { Action = "node.drain", Outcome = "resumed" });

        await events.PublishAsync(new NodeEvent
        {
            Kind = NodeEventKinds.DrainCompleted,
            Message = "Node is back in service",
        }, ct);

        return new DrainNodeResult { Draining = false, WorkloadsRemaining = registry.All().Count };
    }
}
