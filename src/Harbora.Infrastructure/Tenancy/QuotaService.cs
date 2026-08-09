using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Tenancy;
using Harbora.Infrastructure.Billing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Harbora.Infrastructure.Tenancy;

/// <summary>
/// Computes workspace usage and answers "can this workspace take on more?" against its plan
/// (or the platform default plan). Committed CPU/memory come from apps' instance-size limits.
///
/// <para>
/// A cap is either a wall or a price line, and <see cref="Plan.AllowsOverage"/> decides which for
/// that plan rather than the platform deciding for all of them: a free tier's cap is the whole
/// product, and a pay-as-you-go tier's cap is a figure the customer may buy past. See
/// <see cref="SellsPastItsCaps"/> for what that does and does not lift.
/// </para>
/// </summary>
public sealed class QuotaService(HarboraDbContext db, IOptions<BillingOptions> billing) : IQuotaService
{
    public async Task<WorkspaceUsage> GetUsageAsync(Guid workspaceId, CancellationToken ct)
    {
        var (plan, apps, services, mem, cpu, suspended) = await SnapshotAsync(workspaceId, ct);

        // Disk is reported here too. It was enforced on create and shown on the pricing card, and
        // the usage screen — the one place a person looks to find out where they stand — had no
        // disk on it at all.
        var disk = await DiskUsageAsync(workspaceId, ct);

        return new WorkspaceUsage(
            plan?.Name ?? "Default",
            apps, plan?.MaxApps ?? 0,
            services, plan?.MaxServices ?? 0,
            mem, plan?.MaxMemoryBytes ?? 0,
            cpu, plan?.MaxCpuCores ?? 0,
            suspended,
            disk.MeasuredBytes, plan?.MaxDiskBytes ?? 0, disk.UnmeasuredResources);
    }

    public async Task<QuotaCheck> CanAddAppAsync(Guid workspaceId, string? instanceSizeKey, Guid? excludeAppId, CancellationToken ct)
    {
        var (plan, apps, _, mem, cpu, suspended) = await SnapshotAsync(workspaceId, ct, excludeAppId);
        if (suspended) return QuotaCheck.Deny("This workspace is suspended.");
        if (plan is null) return QuotaCheck.Ok;

        // Asked once for the whole check. A plan that sold past one cap and walled at the next would
        // be a plan nobody chose: the caps arrive as one list, on one screen, under one flag.
        var sells = SellsPastItsCaps(plan);

        if (!sells && plan.MaxApps > 0 && apps >= plan.MaxApps)
            return QuotaCheck.Deny($"App limit reached ({plan.MaxApps}).");

        // Deliberately not conditional. A cap is a quantity and this is an entitlement — the sizes
        // the provider offers on this tier, which may be all the hardware there is. Selling more of
        // what somebody already has is not the same as selling them something never on the menu.
        var size = await SizeAsync(instanceSizeKey, ct);
        if (size is not null && !IsSizeAllowed(plan, size.Key))
            return QuotaCheck.Deny($"Instance size '{size.Key}' is not allowed on the {plan.Name} plan.");

        var addMem = size?.MemoryBytes ?? 0;
        var addCpu = size?.CpuCores ?? 0;

        if (!sells && plan.MaxMemoryBytes > 0 && mem + addMem > plan.MaxMemoryBytes)
            return QuotaCheck.Deny("Memory quota exceeded for this plan.");
        if (!sells && plan.MaxCpuCores > 0 && cpu + addCpu > plan.MaxCpuCores)
            return QuotaCheck.Deny("CPU quota exceeded for this plan.");

        // The disk limit used to sit on the plan, appear on the pricing screen, and be checked
        // nowhere at all.
        var disk = await DiskUsageAsync(workspaceId, ct);
        if (!sells && !DiskQuota.Allows(plan.MaxDiskBytes, disk))
            return QuotaCheck.Deny(DiskQuota.Explain(plan.MaxDiskBytes, disk));

        return QuotaCheck.Ok;
    }

