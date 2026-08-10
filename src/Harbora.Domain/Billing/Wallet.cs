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
    /// The balance the outstanding low-balance warning was sent at, or null when no warning is
    /// outstanding. It is what stops the same customer being told twenty times.
    ///
    /// <para>
    /// A balance rather than a timestamp, and that is the whole design. "Warned at 14:00" needs
    /// somebody to remember to clear it — the credit path, the adjustment path, whatever settles a
    /// payment next year — and a flag that only stays honest while every writer remembers is a flag
    /// that eventually lies. A balance re-arms itself: the hourly pass only ever takes money out, so
    /// a balance ABOVE the one that was warned at can only mean somebody put money in, and that is
    /// exactly the moment a second warning becomes news again. The other re-arming half — climbing
    /// clear of the warning window altogether — is <see cref="BillingTick"/>'s to notice, and it
    /// writes null back here when it does.
    /// </para>
    ///
    /// <para>
    /// Null on every existing row, which is the correct starting state: nobody has been warned yet.
    /// </para>
    /// </summary>
    public long? LowBalanceWarnedAtBalanceMinor { get; set; }

    /// <summary>
    /// Concurrency token. The tick and an administrator's credit can land in the same second, and
    /// last-write-wins on a balance loses somebody's money.
    /// </summary>
    public Guid ConcurrencyStamp { get; set; } = Guid.CreateVersion7();
}
