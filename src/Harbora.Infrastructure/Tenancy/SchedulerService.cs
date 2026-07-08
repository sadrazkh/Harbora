using Harbora.Application.Abstractions;

namespace Harbora.Infrastructure.Tenancy;

/// <summary>Capacity-aware placement built on <see cref="INodeCapacityService"/>.</summary>
public sealed class SchedulerService(INodeCapacityService capacity) : ISchedulerService
{
    public async Task<PlacementResult> PlaceAsync(long memoryBytes, double cpu, string? requiredPool, CancellationToken ct)
    {
        var nodes = await capacity.GetAllAsync(ct);

        var candidates = nodes
            .Where(n => n.IsOnline)
            .Where(n => string.IsNullOrWhiteSpace(requiredPool) || string.Equals(n.Pool, requiredPool, StringComparison.OrdinalIgnoreCase))
            .Where(n => n.CanFit(memoryBytes, cpu))
            // Spread load: prefer the node with the most free memory.
            .OrderByDescending(n => n.FreeMemoryBytes)
            .ToList();

        if (candidates.Count == 0)
        {
            var reason = nodes.Any(n => n.IsOnline)
                ? "No node has enough free capacity for this instance size."
                : "No online node is available.";
            if (!string.IsNullOrWhiteSpace(requiredPool) && !nodes.Any(n => string.Equals(n.Pool, requiredPool, StringComparison.OrdinalIgnoreCase)))
                reason = $"No node in the '{requiredPool}' pool.";
            return PlacementResult.Fail(reason);
        }

        return PlacementResult.Placed(candidates[0].ServerId);
    }

    public async Task<PlacementResult> CheckAsync(Guid serverId, long memoryBytes, double cpu, CancellationToken ct)
    {
        var node = await capacity.GetAsync(serverId, ct);
        if (node is null) return PlacementResult.Fail("Server not found.");
        if (!node.IsOnline) return PlacementResult.Fail($"'{node.Name}' is offline.");
        if (!node.CanFit(memoryBytes, cpu)) return PlacementResult.Fail($"'{node.Name}' does not have enough free capacity.");
        return PlacementResult.Placed(serverId);
    }
}
