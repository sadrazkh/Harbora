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

public sealed record PlacementResult(bool Ok, Guid? ServerId, string? Reason, string? ReasonFa = null)
{
    public static PlacementResult Placed(Guid serverId) => new(true, serverId, null);
    public static PlacementResult Fail(string reason, string? reasonFa = null) => new(false, null, reason, reasonFa);
}

/// <summary>
/// A deploy was refused at queue time because the node it would run on no longer has room for it —
/// P7 (2026-08-17 app-environment-management design), "capacity re-checked at queue time is the
/// whole of the item". <see cref="ISchedulerService.CheckAsync"/> already existed and already had
/// one caller (a database create); this is the same check, called from
/// <c>DeploymentEngine.QueueDeploymentAsync</c> so every one of its callers gets it without each
/// remembering to ask on its own — the same reasoning the PAYG start gate's own comment gives for
/// living in one shared place rather than at every queue site.
/// </summary>
public sealed class CapacityRefusedException(PlacementResult result) : InvalidOperationException(result.Reason)
{
    /// <summary>The same refusal in Persian, or null where the check itself carried none.</summary>
    public string? ReasonFa { get; } = result.ReasonFa;
}