    public async Task<QuotaCheck> CanAddServiceAsync(
        Guid workspaceId, string? instanceSizeKey, CancellationToken ct)
    {
        var (plan, _, services, mem, cpu, suspended) = await SnapshotAsync(workspaceId, ct);
        if (suspended) return QuotaCheck.Deny("This workspace is suspended.");
        if (plan is null) return QuotaCheck.Ok;

        // The same answer an app gets, and asked here for the same reason the caps below are checked
        // here: a plan that sold applications past its cap and walled databases at theirs would let
        // the same tenant buy the excess on one screen and be refused it on the next.
        var sells = SellsPastItsCaps(plan);

        if (!sells && plan.MaxServices > 0 && services >= plan.MaxServices)
            return QuotaCheck.Deny($"Service limit reached ({plan.MaxServices}).");

        // The same check an app gets. Without it a plan could cap applications precisely and let a
        // database of any size sit next to them. Not lifted by overage, for the reason given there.
        var size = await SizeAsync(instanceSizeKey, ct);
        if (size is not null && !IsSizeAllowed(plan, size.Key))
            return QuotaCheck.Deny($"Instance size '{size.Key}' is not allowed on the {plan.Name} plan.");

        if (!sells && plan.MaxMemoryBytes > 0 && mem + (size?.MemoryBytes ?? 0) > plan.MaxMemoryBytes)
            return QuotaCheck.Deny("Memory quota exceeded for this plan.");
        if (!sells && plan.MaxCpuCores > 0 && cpu + (size?.CpuCores ?? 0) > plan.MaxCpuCores)
            return QuotaCheck.Deny("CPU quota exceeded for this plan.");

        var disk = await DiskUsageAsync(workspaceId, ct);
        if (!sells && !DiskQuota.Allows(plan.MaxDiskBytes, disk))
            return QuotaCheck.Deny(DiskQuota.Explain(plan.MaxDiskBytes, disk));

        return QuotaCheck.Ok;
    }

    /// <summary>
    /// Whether this plan's caps are walls or price lines.
    ///
    /// <para>
    /// <b>Both halves are required, and the second is not in the flag's name.</b> A cap lifted for a
    /// customer is capacity somebody has to be paying for by the hour, and <c>Billing:Enabled</c> is
    /// false on every install that has not opted in — <see cref="BillingTick"/> returns without
    /// charging anybody. Lifting a published limit there would hand out capacity for nothing while
    /// this method reported success, which is the shape the rest of this feature is built to avoid.
    /// The consequence to hand on: the screen that offers this tick box has to say it does nothing
    /// until billing is switched on, because the tenant's refusal cannot say it for them — the
    /// wording they see is the ordinary cap message, unchanged.
    /// </para>
    ///
    /// <para>
    /// <b>What the excess costs is the ordinary meter.</b> An application past the cap is charged its
    /// instance size's hourly rate like every other application, and a volume past it is charged the
    /// plan's gibibyte-hour. <c>Plan.OverageCpuCoreHourMinor</c> and its two neighbours are a
    /// surcharge nothing on this branch reads; a plan with this flag set and those columns blank
    /// still bills for what it hands over, so they are not required here. If a surcharge is ever
    /// wired up, it has to be told apart from the rate already being charged, or the hour is billed
    /// twice.
    /// </para>
    ///
    /// <para>
    /// <b>What it does not lift:</b> a suspension, which is checked before this and is about whether
    /// the customer is paying at all rather than how much; the plan's allowed-size list, which is an
    /// entitlement and not a quantity; and nothing in <see cref="GetUsageAsync"/> — a workspace that
    /// was allowed past its cap still reads as over it, so the operator's list of who a limit is
    /// biting still names them.
    /// </para>
    /// </summary>
    private bool SellsPastItsCaps(Plan plan) => plan.AllowsOverage && billing.Value.Enabled;

    /// <summary>
    /// What this workspace is measured to be using, and how much has never been measured.
    ///
    /// Both halves matter: a volume nobody has measured is reported as unknown rather than counted
    /// as empty, because assuming zero is how a quota quietly stops being one.
    /// </summary>
    public async Task<DiskUsage> DiskUsageAsync(Guid workspaceId, CancellationToken ct)
    {
        var databases = await db.ManagedServices.AsNoTracking()
            .Where(s => s.WorkspaceId == workspaceId)
            .Select(s => s.StorageBytes)
            .ToListAsync(ct);

        var volumes = await db.Volumes.AsNoTracking()
            .Where(v => v.App!.WorkspaceId == workspaceId)
            .Select(v => v.StorageBytes)
            .ToListAsync(ct);

        var all = databases.Concat(volumes).ToList();

        return new DiskUsage(
            all.Where(b => b is not null).Sum(b => b!.Value),
            all.Count(b => b is null));
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

        // Databases count too. They did not, so a plan's memory limit measured half the workspace
        // and a tenant could sit inside their quota while the host ran out of memory.
        var serviceQuery = db.ManagedServices.AsNoTracking().Where(s => s.WorkspaceId == workspaceId);
        mem += await serviceQuery.SumAsync(s => (long?)s.MemoryLimitBytes, ct) ?? 0;
        cpu += await serviceQuery.SumAsync(s => (double?)s.CpuLimit, ct) ?? 0;
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
