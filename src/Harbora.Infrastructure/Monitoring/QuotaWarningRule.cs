using Harbora.Application.Abstractions;
using Harbora.Infrastructure.Tenancy;

namespace Harbora.Infrastructure.Monitoring;

/// <summary>
/// One plan cap a workspace is close to, for the subject/body of a quota warning.
/// </summary>
/// <param name="ResourceEn">The resource's name, English.</param>
/// <param name="ResourceFa">The same resource, Persian.</param>
/// <param name="Percent">
/// Share of the cap already committed, 0–100 — <see cref="AllocationReading.Percent"/> under the
/// same rounding the Plans usage page already draws its bars from.
/// </param>
/// <param name="Detail">
/// The two raw figures behind <see cref="Percent"/> ("4/5", "3.2 GB / 4 GB") — a percentage alone
/// answers "how full", not "how much room is actually left", and a customer deciding whether to act
/// needs the second question answered too.
/// </param>
public readonly record struct QuotaBreach(string ResourceEn, string ResourceFa, int Percent, string Detail);

/// <summary>
/// C1 (2026-08-27 "warn before the refusal"): which of a workspace's committed-capacity plan caps are
/// worth an approaching-the-limit warning, and how close counts as close.
///
/// <para>
/// <b>Reuse, not a second computation.</b> Every figure here comes from the exact
/// <see cref="WorkspaceUsage"/> <see cref="IQuotaService.GetUsageAsync"/> already returns — the same
/// method <c>PlansController</c> renders the usage screen from and <c>QuotaService</c>'s own refusal
/// paths (<c>CanAddAppAsync</c>, <c>CanAddServiceAsync</c>, <c>CanAddWorkloadsAsync</c>) are computed
/// alongside. A warning built from a second query could disagree with the refusal it is meant to give
/// advance notice of; this cannot, because there is only the one query.
/// </para>
///
/// <para>
/// <b>Why Apps, Services, Memory, CPU and Disk, and not the rest of <see cref="WorkspaceUsage"/>.</b>
/// Those five are what <c>QuotaService</c> itself calls "metered capacity" — usage that grows on its
/// own as a workspace runs its workload, with no single deliberate click behind each increment, so a
/// customer can cross 80% without ever having "decided" to. <c>QuotaService</c>'s own comment calls
/// the rest — members, projects, environments, domains, volumes, backup schedules, cron jobs —
/// "governance ceilings, not metered capacity": each one only moves when a person deliberately invites
/// a member or creates a project, the count is small and already visible on the page that manages it,
/// and a warning that nags "3 of 5 projects" every collector tick would train people to ignore this
/// channel before it ever said something that mattered. <c>MaxConcurrentDeployments</c> is left out for
/// a different reason — it is a transient in-flight count that can legitimately cross and clear within
/// minutes, not a level a workspace holds — and <c>MaxBackupRetentionCount</c> is a policy knob, not a
/// quantity anything is measured against.
/// </para>
///
/// <para>
/// <b>Disk gets one more guard the other four do not need.</b> <see cref="WorkspaceUsage.DiskUnmeasured"/>
/// counts volumes nothing has measured yet, and <see cref="WorkspaceUsage.DiskUsedBytes"/> only sums
/// what has been — so treating that sum as the whole truth while volumes remain unmeasured would read
/// as "comfortably under" a workspace that might already be over. This skips disk entirely rather than
/// warn on a figure it knows is incomplete, the same "not measured" rule <c>DiskQuota.Caveat</c> and
/// the Plans usage screen already apply to the same number.
/// </para>
/// </summary>
public static class QuotaWarningRule
{
    /// <summary>
    /// Every metered cap the workspace has already committed at least <paramref name="warnRatio"/> of.
    /// Empty when nothing is close — including when the workspace has no plan, or a plan with every
    /// one of these caps at zero (unlimited): <see cref="AllocationReading.Of"/> reads a zero-or-less
    /// allocation as <c>Unlimited</c>, which this treats exactly like "not close", because there is no
    /// limit for it to be close to. A cap with a limit but nothing measured yet reads as
    /// <c>Unmeasured</c> and is skipped the same way — never counted as 0% used.
    /// </summary>
    public static IReadOnlyList<QuotaBreach> Breaches(WorkspaceUsage u, double warnRatio)
    {
        var line = (int)Math.Round(Math.Clamp(warnRatio, 0, 1) * 100, MidpointRounding.AwayFromZero);
        var breaches = new List<QuotaBreach>();

        void Consider(string en, string fa, double? used, double max, string detail)
        {
            var reading = AllocationReading.Of(used, max);
            if (reading.Kind == AllocationKind.Known && reading.Percent >= line)
                breaches.Add(new QuotaBreach(en, fa, reading.Percent, detail));
        }

        // Same English/Persian names the Plans usage screen already uses for these five figures
        // (Views/Plans/Index.cshtml) — a warning naming "Services" for the row the rest of the panel
        // calls "Databases" would read as a different resource to the person comparing the two.
        Consider("Apps", "اپلیکیشن", u.Apps, u.MaxApps, $"{u.Apps}/{u.MaxApps}");
        Consider("Databases", "دیتابیس", u.Services, u.MaxServices, $"{u.Services}/{u.MaxServices}");
        Consider("Memory", "حافظه", u.MemoryUsedBytes, u.MaxMemoryBytes,
            $"{ByteSize.Measured(u.MemoryUsedBytes)}/{ByteSize.Format(u.MaxMemoryBytes)}");
        Consider("CPU", "پردازنده", u.CpuUsed, u.MaxCpuCores,
            $"{u.CpuUsed:0.##}/{u.MaxCpuCores:0.##} cores");

        if (u.DiskUnmeasured == 0)
            Consider("Disk", "دیسک", u.DiskUsedBytes, u.MaxDiskBytes,
                $"{ByteSize.Measured(u.DiskUsedBytes)}/{ByteSize.Format(u.MaxDiskBytes)}");

        return breaches;
    }
}
