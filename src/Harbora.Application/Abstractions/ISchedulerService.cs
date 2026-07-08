namespace Harbora.Application.Abstractions;

/// <summary>
/// Places workloads on nodes without overcommitting. Picks an online node in the required pool
/// that can fit the requested CPU/memory, spreading load by preferring the node with the most
/// free memory. Returns null when nothing fits, so callers can reject cleanly.
/// </summary>
public interface ISchedulerService
{
    Task<PlacementResult> PlaceAsync(long memoryBytes, double cpu, string? requiredPool, CancellationToken ct);

    /// <summary>Verify a specific node can still fit the workload (for an explicit server choice).</summary>
    Task<PlacementResult> CheckAsync(Guid serverId, long memoryBytes, double cpu, CancellationToken ct);
}

public sealed record PlacementResult(bool Ok, Guid? ServerId, string? Reason)
{
    public static PlacementResult Placed(Guid serverId) => new(true, serverId, null);
    public static PlacementResult Fail(string reason) => new(false, null, reason);
}
