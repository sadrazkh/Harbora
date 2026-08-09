using Harbora.Domain.Common;

namespace Harbora.Domain.Billing;

/// <summary>
/// What a ledger line is. Appended, never renumbered — rows hold these by value.
/// </summary>
public enum LedgerKind
{
    /// <summary>An hour of one reserved resource, written by the tick.</summary>
    Charge = 0,
    /// <summary>Money put in by an administrator.</summary>
    Credit = 1,
    /// <summary>
    /// The difference between the plan's hourly minimum and the sum of the hour's resource lines,
    /// so the ledger totals exactly what left the wallet and the customer can see why.
    /// </summary>
    PlanMinimumTopUp = 2,
    /// <summary>A correction. Nothing is ever edited or deleted; a mistake gets an opposing line.</summary>
    Adjustment = 3
}

/// <summary>What a line is for. Appended, never renumbered.</summary>
public enum BilledResourceType
{
    None = 0,
    App = 1,
    Service = 2,
    Volume = 3,
    /// <summary>The plan-minimum line. Carries a null <c>ResourceId</c>.</summary>
    PlanBase = 4
}

/// <summary>Whether the resource was running for the hour being charged.</summary>
public enum BilledRunState
{
    NotApplicable = 0,
    Running = 1,
    Stopped = 2
}

/// <summary>
/// One line of one workspace's bill. <b>Append-only.</b> Nothing updates or deletes a row here: a
/// correction is a new <see cref="LedgerKind.Adjustment"/> line, so "why did my balance move" is a
/// query rather than a reconstruction.
///
/// <para>
/// <see cref="ResourceName"/> is copied rather than joined on purpose. An app deleted next month
/// must still be readable on this month's bill, and a join to a row that is gone renders a blank
/// where the customer is looking for a name.
/// </para>
/// </summary>
public class BillingLedgerEntry : BaseEntity
{
    public Guid WorkspaceId { get; set; }

    /// <summary>The UTC hour this line pays for — always an hour that has already ended.</summary>
    public DateTimeOffset BillingHour { get; set; }

    public LedgerKind Kind { get; set; }

    /// <summary>
    /// Signed minor units: charges negative, credits positive. The sign lives here rather than in
    /// <see cref="Kind"/> so the balance is <c>SUM(AmountMinor)</c> and no reader needs a table of
    /// which kinds subtract.
    /// </summary>
    public long AmountMinor { get; set; }

    public BilledResourceType ResourceType { get; set; }
    public Guid? ResourceId { get; set; }

    /// <summary>Copied at write time. See the note on the class.</summary>
    public string ResourceName { get; set; } = string.Empty;

    public BilledRunState RunState { get; set; }

    /// <summary>The rate and the hours, kept so the arithmetic on the line can be checked by eye.</summary>
    public long RatePerHourMinor { get; set; }
    public int Hours { get; set; } = 1;

    public string Description { get; set; } = string.Empty;

    /// <summary>Set on credits and adjustments so a person's money movement has a person on it.</summary>
    public Guid? CreatedByUserId { get; set; }
}
