using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Common;
using Harbora.Domain.Deployments;
using Harbora.Domain.Tenancy;
using Harbora.Infrastructure.Billing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
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
    /// <summary>
    /// Takes a transaction-scoped PostgreSQL advisory lock derived from the workspace id. Every
    /// resource-creation path takes the same lock before reading usage and commits it only after its
    /// resource has been saved, turning the old check-then-insert race into a serial decision.
    /// </summary>
    public async Task<IQuotaReservation> AcquireCreationLockAsync(Guid workspaceId, CancellationToken ct)
    {
        if (!db.Database.IsRelational()) return NoopQuotaReservation.Instance;

        // A stack deployment owns the transaction and then calls ProjectService.PrepareAsync in the
        // same DbContext. PostgreSQL advisory locks are re-entrant on that connection, so nested
        // callers only add the same lock and leave commit ownership with the outer operation.
        var ownsTransaction = db.Database.CurrentTransaction is null;
        IDbContextTransaction? transaction = null;
        try
        {
            if (ownsTransaction)
                transaction = await db.Database.BeginTransactionAsync(ct);

            await db.Database.ExecuteSqlRawAsync(
                "SELECT pg_advisory_xact_lock({0})", [LockKey(workspaceId)], ct);

            return transaction is null
                ? NoopQuotaReservation.Instance
                : new DatabaseQuotaReservation(transaction);
        }
        catch
        {
            if (transaction is not null) await transaction.DisposeAsync();
            throw;
        }
    }

    public async Task<WorkspaceUsage> GetUsageAsync(Guid workspaceId, CancellationToken ct)
    {
        var (plan, apps, services, mem, cpu, suspended) = await SnapshotAsync(workspaceId, ct);

        // Disk is reported here too. It was enforced on create and shown on the pricing card, and
        // the usage screen — the one place a person looks to find out where they stand — had no
        // disk on it at all.
        var disk = await DiskUsageAsync(workspaceId, ct);
        var governed = await GovernanceUsageAsync(workspaceId, ct);
        var inFlight = DeploymentStateMachine.InFlight.ToArray();
        var activeDeployments = await db.Deployments.AsNoTracking()
            .CountAsync(d => d.WorkspaceId == workspaceId && inFlight.Contains(d.Status), ct);

        return new WorkspaceUsage(
            plan?.Name ?? "Default",
            apps, plan?.MaxApps ?? 0,
            services, plan?.MaxServices ?? 0,
            mem, plan?.MaxMemoryBytes ?? 0,
            cpu, plan?.MaxCpuCores ?? 0,
            suspended,
            disk.MeasuredBytes, plan?.MaxDiskBytes ?? 0, disk.UnmeasuredResources,
            governed.Members, plan?.MaxMembers ?? 0,
            governed.Projects, plan?.MaxProjects ?? 0,
            governed.Environments, plan?.MaxEnvironments ?? 0,
            governed.Domains, plan?.MaxDomains ?? 0,
            governed.Volumes, plan?.MaxVolumes ?? 0,
            governed.BackupSchedules, plan?.MaxBackupSchedules ?? 0,
            governed.CronJobs, plan?.MaxCronJobs ?? 0,
            activeDeployments, plan?.MaxConcurrentDeployments ?? 0,
            plan?.MaxBackupRetentionCount ?? 0);
    }

    public async Task<QuotaCheck> CanAddGovernedResourcesAsync(
        Guid workspaceId, GovernanceQuotaDelta delta, CancellationToken ct)
    {
        if (delta is { Members: < 0 } or { Projects: < 0 } or { Environments: < 0 }
            or { Domains: < 0 } or { Volumes: < 0 } or { BackupSchedules: < 0 })
            throw new ArgumentOutOfRangeException(nameof(delta), "A quota delta cannot be negative.");

        var workspace = await db.Workspaces.AsNoTracking()
            .FirstOrDefaultAsync(w => w.Id == workspaceId, ct);
        if (workspace?.IsSuspended == true)
            return QuotaCheck.Deny("This workspace is suspended.", "این فضای کاری تعلیق شده است.");

        var plan = workspace?.PlanId is { } planId
            ? await db.Plans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == planId, ct)
            : await db.Plans.AsNoTracking().FirstOrDefaultAsync(p => p.IsDefault, ct);
        // These are governance ceilings, not metered capacity. There is no per-member,
        // per-project or per-domain overage rate to collect, so AllowsOverage must not turn them
        // into free unlimited resources. Compute/disk overage is handled by the workload checks.
        if (plan is null) return QuotaCheck.Ok;

        var used = await GovernanceUsageAsync(workspaceId, ct);
        return Refuse(plan.MaxMembers, used.Members, delta.Members, "member", "عضو")
            ?? Refuse(plan.MaxProjects, used.Projects, delta.Projects, "project", "پروژه")
            ?? Refuse(plan.MaxEnvironments, used.Environments, delta.Environments, "environment", "محیط")
            ?? Refuse(plan.MaxDomains, used.Domains, delta.Domains, "domain", "دامنه")
            ?? Refuse(plan.MaxVolumes, used.Volumes, delta.Volumes, "volume", "والیوم")
            ?? Refuse(plan.MaxBackupSchedules, used.BackupSchedules, delta.BackupSchedules,
                "backup schedule", "زمان‌بندی پشتیبان‌گیری")
            ?? QuotaCheck.Ok;
    }

    public async Task<QuotaCheck> CanAddWorkloadsAsync(
        Guid workspaceId, WorkloadQuotaDelta delta, CancellationToken ct)
    {
        if (delta is { Apps: < 0 } or { Services: < 0 } or { MemoryBytes: < 0 }
            or { CpuCores: < 0 } or { CronJobs: < 0 })
            throw new ArgumentOutOfRangeException(nameof(delta), "A quota delta cannot be negative.");

        var (plan, apps, services, memory, cpu, suspended) = await SnapshotAsync(workspaceId, ct);
        if (suspended)
            return QuotaCheck.Deny("This workspace is suspended.", "این فضای کاری تعلیق شده است.");
        if (plan is null) return QuotaCheck.Ok;

        if (plan.MaxCronJobs > 0 && delta.CronJobs > 0)
        {
            var cronJobs = await db.Apps.AsNoTracking()
                .CountAsync(a => a.WorkspaceId == workspaceId && a.Kind == ServiceKind.Cron, ct);
            if (cronJobs + delta.CronJobs > plan.MaxCronJobs)
                return QuotaCheck.Deny(
                    $"The plan allows {plan.MaxCronJobs} scheduled job(s); this operation would use {cronJobs + delta.CronJobs}.",
                    $"این پلن حداکثر {plan.MaxCronJobs} کار زمان‌بندی‌شده اجازه می‌دهد؛ این عملیات مصرف را به {cronJobs + delta.CronJobs} می‌رساند.");
        }

        // Scheduled jobs are a governance cap: there is no per-job overage meter to collect.
        if (SellsPastItsCaps(plan)) return QuotaCheck.Ok;

        if (plan.MaxApps > 0 && apps + delta.Apps > plan.MaxApps)
            return QuotaCheck.Deny(
                $"The plan allows {plan.MaxApps} app(s); this operation would use {apps + delta.Apps}.",
                $"این پلن حداکثر {plan.MaxApps} برنامه اجازه می‌دهد؛ این عملیات مصرف را به {apps + delta.Apps} می‌رساند.");
        if (plan.MaxServices > 0 && services + delta.Services > plan.MaxServices)
            return QuotaCheck.Deny(
                $"The plan allows {plan.MaxServices} service(s); this operation would use {services + delta.Services}.",
                $"این پلن حداکثر {plan.MaxServices} سرویس اجازه می‌دهد؛ این عملیات مصرف را به {services + delta.Services} می‌رساند.");
        if (plan.MaxMemoryBytes > 0 && memory + delta.MemoryBytes > plan.MaxMemoryBytes)
            return QuotaCheck.Deny("Memory quota exceeded for this plan.", "سهمیه حافظه این پلن کافی نیست.");
        if (plan.MaxCpuCores > 0 && cpu + delta.CpuCores > plan.MaxCpuCores)
            return QuotaCheck.Deny("CPU quota exceeded for this plan.", "سهمیه پردازنده این پلن کافی نیست.");

        return QuotaCheck.Ok;
    }

    public async Task<QuotaCheck> CanQueueDeploymentAsync(Guid workspaceId, CancellationToken ct)
    {
        var workspace = await db.Workspaces.AsNoTracking().FirstOrDefaultAsync(w => w.Id == workspaceId, ct);
        if (workspace?.IsSuspended == true)
            return QuotaCheck.Deny("This workspace is suspended.", "این فضای کاری تعلیق شده است.");
        var plan = workspace?.PlanId is { } planId
            ? await db.Plans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == planId, ct)
            : await db.Plans.AsNoTracking().FirstOrDefaultAsync(p => p.IsDefault, ct);
        if (plan is null || plan.MaxConcurrentDeployments <= 0) return QuotaCheck.Ok;

        var inFlight = DeploymentStateMachine.InFlight.ToArray();
        var active = await db.Deployments.AsNoTracking()
            .CountAsync(d => d.WorkspaceId == workspaceId && inFlight.Contains(d.Status), ct);
        return active >= plan.MaxConcurrentDeployments
            ? QuotaCheck.Deny(
                $"The plan allows {plan.MaxConcurrentDeployments} concurrent deployment(s); {active} are already active.",
                $"این پلن حداکثر {plan.MaxConcurrentDeployments} استقرار هم‌زمان اجازه می‌دهد و اکنون {active} استقرار فعال است.")
            : QuotaCheck.Ok;
    }

    public async Task<QuotaCheck> CanUseBackupRetentionAsync(
        Guid workspaceId, int retentionCount, CancellationToken ct)
    {
        if (retentionCount < 1)
            return QuotaCheck.Deny("Retention must keep at least one backup.", "حداقل یک نسخه پشتیبان باید نگهداری شود.");
        var workspace = await db.Workspaces.AsNoTracking().FirstOrDefaultAsync(w => w.Id == workspaceId, ct);
        if (workspace?.IsSuspended == true)
            return QuotaCheck.Deny("This workspace is suspended.", "این فضای کاری تعلیق شده است.");
        var plan = workspace?.PlanId is { } planId
            ? await db.Plans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == planId, ct)
            : await db.Plans.AsNoTracking().FirstOrDefaultAsync(p => p.IsDefault, ct);
        return plan is { MaxBackupRetentionCount: > 0 }
               && retentionCount > plan.MaxBackupRetentionCount
            ? QuotaCheck.Deny(
                $"The plan retains at most {plan.MaxBackupRetentionCount} backup(s) per schedule.",
                $"این پلن در هر زمان‌بندی حداکثر {plan.MaxBackupRetentionCount} نسخه پشتیبان نگه می‌دارد.")
            : QuotaCheck.Ok;
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
    /// The consequence, now carried out: the screen that offers this tick box says it does nothing
    /// until billing is switched on, because the tenant's refusal cannot say it for them — the
    /// wording they see is the ordinary cap message, unchanged.
    /// </para>
    ///
    /// <para>
    /// <b>What the excess costs is the ordinary meter.</b> An application past the cap is charged its
    /// instance size's hourly rate like every other application, and a volume past it is charged the
    /// plan's gibibyte-hour. There is no surcharge, and no column for one: <c>Plan</c> carried
    /// <c>OverageCpuCoreHourMinor</c> and two neighbours that nothing read, and they were dropped
    /// rather than surfaced on the plan form, because an operator who sets a burst rate and is
    /// charged nothing extra for ever is the failure this whole feature was written against.
    /// Adding one back starts at the tick, not at the column: the compute meter is priced per
    /// size-hour, so there is no per-core figure to charge an over-cap fraction of an hour at, and
    /// whatever is added has to be told apart from the rate already being charged or the hour is
    /// billed twice.
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

    private async Task<GovernanceUsage> GovernanceUsageAsync(Guid workspaceId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var members = await db.WorkspaceMembers.IgnoreQueryFilters().AsNoTracking()
            .CountAsync(m => m.WorkspaceId == workspaceId, ct);
        members += await db.WorkspaceInvitations.IgnoreQueryFilters().AsNoTracking()
            .CountAsync(i => i.WorkspaceId == workspaceId && i.AcceptedAt == null
                && !i.IsRevoked && i.ExpiresAt > now, ct);

        return new GovernanceUsage(
            members,
            await db.Projects.AsNoTracking().CountAsync(p => p.WorkspaceId == workspaceId, ct),
            await db.Environments.AsNoTracking().CountAsync(e => e.WorkspaceId == workspaceId, ct),
            await db.Domains.AsNoTracking().CountAsync(d => d.App!.WorkspaceId == workspaceId, ct),
            await db.Volumes.AsNoTracking().CountAsync(v => v.App!.WorkspaceId == workspaceId, ct),
            await db.BackupSchedules.AsNoTracking().CountAsync(s => s.WorkspaceId == workspaceId, ct),
            await db.Apps.AsNoTracking()
                .CountAsync(a => a.WorkspaceId == workspaceId && a.Kind == ServiceKind.Cron, ct));
    }

    private static QuotaCheck? Refuse(int limit, int used, int adding, string resource, string resourceFa)
    {
        if (limit <= 0 || used + adding <= limit) return null;
        return QuotaCheck.Deny(
            $"The plan allows {limit} {resource}(s); {used} are already reserved and this operation adds {adding}.",
            $"این پلن حداکثر {limit} {resourceFa} اجازه می‌دهد؛ اکنون {used} مورد رزرو شده و این عملیات {adding} مورد اضافه می‌کند.");
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

    private static long LockKey(Guid workspaceId)
    {
        Span<byte> bytes = stackalloc byte[16];
        workspaceId.TryWriteBytes(bytes, bigEndian: true, out _);
        return System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(bytes);
    }

    private sealed class DatabaseQuotaReservation(IDbContextTransaction transaction) : IQuotaReservation
    {
        private bool _committed;

        public async Task CommitAsync(CancellationToken ct)
        {
            if (_committed) return;
            await transaction.CommitAsync(ct);
            _committed = true;
        }

        public ValueTask DisposeAsync() => transaction.DisposeAsync();
    }

    private sealed record GovernanceUsage(
        int Members, int Projects, int Environments, int Domains, int Volumes, int BackupSchedules,
        int CronJobs);
}
