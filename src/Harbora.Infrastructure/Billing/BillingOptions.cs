namespace Harbora.Infrastructure.Billing;

/// <summary>
/// How the hourly charge behaves. Every value here is deliberately visible to an operator, because
/// each one decides how somebody's money moves.
/// </summary>
public sealed class BillingOptions
{
    public const string SectionName = "Billing";

    /// <summary>
    /// Off by default. Billing is a commercial decision, and an install that upgrades into it
    /// without being asked would start charging tenants who were never told there was a price.
    ///
    /// <para>
    /// Read by <see cref="BillingTick"/> itself and not only by whatever schedules it. The switch
    /// guards the money, so it belongs on the method that moves the money — a second caller added
    /// later (an admin's "charge now" button, a recovery command) would otherwise have to remember.
    /// </para>
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// How many missed hours one catch-up will pay for. A panel that was down must not have hosted
    /// for free, but it must not silently decide how much free hosting is acceptable either —
    /// reaching this bound is a warning naming what was dropped, never a quiet skip.
    ///
    /// <para>
    /// The oldest hours are paid first and the newest are the ones dropped, so an hour beyond the
    /// bound is one nobody has been billed for yet and the next catch-up reaches it. Three days is
    /// the shipped value: long enough to cover a weekend outage, short enough that a panel restored
    /// from an old backup does not issue a month of bills in one pass.
    /// </para>
    /// </summary>
    public int MaxBackfillHours { get; set; } = 72;
}
