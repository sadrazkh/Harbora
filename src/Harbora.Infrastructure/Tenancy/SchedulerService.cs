using System.Globalization;
using Harbora.Application.Abstractions;

namespace Harbora.Infrastructure.Tenancy;

/// <summary>Capacity-aware placement built on <see cref="INodeCapacityService"/>.</summary>
public sealed class SchedulerService(INodeCapacityService capacity) : ISchedulerService
{
    public async Task<PlacementResult> PlaceAsync(long memoryBytes, double cpu, string? requiredPool, CancellationToken ct)
    {
        var nodes = await capacity.GetAllAsync(ct);

        var pooled = nodes
            .Where(n => n.IsOnline)
            .Where(n => string.IsNullOrWhiteSpace(requiredPool) || string.Equals(n.Pool, requiredPool, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var candidates = pooled
            .Where(n => n.CanFit(memoryBytes, cpu))
            // Spread load: prefer the node with the most free memory.
            .OrderByDescending(n => n.FreeMemoryBytes)
            .ToList();

        if (candidates.Count == 0)
        {
            if (!nodes.Any(n => n.IsOnline))
                return PlacementResult.Fail("No online node is available.", "هیچ نود آنلاینی در دسترس نیست.");

            if (!string.IsNullOrWhiteSpace(requiredPool) && pooled.Count == 0)
                return PlacementResult.Fail(
                    $"No node in the '{requiredPool}' pool.", $"هیچ نودی در استخر «{requiredPool}» نیست.");

            // At least one online node exists (in the required pool, if any) but none of them fit —
            // this may be a full machine, or it may be a conservative commitment ratio on an idle one.
            // Report the closest miss (the node the spreading rule above would have picked) with real
            // numbers rather than a bare "no capacity", so an administrator can tell the two apart.
            var closest = pooled.OrderByDescending(n => n.FreeMemoryBytes).First();
            return PlacementResult.Fail(
                $"No node has enough allocatable capacity for this instance size. Closest: {CapacityRefusalReason(closest, fa: false)}",
                $"هیچ نودی ظرفیت قابل‌واگذاری کافی برای این اندازه ندارد. نزدیک‌ترین: {CapacityRefusalReason(closest, fa: true)}");
        }

        return PlacementResult.Placed(candidates[0].ServerId);
    }

    public async Task<PlacementResult> CheckAsync(Guid serverId, long memoryBytes, double cpu, CancellationToken ct)
    {
        var node = await capacity.GetAsync(serverId, ct);
        if (node is null)
            return PlacementResult.Fail("Server not found.", "سرور یافت نشد.");
        if (!node.IsOnline)
            return PlacementResult.Fail($"'{node.Name}' is offline.", $"«{node.Name}» آفلاین است.");
        if (!node.CanFit(memoryBytes, cpu))
            return PlacementResult.Fail(CapacityRefusalReason(node, fa: false), CapacityRefusalReason(node, fa: true));
        return PlacementResult.Placed(serverId);
    }

    /// <summary>
    /// Why a node cannot take more work, in terms an administrator can act on: not a bare "no
    /// capacity" that reads like a full machine, but what is committed against what this node's policy
    /// currently allows, and where that policy lives. "Allocatable" is a computed, policy-driven figure
    /// — reserved-memory ratio and overcommit factor — not the node's physical resources, and a
    /// conservative ratio on an idle machine must not look identical to an actually-full one.
    /// </summary>
    private static string CapacityRefusalReason(NodeCapacity node, bool fa)
    {
        string Gb(long bytes) => (bytes / 1024.0 / 1024 / 1024).ToString("0.0", CultureInfo.InvariantCulture);
        string Cpu(double cores) => cores.ToString("0.##", CultureInfo.InvariantCulture);

        return fa
            ? $"«{node.Name}» {Gb(node.CommittedMemoryBytes)} از {Gb(node.AllocatableMemoryBytes)} گیگابایت حافظه‌ی " +
              $"قابل‌واگذاری، و {Cpu(node.CommittedCpu)} از {Cpu(node.AllocatableCpu)} vCPU قابل‌واگذاری را متعهد کرده " +
              "است. «قابل‌واگذاری» ظرفیت فیزیکی این نود نیست — بر پایه‌ی نسبت رزرو حافظه و ضریب مازاد-تعهد آن حساب " +
              "شده، و مدیر می‌تواند آن را از صفحه‌ی «نودها»، بخش «سیاست ظرفیت» همین نود، تغییر دهد."
            : $"'{node.Name}' is committed to {Gb(node.CommittedMemoryBytes)} GB of {Gb(node.AllocatableMemoryBytes)} GB " +
              $"allocatable memory and {Cpu(node.CommittedCpu)} of {Cpu(node.AllocatableCpu)} allocatable vCPU. " +
              "\"Allocatable\" is not this node's physical capacity — it reflects its reserved-memory ratio and " +
              "overcommit factor. An administrator can raise it from Nodes → this node → Capacity policy.";
    }
}
