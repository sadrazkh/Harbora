using Harbora.NodeAgent.Contracts;
using Harbora.NodeAgent.State;

namespace Harbora.NodeAgent.Runtime;

public sealed record RouteRecord
{
    public required string RouteId { get; init; }
    public required string TenantId { get; init; }
    public required string WorkloadId { get; init; }

    /// <summary><c>http</c> or <c>tcp</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>Where the control plane's proxy should send traffic, as <c>host:port</c>.</summary>
    public required string Endpoint { get; init; }

    public string? Domain { get; init; }
    public int? GatewayPort { get; init; }
    public DateTimeOffset RegisteredAt { get; init; }
}

public sealed record RouteRegistryState
{
    public IReadOnlyList<RouteRecord> Routes { get; init; } = [];
}

/// <summary>
/// Routes this node has published.
///
/// <para>
/// The node does not run the reverse proxy — Harbora's Traefik lives with the control plane, and
/// cross-node traffic reaches a workload through a published host port. What the node owns is the
/// endpoint behind the route, so this records the mapping and survives a restart. Without it,
/// <c>RemoveRoute</c> after a reboot would have nothing to remove and would report success.
/// </para>
/// </summary>
public sealed class RouteRegistry(JsonFileStore<RouteRegistryState> store)
{
    private readonly Lock _gate = new();

    public IReadOnlyList<RouteRecord> All()
    {
        lock (_gate) return store.Load()?.Routes ?? [];
    }

    public RouteRecord? Find(string routeId, string? tenantId)
    {
        lock (_gate)
            return All().FirstOrDefault(r => r.RouteId == routeId && (tenantId is null || r.TenantId == tenantId));
    }

    public IReadOnlyList<RouteRecord> ForWorkload(string workloadId)
    {
        lock (_gate) return All().Where(r => r.WorkloadId == workloadId).ToList();
    }

    public void Save(RouteRecord record)
    {
        lock (_gate)
        {
            var state = store.Load() ?? new RouteRegistryState();
            store.Save(state with
            {
                Routes = state.Routes.Where(r => r.RouteId != record.RouteId).Append(record).ToList(),
            });
        }
    }

    /// <summary>Returns whether anything was actually removed, so a caller never logs a removal it did not do.</summary>
    public bool Remove(string routeId, string? tenantId)
    {
        lock (_gate)
        {
            var state = store.Load();
            if (state is null) return false;

            var remaining = state.Routes
                .Where(r => r.RouteId != routeId || (tenantId is not null && r.TenantId != tenantId))
                .ToList();

            if (remaining.Count == state.Routes.Count) return false;

            store.Save(state with { Routes = remaining });
            return true;
        }
    }

    public void RemoveForWorkload(string workloadId)
    {
        lock (_gate)
        {
            var state = store.Load();
            if (state is null) return;

            store.Save(state with { Routes = state.Routes.Where(r => r.WorkloadId != workloadId).ToList() });
        }
    }
}
