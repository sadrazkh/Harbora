using Harbora.Domain.Common;

namespace Harbora.Domain.Billing;

/// <summary>
/// A workspace's spendable balance.
///
/// <para>
/// This is a <b>cached total</b>, not the truth. The truth is <c>SUM(BillingLedgerEntry.AmountMinor)</c>
/// over the same workspace, and the two are written in one transaction everywhere money moves so that
/// they cannot part company. That the ledger exists at all is what makes a balance checkable rather
/// than merely trusted.
/// </para>
///
/// <para>
/// <b>Checkable, not checked.</b> Nothing in the product reconciles the two — no command, no health
/// probe, no screen. The only place they are compared is in the test suite. So "the ledger proves the
/// balance" is a property of the design, and until something reads it on a live install it stays one.
/// </para>
///
/// <para>
/// <b>Nothing in the product checks it.</b> There is no reconcile screen, command or scheduled pass —
/// the only place the two are compared is the test suite, which proves the code that writes them
/// keeps them level, not that any particular install's rows are. An operator who suspects a drift
/// reads it off the database themselves:
/// <c>SELECT w."WorkspaceId", w."BalanceMinor", COALESCE(SUM(l."AmountMinor"), 0) FROM "Wallets" w
/// LEFT JOIN "BillingLedger" l ON l."WorkspaceId" = w."WorkspaceId" GROUP BY w."WorkspaceId",
/// w."BalanceMinor"</c>. Saying so here rather than implying a check that does not exist:
/// a comment promising one is how nobody goes looking for the drift.
/// </para>
/// </summary>
public class Wallet : BaseEntity
{
    public Guid WorkspaceId { get; set; }

    /// <summary>Whole minor units. Never a floating type — see the note in the plan's constraints.</summary>
    public long BalanceMinor { get; set; }

    /// <summary>
    /// ISO 4217 code. One currency per install, set by <c>Billing:Currency</c> and copied here when
    /// the wallet is opened.
    ///
    /// <para>
    /// Copied rather than read from the setting at render time, for the same reason a ledger line
    /// copies the name of the app it charged: a provider who changes the setting must not silently
    /// redenominate a balance that was billed in something else. The initialiser below is the
    /// shipped default and the value a wallet built in a test carries; the two places that open one
    /// for real both write the setting over it.
    /// </para>
    /// </summary>
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
