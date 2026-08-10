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
    /// <summary>
    /// A correction: an opposing line, because nothing here is ever edited or deleted.
    ///
    /// <para>
    /// <b>Nothing writes one.</b> No service, screen or command in this platform posts an
    /// adjustment — the member exists, and the ledger's unique index deliberately leaves room for
    /// two in one hour, but a credit that landed on the wrong workspace has no remedy in the
    /// product and has to be settled outside it. Said here rather than left to be discovered,
    /// because a kind that reads like a working feature is how somebody promises a customer one.
    /// </para>
    /// </summary>
    Adjustment = 3
}

/// <summary>What a line is for. Appended, never renumbered.</summary>
public enum BilledResourceType
{
    None = 0,
    App = 1,
    Service = 2,
    /// <summary>An app's volume. <c>ResourceId</c> is the <c>Volume</c> row's own id.</summary>
    Volume = 3,
    /// <summary>The plan-minimum line. Carries a null <c>ResourceId</c>.</summary>
    PlanBase = 4,

    /// <summary>
    /// The data disk under a managed database. <c>ResourceId</c> is the <b>managed service's</b> own
    /// id, because the disk has no row of its own: a <c>ManagedService</c> carries its
    /// <c>VolumeName</c> and <c>StorageBytes</c> on itself and has no relation to the <c>Volume</c>
    /// table at all, which is keyed by <c>AppId</c>.
    ///
    /// <para>
    /// A member of its own rather than <see cref="Volume"/> with a service id, for three reasons.
    /// It cannot collide with the database's own <see cref="Service"/> line for the hour, nor with
    /// an app volume's, and the index that makes a retried tick harmless is keyed on exactly
    /// <c>(WorkspaceId, ResourceType, ResourceId, BillingHour)</c> — reusing <see cref="Volume"/>
    /// would leave that index correct only because two tables happen never to mint the same
    /// <c>Guid</c>, which is an accident rather than a decision. It keeps
    /// <c>(ResourceType, ResourceId)</c> a key to exactly one table, so a bill screen or a support
    /// query resolving a line has one place to look instead of two — a reader that joined every
    /// <see cref="Volume"/> row to <c>Volumes</c> would find nothing for half of them and render a
    /// blank, which is the failure copying <c>ResourceName</c> exists to prevent, arriving through
    /// the id instead. And the customer can tell their database's disk from their app's disk by
    /// category rather than by reading a name they chose themselves.
    /// </para>
    ///
    /// <para>
    /// One line per service per hour, because a managed service has exactly one
    /// <c>VolumeName</c>. A second data volume on a database would need its own key, and the row it
    /// gained would supply one.
    /// </para>
    /// </summary>
    ServiceVolume = 5
}

/// <summary>Whether the resource was running for the hour being charged.</summary>
public enum BilledRunState
{
    NotApplicable = 0,
    Running = 1,
    Stopped = 2
}

/// <summary>
/// One line of one workspace's bill. <b>Append-only.</b> Nothing updates or deletes a row here, so
/// "why did my balance move" is a query rather than a reconstruction.
///
/// <para>
/// The shape a correction would take is a new <see cref="LedgerKind.Adjustment"/> line rather than
/// an edit — and nothing in the platform writes one. See the note on that member: this is a design
/// the schema is ready for and the product does not offer.
/// </para>
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
