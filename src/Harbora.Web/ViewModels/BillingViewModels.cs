using Harbora.Infrastructure.Billing;

namespace Harbora.Web.ViewModels;

/// <summary>
/// What one workspace was charged over one period, and what it has paid in.
///
/// <para>
/// Every figure here is <see cref="long"/> minor units, exactly as the ledger stores them, and stays
/// that way until <c>MinorUnits.Format</c> is called in the view. The conversion happens once, at the
/// last possible moment, so nothing in between can round a bill.
/// </para>
/// </summary>
public sealed class BillingPageViewModel
{
    public string WorkspaceName { get; init; } = string.Empty;

    /// <summary>
    /// False when this workspace has no wallet row at all — it has never been through an hourly
    /// pass. Kept apart from a balance of zero on purpose: "nobody has billed you yet" and "you have
    /// nothing left" are opposite situations, and printing 0 for the first is the same mistake as
    /// printing 0 for an unmeasured disk.
    /// </summary>
    public bool HasWallet { get; init; }

    public long BalanceMinor { get; init; }
    public string Currency { get; init; } = "IRR";

    public bool Suspended { get; init; }

    /// <summary>
    /// True when the suspension is the one a payment lifts. The other kind is not lifted by paying,
    /// and telling a customer to top up would be sending them to spend money on nothing.
    /// </summary>
    public bool SuspendedForNoBalance { get; init; }

    /// <summary>The month being shown, as <c>yyyy-MM</c>. Also what the links either side carry.</summary>
    public string Period { get; init; } = string.Empty;
    public string PreviousPeriod { get; init; } = string.Empty;

    /// <summary>Empty when the period shown is the current one — there is no bill after this one yet.</summary>
    public string? NextPeriod { get; init; }

    /// <summary>One row per thing the workspace held, most expensive first.</summary>
    public IReadOnlyList<ResourceCost> Costs { get; init; } = [];

    /// <summary>Money paid in during the same window, newest first, never summed into the table above.</summary>
    public IReadOnlyList<BillingCreditRow> Credits { get; init; } = [];

    /// <summary>Signed, so it adds up with <see cref="CreditTotalMinor"/> to the period's movement.</summary>
    public long CostTotalMinor => Costs.Sum(c => c.TotalMinor);

    public long CreditTotalMinor => Credits.Sum(c => c.AmountMinor);
}

/// <param name="AmountMinor">Positive: a credit is money in.</param>
/// <param name="Note">Why it moved, in the words of whoever moved it.</param>
public sealed record BillingCreditRow(DateTimeOffset Hour, long AmountMinor, string Note);

/// <summary>
/// The page an administrator confirms a credit on.
///
/// <para>
/// It exists for two reasons at once, and they turn out to be the same mechanism. Money moving needs
/// somebody to look at the figure before it moves — the pattern every destructive action on this
/// panel already follows — and a credit needs an identity of its own so that submitting the form
/// twice cannot apply it twice. <see cref="CreditId"/> is minted when this page is rendered, so the
/// act of confirming is what the id belongs to.
/// </para>
/// </summary>
public sealed class TenantCreditViewModel
{
    public Guid WorkspaceId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Slug { get; init; } = string.Empty;

    /// <summary>This confirmation's own id. One rendering, one credit, however many times it is posted.</summary>
    public Guid CreditId { get; init; }

    public bool HasWallet { get; init; }
    public long BalanceMinor { get; init; }
    public string Currency { get; init; } = "IRR";

    public bool Suspended { get; init; }
    public bool SuspendedForNoBalance { get; init; }

    /// <summary>What the administrator had typed, handed back when the form is re-rendered.</summary>
    public string? Amount { get; init; }
    public string? Note { get; init; }
}
