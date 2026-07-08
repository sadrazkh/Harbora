using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Infrastructure.Tenancy;

/// <summary>
/// Computes workspace usage and answers "can this workspace take on more?" against its plan
/// (or the platform default plan). Committed CPU/memory come from apps' instance-size limits.
/// </summary>
public sealed class QuotaService(HarboraDbContext db) : IQuotaService
{
    public async Task<WorkspaceUsage> GetUsageAsync(Guid workspaceId, CancellationToken ct)
    {
        var (plan, apps, services, mem, cpu, suspended) = await SnapshotAsync(workspaceId, ct);
        return new WorkspaceUsage(
            plan?.Name ?? "Default",
            apps, plan?.MaxApps ?? 0,
            services, plan?.MaxServices ?? 0,
            mem, plan?.MaxMemoryBytes ?? 0,
            cpu, plan?.MaxCpuCores ?? 0,
            suspended);
    }

    public async Task<QuotaCheck> CanAddAppAsync(Guid workspaceId, string? instanceSizeKey, Guid? excludeAppId, CancellationToken ct)
    {
        var (plan, apps, _, mem, cpu, suspended) = await SnapshotAsync(workspaceId, ct, excludeAppId);
        if (suspended) return QuotaCheck.Deny("This workspace is suspended.");
        if (plan is null) return QuotaCheck.Ok;

        if (plan.MaxApps > 0 && apps >= plan.MaxApps)
            return QuotaCheck.Deny($"App limit reached ({plan.MaxApps}).");

        var size = await SizeAsync(instanceSizeKey, ct);
        if (size is not null && !IsSizeAllowed(plan, size.Key))
            return QuotaCheck.Deny($"Instance size '{size.Key}' is not allowed on the {plan.Name} plan.");

        var addMem = size?.MemoryBytes ?? 0;
        var addCpu = size?.CpuCores ?? 0;

        if (plan.MaxMemoryBytes > 0 && mem + addMem > plan.MaxMemoryBytes)
            return QuotaCheck.Deny("Memory quota exceeded for this plan.");
        if (plan.MaxCpuCores > 0 && cpu + addCpu > plan.MaxCpuCores)
            return QuotaCheck.Deny("CPU quota exceeded for this plan.");

        return QuotaCheck.Ok;
    }

    public async Task<QuotaCheck> CanAddServiceAsync(Guid workspaceId, CancellationToken ct)
    {
        var (plan, _, services, _, _, suspended) = await SnapshotAsync(workspaceId, ct);
        if (suspended) return QuotaCheck.Deny("This workspace is suspended.");
        if (plan is null) return QuotaCheck.Ok;
        if (plan.MaxServices > 0 && services >= plan.MaxServices)
            return QuotaCheck.Deny($"Service limit reached ({plan.MaxServices}).");
        return QuotaCheck.Ok;
    }

    // --- helpers ---

    private async Task<(Plan? Plan, int Apps, int Services, long Mem, double Cpu, bool Suspended)> SnapshotAsync(
        Guid workspaceId, CancellationToken ct, Guid? excludeAppId = null)
    {
        var ws = await db.Workspaces.AsNoTracking().FirstOrDefaultAsync(w => w.Id == workspaceId, ct);
        var plan = ws?.PlanId is { } pid
            ? await db.Plans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == pid, ct)
            : await db.Plans.AsNoTracking().FirstOrDefaultAsync(p => p.IsDefault, ct);

        var appsQuery = db.Apps.AsNoTracking().Where(a => a.WorkspaceId == workspaceId);
        if (excludeAppId is { } ex) appsQuery = appsQuery.Where(a => a.Id != ex);

        var apps = await appsQuery.CountAsync(ct);
        var mem = await appsQuery.SumAsync(a => (long?)a.MemoryLimitBytes, ct) ?? 0;
        var cpu = await appsQuery.SumAsync(a => (double?)a.CpuLimit, ct) ?? 0;
        var services = await db.ManagedServices.AsNoTracking().CountAsync(s => s.WorkspaceId == workspaceId, ct);

        return (plan, apps, services, mem, cpu, ws?.IsSuspended ?? false);
    }

    private Task<InstanceSize?> SizeAsync(string? key, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(key)
            ? Task.FromResult<InstanceSize?>(null)
            : db.InstanceSizes.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key, ct);

    private static bool IsSizeAllowed(Plan plan, string sizeKey) =>
        string.IsNullOrWhiteSpace(plan.AllowedSizeKeys) ||
        plan.AllowedSizeKeys.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(sizeKey, StringComparer.OrdinalIgnoreCase);
}
