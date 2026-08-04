using Harbora.NodeAgent.Auditing;
using Harbora.NodeAgent.Runtime;
using Microsoft.Extensions.Logging;

namespace Harbora.NodeAgent.Hosting;

/// <summary>
/// Brings the runtime back in line with what the node intended, after a reboot or a crash.
///
/// <para>
/// Docker's own restart policies bring most containers back, but not all: a container removed while
/// the agent was down, or one whose restart policy is <c>no</c>, stays missing — and the control
/// plane would go on believing the workload is running because the node is heartbeating. This is
/// what makes "the node recovers its state" true rather than aspirational.
/// </para>
/// </summary>
public sealed class StateReconciler(
    WorkloadRegistry registry,
    WorkloadDeployer deployer,
    IContainerRuntime runtime,
    NodeAuditLog audit,
    ILogger<StateReconciler> log)
{
    public sealed record ReconciliationReport(int Checked, int Restarted, int Missing, IReadOnlyList<string> Problems);

    public async Task<ReconciliationReport> ReconcileAsync(CancellationToken ct)
    {
        var workloads = registry.All();
        if (workloads.Count == 0) return new ReconciliationReport(0, 0, 0, []);

        var info = await runtime.GetInfoAsync(ct);

        if (!info.Available)
        {
            // Reconciling against an unreachable daemon would conclude that every container is
            // missing and try to recreate all of them the moment Docker came back.
            log.LogWarning("Container runtime is unavailable; skipping reconciliation of {Count} workload(s).", workloads.Count);
            return new ReconciliationReport(0, 0, 0, ["container runtime unavailable"]);
        }

        var restarted = 0;
        var missing = 0;
        var problems = new List<string>();

        foreach (var record in workloads)
        {
            ct.ThrowIfCancellationRequested();

            var status = await deployer.StatusAsync(record, ct);

            switch (status.State)
            {
                case "running":
                    continue;

                case "absent":
                    missing++;
                    problems.Add($"{record.Name}: no containers present");

                    log.LogWarning(
                        "Workload {Workload} has no containers on this node. The control plane must redeploy it; " +
                        "the agent will not recreate containers from a stored spec on its own.",
                        record.Name);

                    audit.Write(new NodeAuditEntry
                    {
                        Action = "node.reconcile",
                        Outcome = "missing",
                        TargetType = "workload",
                        TargetId = record.WorkloadId,
                        TenantId = record.TenantId,
                        Detail = "containers absent after restart",
                    });
                    continue;

                default:
                    try
                    {
                        log.LogInformation("Restarting {Workload}, which came back {State}.", record.Name, status.State);
                        await deployer.StartAsync(record, ct);
                        restarted++;

                        audit.Write(new NodeAuditEntry
                        {
                            Action = "node.reconcile",
                            Outcome = "restarted",
                            TargetType = "workload",
                            TargetId = record.WorkloadId,
                            TenantId = record.TenantId,
                        });
                    }
                    catch (Exception e) when (e is ContainerRuntimeException or IOException)
                    {
                        problems.Add($"{record.Name}: {e.Message}");
                        log.LogError(e, "Could not restart {Workload} during reconciliation.", record.Name);
                    }

                    continue;
            }
        }

        log.LogInformation(
            "Reconciled {Checked} workload(s): {Restarted} restarted, {Missing} missing.",
            workloads.Count, restarted, missing);

        return new ReconciliationReport(workloads.Count, restarted, missing, problems);
    }
}
