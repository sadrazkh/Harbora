using Harbora.Application.Abstractions;
using Harbora.Domain.Tenancy;
using Harbora.Infrastructure.Tenancy;

namespace Harbora.Web.ViewModels;

public sealed class PlansPageViewModel
{
    public WorkspaceUsage Usage { get; set; } = null!;
    public bool IsProvider { get; set; }
    public List<Plan> Plans { get; set; } = new();
    public List<InstanceSize> Sizes { get; set; } = new();

    /// <summary>The object-storage tiers, listed beside the compute ones and edited the same way.</summary>
    public List<Harbora.Domain.Storage.StoragePlan> StoragePlans { get; set; } = new();

    /// <summary>
    /// Tenants already past a limit. Carried on the model rather than in ViewBag: it is a typed
    /// list the page renders per resource, and an untyped one is how a new resource gets added to
    /// the rule and quietly never appears on the page.
    /// </summary>
    public List<TenantOverPlanViewModel> Overages { get; set; } = new();

    /// <summary>
    /// The price matrix: one row per server, each carrying every tier and what that server charges
    /// for it. Provider-only, and empty for anybody else.
    /// </summary>
    public List<ServerPriceRowViewModel> ServerPrices { get; set; } = new();
}

public sealed record TenantOverPlanViewModel(string Tenant, string PlanName, PlanBreach Breach);

/// <summary>One server, and what it charges for each tier.</summary>
public sealed record ServerPriceRowViewModel(
    Guid ServerId,
    string Name,
    string Hostname,
    bool IsLocal,
    List<ServerPriceCellViewModel> Cells);

/// <summary>
/// What one server charges for one tier, with the figure it would inherit alongside it.
///
/// <para>
/// <see cref="InheritedRunningMinor"/> and <see cref="InheritedStoppedMinor"/> are the tier's own
/// rates, carried here so the form can print them <b>inside</b> the empty box as its placeholder. On
/// this page an empty price box means "not priced" everywhere else and "inherit" here — opposite
/// meanings in identical controls — and a note under a grid is not where somebody scanning it will
/// read the difference. Showing the inherited figure in the box states it at the point of confusion.
/// </para>
/// </summary>
public sealed record ServerPriceCellViewModel(
    string SizeKey,
    string SizeName,
    string Family,
    bool IsOffered,
    long? RunningMinor,
    long? StoppedMinor,
    long? InheritedRunningMinor,
    long? InheritedStoppedMinor)
{
    /// <summary>
    /// What the hour actually costs here — the override if there is one, otherwise the tier's own
    /// rate. The figure the matrix shows as the effective price, so an operator does not have to
    /// resolve the precedence in their head across two columns.
    /// </summary>
    public long? EffectiveRunningMinor => RunningMinor ?? InheritedRunningMinor;

    /// <inheritdoc cref="EffectiveRunningMinor"/>
    public long? EffectiveStoppedMinor => StoppedMinor ?? InheritedStoppedMinor;
}
