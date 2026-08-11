using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Authorization;
using Harbora.Domain.Tenancy;
using Harbora.Infrastructure.Tenancy;
using Harbora.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Web.Controllers;

/// <summary>
/// Shows the current workspace's usage vs its plan (for any user) and lets the provider
/// (Owner/Admin) define the plans + instance sizes offered to customers.
/// </summary>
[Authorize]
[Route("plans")]
public sealed class PlansController(
    HarboraDbContext db, IQuotaService quota, ICurrentUser currentUser, IAuditLogger audit) : Controller
{
    private Guid WorkspaceId => currentUser.WorkspaceId ?? Guid.Empty;
    private bool IsProvider => User.IsInRole("Owner") || User.IsInRole("Admin");
    private string? ClientIp => HttpContext.Connection.RemoteIpAddress?.ToString();
    private bool IsFa => System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fa";

    /// <summary>
    /// Reads one price box, and returns the refusal to show the operator — or null when the box was
    /// accepted, which includes it having been left empty.
    ///
    /// <para>
    /// <b>Empty is an answer: nobody has priced this.</b> It is stored as null, and null is not
    /// zero. Zero says a resource is deliberately free and is a tier somebody may genuinely want to
    /// sell; null says no human has answered yet, and the hourly tick has to be able to tell them
    /// apart. A box that wrote zero when it was left blank would give every unpriced resource away
    /// for ever while each tick reported success.
    /// </para>
    ///
    /// <para>
    /// The refusals name the box. There are four price boxes on this screen and "that is not a
    /// number" without saying which one sends an operator to check all of them — and they are in
    /// both languages, because a price box a Persian-reading operator has to guess at is how an
    /// hourly rate gets typed into a per-gibibyte one.
    /// </para>
    /// </summary>
    private string? ReadRate(string? typed, string labelEn, string labelFa, out long? minor)
    {
        if (!Harbora.Web.Infrastructure.MinorUnits.TryParseRate(typed, out minor))
            return IsFa
                ? $"«{labelFa}» باید عدد باشد، مثلاً ۱۲٫۵۰ را به شکل 12.50 بنویسید. برای «قیمت‌گذاری‌نشده» خالی بگذارید."
                : $"'{labelEn}' must be a figure, for example 12.50. Leave it empty for 'not priced'.";

        if (minor < 0)
            return IsFa
                ? $"«{labelFa}» نمی‌تواند منفی باشد — این یعنی بابت اجرای بار کاری به مشتری پول بدهیم."
                : $"'{labelEn}' cannot be negative — that is a machine that pays customers to run workloads.";

        return null;
    }

    /// <summary>What an operator sees written on a price box, in whichever language they read.</summary>
    private const string BaseRateEn = "Hourly minimum";
    private const string BaseRateFa = "حداقل هزینهٔ هر ساعت";
    private const string DiskRateEn = "Disk — per GB, per hour";
    private const string DiskRateFa = "دیسک — هر گیگابایت، هر ساعت";
    private const string RunningRateEn = "Running — per hour";
    private const string RunningRateFa = "در حال اجرا — هر ساعت";
    private const string StoppedRateEn = "Stopped — per hour";
    private const string StoppedRateFa = "متوقف — هر ساعت";

    /// <summary>A refusal shown on the page the form was posted from, with nothing written.</summary>
    private IActionResult Refuse(string error)
    {
        TempData["Error"] = error;
        return RedirectToAction(nameof(Index));
    }

    // Same reasoning as Servers: this is the platform's plan administration, not a price list.
    [HttpGet("")]
    [Authorize(Policy = Capabilities.PlansManage)]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        ViewData["Title"] = "Plans";
        var vm = new PlansPageViewModel
        {
            Usage = await quota.GetUsageAsync(WorkspaceId, ct),
            IsProvider = IsProvider,
            // Withdrawn plans are shown to an administrator: they still have tenants' history
            // attached and hiding them makes a plan look deleted when it is not.
            Plans = await db.Plans.Where(p => p.IsEnabled || IsProvider)
                .OrderBy(p => p.MonthlyPrice).ToListAsync(ct),
            Sizes = await db.InstanceSizes.Where(s => s.IsEnabled).OrderBy(s => s.SortOrder).ToListAsync(ct),
            StoragePlans = await db.StoragePlans.OrderBy(p => p.SortOrder).ThenBy(p => p.MonthlyPrice).ToListAsync(ct)
        };

        // Who a limit change would already be biting. Shown because lowering a limit does not take
        // anything away — so without this it is a decision whose effect nobody sees.
        //
        // Which limits count is a rule of its own: this list checked apps, databases and CPU, and
        // skipped memory and disk, so halving a plan's memory produced no visible effect anywhere.
        if (IsProvider)
        {
            foreach (var ws in await db.Workspaces.AsNoTracking().ToListAsync(ct))
            {
                var usage = await quota.GetUsageAsync(ws.Id, ct);
                vm.Overages.AddRange(PlanOverage.For(usage)
                    .Select(b => new TenantOverPlanViewModel(ws.Name, usage.PlanName, b)));
            }
        }

        return View(vm);
    }

    /// <summary>
    /// Offers a new plan.
    ///
    /// <para>
    /// <paramref name="monthlyPrice"/> is the pre-existing display figure on the plan card and has
    /// nothing to do with the two rates beside it: it is a <c>decimal</c> nobody charges, while a
    /// rate is a <c>long</c> count of minor units the hourly tick spends. They are deliberately not
    /// read the same way, and following the older one's pattern is how money becomes a floating
    /// figure that bends over a month of addition.
    /// </para>
    /// </summary>
    [HttpPost("create")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.PlansManage)]
    public async Task<IActionResult> CreatePlan(
        string name, int maxApps, int maxServices, long maxMemoryMb, double maxCpu,
        long maxDiskGb, int maxMembers, int maxProjects, int maxEnvironments,
        int maxDomains, int maxVolumes, int maxBackupSchedules,
        string? allowedSizeKeys, decimal monthlyPrice,
        string? baseRatePerHour, string? diskGbHour, bool allowsOverage, CancellationToken ct,
        int maxCronJobs = 0, int maxConcurrentDeployments = 0, int maxBackupRetentionCount = 0)
    {
        if (!IsProvider) return Forbid();

        // Every box read before anything is written. A plan saved with its caps and without the
        // price that was refused is a plan the form said it had not created.
        if (ReadRate(baseRatePerHour, BaseRateEn, BaseRateFa, out var baseRateMinor) is { } refusal)
            return Refuse(refusal);
        if (ReadRate(diskGbHour, DiskRateEn, DiskRateFa, out var diskRateMinor) is { } diskRefusal)
            return Refuse(diskRefusal);

        var plan = new Plan
        {
            Name = name,
            NameFa = name,
            MaxApps = maxApps,
            MaxServices = maxServices,
            MaxMembers = Math.Max(0, maxMembers),
            MaxProjects = Math.Max(0, maxProjects),
            MaxEnvironments = Math.Max(0, maxEnvironments),
            MaxDomains = Math.Max(0, maxDomains),
            MaxVolumes = Math.Max(0, maxVolumes),
            MaxBackupSchedules = Math.Max(0, maxBackupSchedules),
            MaxCronJobs = Math.Max(0, maxCronJobs),
            MaxConcurrentDeployments = Math.Max(0, maxConcurrentDeployments),
            MaxBackupRetentionCount = Math.Max(0, maxBackupRetentionCount),
            MaxMemoryBytes = maxMemoryMb * 1024 * 1024,
            MaxCpuCores = maxCpu,
            // The form never used to set this, so every plan carried a disk limit of zero while the
            // screen showed a column for it.
            MaxDiskBytes = maxDiskGb * 1024 * 1024 * 1024,
            AllowedSizeKeys = allowedSizeKeys ?? "",
            MonthlyPrice = monthlyPrice,
            BaseRatePerHourMinor = baseRateMinor,
            DiskGbHourMinor = diskRateMinor,
            AllowsOverage = allowsOverage
        };
        db.Plans.Add(plan);
        await db.SaveChangesAsync(ct);

        await LogPlanRatesAsync(plan, ct);
        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// Corrects a plan. Plans could only be created, so a wrong limit or a changed price meant a new
    /// plan and moving every tenant by hand.
    /// </summary>
    [HttpPost("{id:guid}")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.PlansManage)]
    public async Task<IActionResult> UpdatePlan(
        Guid id, string name, int maxApps, int maxServices, long maxMemoryMb, double maxCpu,
        long maxDiskGb, int maxMembers, int maxProjects, int maxEnvironments,
        int maxDomains, int maxVolumes, int maxBackupSchedules,
        string? allowedSizeKeys, decimal monthlyPrice,
        string? baseRatePerHour, string? diskGbHour, bool allowsOverage, CancellationToken ct,
        int maxCronJobs = 0, int maxConcurrentDeployments = 0, int maxBackupRetentionCount = 0)
    {
        if (!IsProvider) return Forbid();

        var plan = await db.Plans.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (plan is null) return NotFound();

        // Before the first assignment, not after. The entity is tracked, so a refusal that had
        // already written half the form would leave a correct-looking plan one stray SaveChanges
        // away from being stored.
        if (ReadRate(baseRatePerHour, BaseRateEn, BaseRateFa, out var baseRateMinor) is { } refusal)
            return Refuse(refusal);
        if (ReadRate(diskGbHour, DiskRateEn, DiskRateFa, out var diskRateMinor) is { } diskRefusal)
            return Refuse(diskRefusal);

        plan.Name = name;
        plan.MaxApps = maxApps;
        plan.MaxServices = maxServices;
        plan.MaxMembers = Math.Max(0, maxMembers);
        plan.MaxProjects = Math.Max(0, maxProjects);
        plan.MaxEnvironments = Math.Max(0, maxEnvironments);
        plan.MaxDomains = Math.Max(0, maxDomains);
        plan.MaxVolumes = Math.Max(0, maxVolumes);
        plan.MaxBackupSchedules = Math.Max(0, maxBackupSchedules);
        plan.MaxCronJobs = Math.Max(0, maxCronJobs);
        plan.MaxConcurrentDeployments = Math.Max(0, maxConcurrentDeployments);
        plan.MaxBackupRetentionCount = Math.Max(0, maxBackupRetentionCount);
        plan.MaxMemoryBytes = maxMemoryMb * 1024 * 1024;
        plan.MaxCpuCores = maxCpu;
        plan.MaxDiskBytes = maxDiskGb * 1024 * 1024 * 1024;
        plan.AllowedSizeKeys = allowedSizeKeys ?? "";
        plan.MonthlyPrice = monthlyPrice;
        plan.BaseRatePerHourMinor = baseRateMinor;
        plan.DiskGbHourMinor = diskRateMinor;
        plan.AllowsOverage = allowsOverage;
        await db.SaveChangesAsync(ct);

        await LogPlanRatesAsync(plan, ct);

        // Nothing is taken away from tenants who are already over the new limit — a plan change must
        // not delete somebody's apps. They keep what they have and cannot add more, and the list
        // says who they are so it is a decision rather than a surprise.
        TempData["Message"] = "Plan updated. Tenants already over the new limits keep what they have.";
        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// What this plan now costs, and who said so.
    ///
    /// <para>
    /// A price change is the most disputable thing an administrator does on this screen, and a line
    /// recording only that somebody changed something settles no dispute — so the figures go in,
    /// in the minor units they are stored in. An unset rate is written as <c>null</c> rather than
    /// as <c>0</c>, because the record of the change has to keep the same distinction the column
    /// does: "priced at nothing" and "not priced" are different decisions to have to defend.
    /// </para>
    /// </summary>
    private Task LogPlanRatesAsync(Plan plan, CancellationToken ct) =>
        audit.LogAsync("billing.plan_rates", "plan", plan.Id.ToString(), ClientIp,
            metadataJson: System.Text.Json.JsonSerializer.Serialize(new
            {
                baseRatePerHourMinor = plan.BaseRatePerHourMinor,
                diskGbHourMinor = plan.DiskGbHourMinor,
                allowsOverage = plan.AllowsOverage
            }), ct: ct);

    /// <summary>The same, for a resource tier. Keyed by the tier's key, which is what apps store.</summary>
    private Task LogSizeRatesAsync(InstanceSize size, CancellationToken ct) =>
        audit.LogAsync("billing.size_rates", "instance_size", size.Key, ClientIp,
            metadataJson: System.Text.Json.JsonSerializer.Serialize(new
            {
                runningRatePerHourMinor = size.RunningRatePerHourMinor,
                stoppedRatePerHourMinor = size.StoppedRatePerHourMinor
            }), ct: ct);

    /// <summary>
    /// Corrects a storage tier.
    ///
    /// Its own list rather than a field on the compute plan: object storage is bought in different
    /// amounts from memory, by people who may not want more memory at all, and folding the two
    /// together would mean the only way to buy more space is to buy a bigger server.
    /// </summary>
    [HttpPost("storage/{id:guid}")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.PlansManage)]
    public async Task<IActionResult> UpdateStoragePlan(
        Guid id, string name, long quotaGb, int maxBuckets, decimal monthlyPrice, CancellationToken ct)
    {
        var plan = await db.StoragePlans.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (plan is null) return NotFound();

        plan.Name = string.IsNullOrWhiteSpace(name) ? plan.Name : name.Trim();
        plan.QuotaBytes = quotaGb * 1024 * 1024 * 1024;
        plan.MaxBuckets = Math.Max(0, maxBuckets);
        plan.MonthlyPrice = monthlyPrice;
        await db.SaveChangesAsync(ct);

        // Says what it does not do. A bucket keeps the ceiling it was created with — it is copied
        // onto the row, like an instance's memory limit — so this changes what the next bucket gets.
        TempData["Message"] =
            $"'{plan.Name}' updated. Buckets already created keep the quota they were given.";
        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// Adds a resource tier.
    ///
    /// Tiers were seeded and then read-only forever: the entity's own comment says the provider can
    /// add custom sizes and there was nowhere to do it. That mattered little while a tier was CPU
    /// and memory; it matters now that it carries a disk figure, because a field nobody can set is
    /// a field that stays at whatever it was seeded with.
    /// </summary>
    [HttpPost("sizes")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.PlansManage)]
    public async Task<IActionResult> CreateSize(
        string key, string name, double cpuCores, long memoryMb, long diskGb, int sortOrder,
        string? runningRate, string? stoppedRate, CancellationToken ct)
    {
        var slug = Harbora.Infrastructure.Tenancy.InstanceSizeKey.Normalise(key);
        if (slug is null)
        {
            TempData["Error"] = "A size needs a key: lowercase letters, digits and dashes.";
            return RedirectToAction(nameof(Index));
        }

        // Checked rather than left to the unique index, which surfaces as a 500 on a page somebody
        // was using correctly.
        if (await db.InstanceSizes.AnyAsync(s => s.Key == slug, ct))
        {
            TempData["Error"] = $"A size with the key '{slug}' already exists.";
            return RedirectToAction(nameof(Index));
        }

        if (ReadRate(runningRate, RunningRateEn, RunningRateFa, out var runningMinor) is { } refusal)
            return Refuse(refusal);
        if (ReadRate(stoppedRate, StoppedRateEn, StoppedRateFa, out var stoppedMinor) is { } stoppedRefusal)
            return Refuse(stoppedRefusal);

        var size = new InstanceSize
        {
            Key = slug,
            Name = string.IsNullOrWhiteSpace(name) ? slug : name.Trim(),
            NameFa = string.IsNullOrWhiteSpace(name) ? slug : name.Trim(),
            CpuCores = cpuCores,
            MemoryBytes = memoryMb * 1024 * 1024,
            DiskBytes = diskGb * 1024 * 1024 * 1024,
            SortOrder = sortOrder,
            // Each state priced from its own box, and either may be left empty. A tier added
            // without a price is not a free tier — it is a tier the tick has to report as unpriced,
            // which is the only way an operator finds out before a month of it has gone by.
            RunningRatePerHourMinor = runningMinor,
            StoppedRatePerHourMinor = stoppedMinor
        };
        db.InstanceSizes.Add(size);
        await db.SaveChangesAsync(ct);

        await LogSizeRatesAsync(size, ct);

        TempData["Message"] = $"Size '{slug}' added.";
        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// Corrects a tier.
    ///
    /// The key is deliberately not editable: apps and databases store it, and renaming it would
    /// leave every one of them pointing at a size that no longer exists — silently, since a missing
    /// size reads as "no limit".
    /// </summary>
    [HttpPost("sizes/{id:guid}")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.PlansManage)]
    public async Task<IActionResult> UpdateSize(
        Guid id, string name, double cpuCores, long memoryMb, long diskGb, int sortOrder,
        string? runningRate, string? stoppedRate, CancellationToken ct)
    {
        var size = await db.InstanceSizes.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (size is null) return NotFound();

        if (ReadRate(runningRate, RunningRateEn, RunningRateFa, out var runningMinor) is { } refusal)
            return Refuse(refusal);
        if (ReadRate(stoppedRate, StoppedRateEn, StoppedRateFa, out var stoppedMinor) is { } stoppedRefusal)
            return Refuse(stoppedRefusal);

        size.Name = string.IsNullOrWhiteSpace(name) ? size.Name : name.Trim();
        size.CpuCores = cpuCores;
        size.MemoryBytes = memoryMb * 1024 * 1024;
        size.DiskBytes = diskGb * 1024 * 1024 * 1024;
        size.SortOrder = sortOrder;
        size.RunningRatePerHourMinor = runningMinor;
        size.StoppedRatePerHourMinor = stoppedMinor;
        await db.SaveChangesAsync(ct);

        await LogSizeRatesAsync(size, ct);

        // Says what it does not do. Instances already on this tier keep the figures they were given
        // — they carry their own copy — so a change here is about what happens next, not a resize
        // of everything running.
        // Limits and price part company here, and the message has to say so. An instance froze its
        // capacity when it was created, but the meter looks the rate up by size key on every tick —
        // so a price change reaches everything already running, including hours that have elapsed
        // and not yet been billed. Mint a new size key rather than repricing one in use.
        TempData["Message"] =
            $"'{size.Name}' updated. Instances already on it keep their current limits until they are " +
            "resized — but a price change applies to everything running on this size, from the next tick.";
        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// Stops offering a tier. Refused while anything is on it, for the same reason a plan is: a
    /// size that vanished reads as "no limit" everywhere it was named.
    /// </summary>
    [HttpPost("sizes/{id:guid}/disable")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.PlansManage)]
    public async Task<IActionResult> SetSizeEnabled(Guid id, bool enabled, CancellationToken ct)
    {
        var size = await db.InstanceSizes.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (size is null) return NotFound();

        if (!enabled)
        {
            var apps = await db.Apps.IgnoreQueryFilters().CountAsync(a => a.InstanceSizeKey == size.Key, ct);
            var services = await db.ManagedServices.IgnoreQueryFilters()
                .CountAsync(s => s.InstanceSizeKey == size.Key, ct);

            if (apps + services > 0)
            {
                TempData["Error"] = $"{apps + services} resource(s) are on '{size.Name}'. " +
                    "Move them first, or their limits would silently read as unlimited.";
                return RedirectToAction(nameof(Index));
            }
        }

        size.IsEnabled = enabled;
        await db.SaveChangesAsync(ct);
        TempData["Message"] = enabled ? $"'{size.Name}' is offered again." : $"'{size.Name}' is no longer offered.";
        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// Takes a plan out of circulation. Refused while tenants are on it: a workspace whose plan
    /// vanished falls back to the default one, which is a silent change to what they are allowed.
    /// </summary>
    [HttpPost("{id:guid}/disable")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.PlansManage)]
    public async Task<IActionResult> SetEnabled(Guid id, bool enabled, CancellationToken ct)
    {
        if (!IsProvider) return Forbid();

        var plan = await db.Plans.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (plan is null) return NotFound();

        var onIt = await db.Workspaces.CountAsync(w => w.PlanId == id, ct);
        if (!enabled && onIt > 0)
        {
            TempData["Error"] = $"{onIt} tenant(s) are on this plan. Move them first, or it would " +
                                "silently change what they are allowed.";
            return RedirectToAction(nameof(Index));
        }

        plan.IsEnabled = enabled;
        await db.SaveChangesAsync(ct);
        TempData["Message"] = enabled ? "Plan is available again." : "Plan withdrawn from new tenants.";
        return RedirectToAction(nameof(Index));
    }
}
