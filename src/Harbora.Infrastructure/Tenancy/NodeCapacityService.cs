using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Infrastructure.Tenancy;

/// <summary>
/// Allocatable = reported host resources minus reserved headroom, with CPU scaled by the node's
/// overcommit factor. Committed = sum of the CPU/memory limits of the apps placed on the node.
/// </summary>
public sealed class NodeCapacityService(HarboraDbContext db) : INodeCapacityService
{
    public async Task<IReadOnlyList<NodeCapacity>> GetAllAsync(CancellationToken ct)
    {
        var servers = await db.Servers.AsNoTracking().ToListAsync(ct);

        // One grouped pass over apps for committed load per node.
        var committed = await db.Apps.AsNoTracking()
            .GroupBy(a => a.ServerId)
            .Select(g => new
            {
                ServerId = g.Key,
                Mem = g.Sum(a => (long?)a.MemoryLimitBytes) ?? 0,
                Cpu = g.Sum(a => (double?)a.CpuLimit) ?? 0,
                Count = g.Count()
            })
            .ToDictionaryAsync(x => x.ServerId, ct);

        return servers.Select(s =>
        {
            committed.TryGetValue(s.Id, out var c);
            var allocMem = s.TotalMemoryBytes > 0 ? (long)(s.TotalMemoryBytes * (1 - s.ReservedMemoryRatio)) : 0;
            var allocCpu = s.CpuCores > 0 ? s.CpuCores * Math.Max(1, s.CpuOvercommitFactor) : 0;
            return new NodeCapacity(
                s.Id, s.Name, s.Pool, s.Status == ServerStatus.Online,
                allocMem, c?.Mem ?? 0, allocCpu, c?.Cpu ?? 0, c?.Count ?? 0);
        }).ToList();
    }

    public async Task<NodeCapacity?> GetAsync(Guid serverId, CancellationToken ct) =>
        (await GetAllAsync(ct)).FirstOrDefault(n => n.ServerId == serverId);
}
