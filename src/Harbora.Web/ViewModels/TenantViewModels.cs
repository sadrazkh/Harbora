using Harbora.Domain.Tenancy;

namespace Harbora.Web.ViewModels;

public sealed class TenantsPageViewModel
{
    public List<TenantRow> Tenants { get; set; } = new();
    public List<Plan> Plans { get; set; } = new();
}

public sealed record TenantRow(
    Guid WorkspaceId,
    string Name,
    string Slug,
    bool IsDefault,
    Guid? PlanId,
    string PlanName,
    int Members,
    int Apps,
    int Services,
    bool Suspended,
    /// <summary>
    /// Whether the suspension on this row is <see cref="Harbora.Domain.Identity.SuspensionReason.NoBalance"/>.
    ///
    /// <para>
    /// The list showed "suspended" and offered "resume", and the two kinds of suspension are not the
    /// same act to lift. An operator's own is two field writes. Billing's runs the workloads the
    /// suspension stopped back through the platform's start routes, each of which asks the billing
    /// gate — so on an empty balance it correctly refuses and the row stays suspended. An operator
    /// who cannot see which one they are looking at cannot tell a refusal from a bug.
    /// </para>
    /// </summary>
    bool SuspendedForNoBalance);

public sealed class TenantDetailsViewModel
{
    public Guid WorkspaceId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public bool IsDefault { get; set; }
    public bool Suspended { get; set; }
    public Application.Abstractions.WorkspaceUsage Usage { get; set; } = null!;

    /// <summary>
    /// False when this tenant has no wallet row — no hourly pass has ever reached them. Kept apart
    /// from a balance of zero: "never billed" and "nothing left" want opposite responses, and the
    /// screen prints a dash for the first rather than a nought.
    /// </summary>
    public bool HasWallet { get; set; }

    /// <summary>Minor units, as the ledger stores them. Converted once, in the view.</summary>
    public long BalanceMinor { get; set; }

    public string Currency { get; set; } = "IRR";
    public long LedgerBalanceMinor { get; set; }
    public long BalanceDifferenceMinor { get; set; }
    public bool BalanceReconciled => BalanceDifferenceMinor == 0;

    // Metered usage for the current billing month.
    public double MemoryGbHours { get; set; }
    public double CpuCoreHours { get; set; }
    public int AppCountPeak { get; set; }
    public string PeriodLabel { get; set; } = string.Empty;

    public List<TenantMember> Members { get; set; } = new();

    /// <summary>
    /// What this tenant is entitled to, and who decided it.
    ///
    /// <para>
    /// Here as well as on the features console because this is where an operator is standing when a
    /// customer asks. The console answers "who has Functions"; this answers "what does this customer
    /// have", and they are the same rows read from two ends.
    /// </para>
    /// </summary>
    public List<TenantFeature> Features { get; set; } = new();
}

/// <summary>One feature as it resolves for one tenant.</summary>
public sealed record TenantFeature(
    string Key, string Name, string Pitch,
    Harbora.Domain.Features.FeatureState State,
    Harbora.Domain.Features.FeatureDecision DecidedBy);

public sealed record TenantMember(Guid UserId, string Email, string DisplayName, string WorkspaceRole, bool Active)
{
    /// <summary>When true, this person only reaches the projects listed below.</summary>
    public bool ScopedToProjects { get; init; }

    /// <summary>Their grants, already written out as sentences — see ProjectAccess.Describe.</summary>
    public IReadOnlyList<(Guid Id, string Text)> Grants { get; init; } = [];
}

