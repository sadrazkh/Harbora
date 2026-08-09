using Harbora.Domain.Common;

namespace Harbora.Domain.Billing;

/// <summary>
/// A workspace's spendable balance.
///
/// <para>
/// This is a <b>cached total</b>, not the truth. The truth is <c>SUM(BillingLedgerEntry.AmountMinor)</c>,
/// which is why a reconcile check can prove the two agree — a balance with no ledger behind it can
/// only be trusted, and money should be checkable instead.
/// </para>
/// </summary>
public class Wallet : BaseEntity
{
    public Guid WorkspaceId { get; set; }

    /// <summary>Whole minor units. Never a floating type — see the note in the plan's constraints.</summary>
    public long BalanceMinor { get; set; }

    /// <summary>ISO 4217 code. One currency per install.</summary>
    public string Currency { get; set; } = "IRR";

    /// <summary>
    /// Warn when the balance is worth less than this many hours at the current burn rate. Zero
    /// disables the warning, as a zero does on every other limit in this platform.
    /// </summary>
    public int LowBalanceHours { get; set; } = 24;

    /// <summary>
    /// Concurrency token. The tick and an administrator's credit can land in the same second, and
    /// last-write-wins on a balance loses somebody's money.
    /// </summary>
    public Guid ConcurrencyStamp { get; set; } = Guid.CreateVersion7();
}
