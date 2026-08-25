using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Infrastructure.Tenancy;

/// <summary>
/// Allocatable = reported host resources minus reserved headroom, then scaled by the node's overcommit
/// factor — CPU and memory each have their own, because they are not the same risk (CPU contention
/// queues work; memory overcommit invites the OOM killer). Committed = sum of the CPU/memory limits of
/// the apps and managed services placed on the node.
/// </summary>
public sealed class NodeCapacityService(HarboraDbContext db) : INodeCapacityService
{
    public async Task<IReadOnlyList<NodeCapacity>> GetAllAsync(CancellationToken ct)
    {
        var servers = await db.Servers.AsNoTracking().ToListAsync(ct);

        // One grouped pass over apps for committed load per node.
        //
        // Unfiltered, because placement happens for whoever is creating something and the answer
        // must be the whole node: counting only the current workspace's apps would report a node as
        // nearly empty while another tenant's applications filled it.
        var committed = await db.Apps.AsNoTracking().IgnoreQueryFilters()
            .GroupBy(a => a.ServerId)
            .Select(g => new
            {
                ServerId = g.Key,
                Mem = g.Sum(a => (long?)a.MemoryLimitBytes) ?? 0,
                Cpu = g.Sum(a => (double?)a.CpuLimit) ?? 0,
                Count = g.Count()
            })
            .ToDictionaryAsync(x => x.ServerId, ct);

        // Databases count too. They did not, so a node's committed memory measured only half of
        // what was placed on it: a PostgreSQL given 512 MB was invisible to the scheduler, which
        // would then place applications into memory that was already spoken for and overcommit the
        // host. The same omission was fixed in QuotaService for a workspace's plan; this is the
        // other half of it, for the machine.
        var services = await db.ManagedServices.AsNoTracking().IgnoreQueryFilters()
            .GroupBy(s => s.ServerId)
            .Select(g => new
            {
                ServerId = g.Key,
                Mem = g.Sum(s => (long?)s.MemoryLimitBytes) ?? 0,
                Cpu = g.Sum(s => (double?)s.CpuLimit) ?? 0,
                Count = g.Count()
            })
            .ToListAsync(ct);

        return servers.Select(s =>
        {
            committed.TryGetValue(s.Id, out var c);
            var svc = services.FirstOrDefault(x => x.ServerId == s.Id);

            // Reserved-memory ratio and both overcommit factors are an administrator's call (per-server,
            // set from the node's Capacity policy form) — this is the only place that reads them for
            // placement, so every consumer (scheduler, size picker, node/server pages) agrees.
            //
            // A stored factor of zero or less is never a legitimate admin choice — the write side
            // refuses it — so treating one as "not configured" and falling back to 1.0 (no overcommit)
            // only protects against a row that predates that validation. It deliberately does NOT floor
            // at 1.0 the way this used to: a factor between 0 and 1 is a real undercommit, not a mistake.
            var reservedRatio = Math.Clamp(s.ReservedMemoryRatio, 0, 1);
            var memFactor = s.MemoryOvercommitFactor > 0 ? s.MemoryOvercommitFactor : 1.0;
            var cpuFactor = s.CpuOvercommitFactor > 0 ? s.CpuOvercommitFactor : 1.0;

            var allocMem = s.TotalMemoryBytes > 0 ? (long)(s.TotalMemoryBytes * (1 - reservedRatio) * memFactor) : 0;
            var allocCpu = s.CpuCores > 0 ? s.CpuCores * cpuFactor : 0;
            return new NodeCapacity(
                s.Id, s.Name, s.Pool, s.Status == ServerStatus.Online,
                allocMem,
                (c?.Mem ?? 0) + (svc?.Mem ?? 0),
                allocCpu,
                (c?.Cpu ?? 0) + (svc?.Cpu ?? 0),
                (c?.Count ?? 0) + (svc?.Count ?? 0));
        }).ToList();
    }

    public async Task<NodeCapacity?> GetAsync(Guid serverId, CancellationToken ct) =>
        (await GetAllAsync(ct)).FirstOrDefault(n => n.ServerId == serverId);
}
