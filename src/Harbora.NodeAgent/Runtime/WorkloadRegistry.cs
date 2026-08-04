using Harbora.NodeAgent.Contracts;
using Harbora.NodeAgent.State;

namespace Harbora.NodeAgent.Runtime;

/// <summary>What the node deployed, and what it replaced.</summary>
public sealed record WorkloadRecord
{
    public required string WorkloadId { get; init; }
    public required string TenantId { get; init; }
    public required string Name { get; init; }

    /// <summary>
    /// The spec as deployed, secrets included. This file is owner-only and the reason the whole
    /// state directory is 0700: a rollback has to be able to recreate the containers exactly, and
    /// re-asking the control plane for secrets during an outage is not a plan.
    /// </summary>
    public required WorkloadSpec Spec { get; init; }

    public required string ReleaseId { get; init; }
    public string? AppVersion { get; init; }

    /// <summary>Digest actually pulled per container — what is running, not what was asked for.</summary>
    public IReadOnlyDictionary<string, string> ResolvedDigests { get; init; } = new Dictionary<string, string>();

    /// <summary>Host ports allocated for this workload, keyed <c>container:port</c>.</summary>
    public IReadOnlyDictionary<string, int> AllocatedPorts { get; init; } = new Dictionary<string, int>();

    /// <summary>
    /// Hash of the spec as deployed. Lets a redelivered deploy after the idempotency window has
    /// expired be recognised as "already in this state" rather than restarting a healthy service.
    /// </summary>
    public string SpecFingerprint { get; init; } = string.Empty;

    public DateTimeOffset DeployedAt { get; init; }

    /// <summary>
    /// The release this one replaced. Exactly one level deep: a rollback is "undo the change that
    /// just broke", and keeping a chain would mean keeping every secret a workload ever had.
    /// </summary>
    public WorkloadRecord? Previous { get; init; }

    /// <summary>Container names as the runtime knows them, versioned by release so old and new can coexist.</summary>
    public string ContainerName(string container) => $"harbora-{Name}-{container}-{ReleaseId}";
}

public sealed record WorkloadRegistryState
{
    public IReadOnlyList<WorkloadRecord> Workloads { get; init; } = [];
}

/// <summary>
/// The node's own record of what it is running.
///
/// <para>
/// Docker labels carry the same facts and are the source of truth for "what exists"; this is the
/// source of truth for "what was intended", including the previous release. After a reboot the
/// two are reconciled — which is only possible because the intent survived the reboot too.
/// </para>
/// </summary>
public sealed class WorkloadRegistry(JsonFileStore<WorkloadRegistryState> store)
{
    private readonly Lock _gate = new();

    public IReadOnlyList<WorkloadRecord> All()
    {
        lock (_gate) return store.Load()?.Workloads ?? [];
    }

    /// <summary>
    /// Look up a workload for a tenant. The tenant is part of the key, not a filter applied after:
    /// a command carrying the wrong workload id must find nothing rather than someone else's row.
    /// </summary>
    public WorkloadRecord? Find(string workloadId, string? tenantId)
    {
        lock (_gate)
            return All().FirstOrDefault(w =>
                w.WorkloadId == workloadId &&
                (tenantId is null || w.TenantId == tenantId));
    }

    public void Save(WorkloadRecord record)
    {
        lock (_gate)
        {
            var state = store.Load() ?? new WorkloadRegistryState();

            var workloads = state.Workloads
                .Where(w => w.WorkloadId != record.WorkloadId)
                .Append(record)
                .ToList();

            store.Save(state with { Workloads = workloads });
        }
    }

    public void Remove(string workloadId)
    {
        lock (_gate)
        {
            var state = store.Load();
            if (state is null) return;

            store.Save(state with { Workloads = state.Workloads.Where(w => w.WorkloadId != workloadId).ToList() });
        }
    }

    /// <summary>Every host port this node has promised to a workload.</summary>
    public IReadOnlySet<int> AllocatedPorts()
    {
        lock (_gate)
            return All()
                .SelectMany(w => w.AllocatedPorts.Values)
                .ToHashSet();
    }
}

/// <summary>
/// Picks host ports for workloads that need to be reachable across nodes.
///
/// <para>
/// Deterministic given the same inputs, and it consults both the registry and the kernel's list of
/// listeners. Consulting only its own bookkeeping would collide with whatever else the customer
/// runs on their server, which is a class of bug that surfaces as "the deploy worked yesterday".
/// </para>
/// </summary>
public sealed class PortAllocator(PortAllocationOptions options)
{
    public sealed class NoPortsAvailableException(string message) : Exception(message);

    /// <summary>
    /// Allocate <paramref name="count"/> distinct free ports.
    /// <paramref name="inUse"/> should combine the registry's allocations with the host's listeners.
    /// </summary>
    public IReadOnlyList<int> Allocate(int count, IReadOnlySet<int> inUse)
    {
        var allocated = new List<int>(count);

        for (var port = options.Start; port <= options.End && allocated.Count < count; port++)
        {
            if (inUse.Contains(port) || allocated.Contains(port)) continue;
            allocated.Add(port);
        }

        if (allocated.Count < count)
            throw new NoPortsAvailableException(
                $"Needed {count} free host port(s) in {options.Start}–{options.End}; only {allocated.Count} were available.");

        return allocated;
    }

    public int AllocateOne(IReadOnlySet<int> inUse) => Allocate(1, inUse)[0];
}
