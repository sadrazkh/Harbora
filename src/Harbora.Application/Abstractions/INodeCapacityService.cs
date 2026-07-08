namespace Harbora.Application.Abstractions;

/// <summary>
/// Computes each node's allocatable capacity (host resources minus reserved headroom, CPU scaled
/// by the allowed overcommit factor) versus what's already committed by the apps placed on it.
/// The scheduler (P2.2) uses this to place work without overcommitting a server.
/// </summary>
public interface INodeCapacityService
{
    Task<IReadOnlyList<NodeCapacity>> GetAllAsync(CancellationToken ct);
    Task<NodeCapacity?> GetAsync(Guid serverId, CancellationToken ct);
}

public sealed record NodeCapacity(
    Guid ServerId,
    string Name,
    string Pool,
    bool IsOnline,
    long AllocatableMemoryBytes,
    long CommittedMemoryBytes,
    double AllocatableCpu,
    double CommittedCpu,
    int AppCount)
{
    public long FreeMemoryBytes => Math.Max(0, AllocatableMemoryBytes - CommittedMemoryBytes);
    public double FreeCpu => Math.Max(0, AllocatableCpu - CommittedCpu);

    /// <summary>Whether the node can fit an app needing this much (0 allocatable = unknown → allow).</summary>
    public bool CanFit(long memoryBytes, double cpu) =>
        (AllocatableMemoryBytes <= 0 || CommittedMemoryBytes + memoryBytes <= AllocatableMemoryBytes) &&
        (AllocatableCpu <= 0 || CommittedCpu + cpu <= AllocatableCpu);
}
