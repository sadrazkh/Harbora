using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Servers;
using Harbora.Domain.Tenancy;
using Harbora.Infrastructure.Billing;
using Harbora.Infrastructure.Tenancy;
using Harbora.Web.ViewModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Harbora.Web.Infrastructure;

/// <summary>
/// Assembles the shared size chooser: which hosts this workspace may place on, which tiers each host
/// offers it, and what each costs.
///
/// <para>
/// One service rather than the same joins written into four controllers. The rules it applies are not
/// obvious individually and are certainly not obvious four times over — a plan's pool, a plan's
/// allowed keys, a host's free capacity, a host's withdrawal of a tier, and whether anybody has
/// priced the pair at all.
/// </para>
/// </summary>
public sealed class SizePickerService(
    HarboraDbContext db,
    INodeCapacityService capacity,
    IOptions<BillingOptions> billing)
{
    /// <summary>
    /// The chooser for one workspace.
    /// </summary>
    /// <param name="pinnedServerId">
    /// When set, only that host is offered — a resize keeps the workload where it is, because moving
    /// one between hosts severs its private network and has its own confirmation screen.
    /// </param>
    public async Task<SizePickerModel> BuildAsync(
        Guid workspaceId,
        string sizeFieldName,
        string? serverFieldName,
        string? selectedSizeKey,
        Guid? selectedServerId,
        bool allowNoLimit,
        Guid? pinnedServerId,
        CancellationToken ct)
    {
        var plan = await PlanForAsync(workspaceId, ct);

        // Empty means every tier, which is what the quota check already reads it as. Split here once
        // rather than string-searching per tier, and case-insensitively because half a dozen places
        // compare these keys that way.
        var allowedKeys = (plan?.AllowedSizeKeys ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var sizes = await db.InstanceSizes.AsNoTracking()
            .Where(s => s.IsEnabled).OrderBy(s => s.SortOrder).ToListAsync(ct);

        var offers = await db.ServerInstanceOffers.AsNoTracking().ToListAsync(ct);

        var servers = await db.Servers.AsNoTracking()
            .OrderByDescending(s => s.IsLocal).ThenBy(s => s.Name).ToListAsync(ct);

        if (pinnedServerId is { } pinned)
            servers = servers.Where(s => s.Id == pinned).ToList();

        // A plan pinned to a pool may only place inside it, the same rule SchedulerService applies.
        // Filtered out rather than shown disabled: a pool a tenant will never be allowed into is not
        // a choice they are being refused, it is somebody else's hardware.
        if (!string.IsNullOrWhiteSpace(plan?.NodePool))
            servers = servers
                .Where(s => string.Equals(s.Pool, plan!.NodePool, StringComparison.OrdinalIgnoreCase))
                .ToList();

        var capacities = await capacity.GetAllAsync(ct);

        var rows = new List<SizePickerServerViewModel>(servers.Count);
        foreach (var server in servers)
        {
            var reading = capacities.FirstOrDefault(c => c.ServerId == server.Id);

            var tiers = sizes.Select(size => Tier(size, server, reading, offers, allowedKeys)).ToList();

            // Derived, never stored: a host badged "memory-optimised" in a column of its own could
            // contradict the tiers it actually offers, and nothing would report the disagreement.
            var families = InstanceSizeFamily.Present(
                tiers.Where(t => t.Selectable).Select(t => t.Family));

            rows.Add(new SizePickerServerViewModel(
                server.Id,
                server.Name,
                server.Hostname,
                server.IsLocal,
                ServerUnavailable(reading, tiers),
                reading?.FreeMemoryBytes ?? 0,
                reading?.FreeCpu ?? 0,
                families.ToList(),
                tiers));
        }

        // The host the chooser opens on: the one asked for, else the first that can actually take work,
        // so it does not open on a card the customer has to notice is refused.
        var openServerId = selectedServerId ?? rows.FirstOrDefault(r => r.Selectable)?.ServerId;

        // And the tier it opens on.
        //
        // <b>A create form must open on something.</b> The size used to be a <select>, which always
        // posts its first option, so an install that never set a platform default still created apps on
        // a tier. Cards post nothing until one is chosen — so with no default and nothing preselected,
        // the form would submit an empty InstanceSizeKey, the binder would accept it, and the app would
        // be created on no tier at all: no ceiling, and an hourly pass reporting it as something it
        // cannot price. Resolved here rather than in the script, so it holds with JavaScript off.
        //
        // Not applied when "no ceiling" is offered: that is the resize controls, where a null key is
        // the state the resource is already in and choosing a tier for somebody would be a resize they
        // did not ask for.
        var openSizeKey = selectedSizeKey;
        if (!allowNoLimit && string.IsNullOrEmpty(openSizeKey))
            openSizeKey = rows.FirstOrDefault(r => r.ServerId == openServerId)
                ?.Tiers.FirstOrDefault(t => t.Selectable)?.Key;

        return new SizePickerModel(
            sizeFieldName,
            serverFieldName,
            openSizeKey,
            openServerId,
            allowNoLimit,
            billing.Value.Enabled,
            rows);
    }

    /// <summary>
    /// The workspace's plan, or the default one. Same resolution the hourly pass makes, so the
    /// chooser cannot offer a tier the meter would then refuse to price.
    /// </summary>
    private async Task<Plan?> PlanForAsync(Guid workspaceId, CancellationToken ct)
    {
        var planId = await db.Workspaces.IgnoreQueryFilters().AsNoTracking()
            .Where(w => w.Id == workspaceId).Select(w => w.PlanId).FirstOrDefaultAsync(ct);

        return planId is { } id
            ? await db.Plans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct)
            : await db.Plans.AsNoTracking().FirstOrDefaultAsync(p => p.IsDefault, ct);
    }

    private SizePickerTierViewModel Tier(
        InstanceSize size,
        Server server,
        NodeCapacity? reading,
        List<ServerInstanceOffer> offers,
        HashSet<string> allowedKeys)
    {
        var offer = offers.FirstOrDefault(o =>
            o.ServerId == server.Id
            && string.Equals(o.InstanceSizeKey, size.Key, StringComparison.OrdinalIgnoreCase));

        var running = ServerRates.ForWorkload(size, offer, Domain.Billing.BilledRunState.Running);
        var stopped = ServerRates.ForWorkload(size, offer, Domain.Billing.BilledRunState.Stopped);

        return new SizePickerTierViewModel(
            size.Key,
            string.IsNullOrWhiteSpace(size.Name) ? size.Key : size.Name,
            InstanceSizeFamily.Normalise(size.Family),
            size.CpuCores,
            size.MemoryBytes,
            size.DiskBytes,
            running,
            stopped,
            TierUnavailable(size, offer, reading, allowedKeys, running));
    }

    /// <summary>
    /// Why this tier cannot be chosen on this host, in the order the reasons are worth reading.
    ///
    /// <para>
    /// Ordered deliberately: the plan first, because "your plan does not include this" is the one the
    /// customer can act on; then the host's withdrawal; then capacity, which may clear on its own;
    /// then price, which is the provider's unfinished job. Reporting the last of those when the first
    /// also applies would send somebody to ask about a price they were never going to be sold.
    /// </para>
    /// </summary>
    private SizeUnavailable TierUnavailable(
        InstanceSize size,
        ServerInstanceOffer? offer,
        NodeCapacity? reading,
        HashSet<string> allowedKeys,
        long? runningRate)
    {
        if (allowedKeys.Count > 0 && !allowedKeys.Contains(size.Key)) return SizeUnavailable.NotInPlan;

        if (!ServerRates.OffersNewWork(offer)) return SizeUnavailable.NotOfferedHere;

        // No reading is not "no room": a host nobody has measured is the state a fresh install is in,
        // and CanFit already treats an unknown allocatable figure as "do not filter".
        if (reading is not null && !reading.CanFit(size.MemoryBytes, size.CpuCores))
            return SizeUnavailable.NoCapacity;

        // Only when billing is on. With it off, a price is not a gate anywhere else either — the
        // creation charge is skipped entirely — and refusing every tier on a fresh install, where
        // nothing is priced yet, would mean nobody could create anything at all.
        if (billing.Value.Enabled && runningRate is null) return SizeUnavailable.NotPriced;

        return SizeUnavailable.None;
    }

    private static SizeUnavailable ServerUnavailable(
        NodeCapacity? reading, List<SizePickerTierViewModel> tiers)
    {
        // Offline beats everything: nothing can be placed here now whatever it is priced at. An
        // unmeasured host is NOT offline — see the reading note above.
        if (reading is { IsOnline: false }) return SizeUnavailable.ServerOffline;

        // A host with no usable tier is still drawn, so the operator can see it is selling nothing.
        return tiers.Any(t => t.Selectable) ? SizeUnavailable.None : SizeUnavailable.NothingOffered;
    }
}
