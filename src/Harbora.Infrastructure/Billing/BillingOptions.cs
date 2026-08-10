namespace Harbora.Infrastructure.Billing;

/// <summary>
/// How the hourly charge behaves. Every value here is deliberately visible to an operator, because
/// each one decides how somebody's money moves.
/// </summary>
public sealed class BillingOptions
{
    public const string SectionName = "Billing";

    /// <summary>
    /// The Web host ships this as enabled for a hosted PaaS: an unfunded customer must not allocate
    /// billable capacity. The CLR default stays false so another host that forgets to bind its
    /// configuration fails safe without unexpectedly moving money.
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

    /// <summary>How often an incomplete hour is offered to the durable queue again.</summary>
    public int IncompleteRetryMinutes { get; set; } = 30;

    /// <summary>How often the lightweight scheduler looks for a newly ended or retryable hour.</summary>
    public int SchedulerPollSeconds { get; set; } = 60;

    /// <summary>What <see cref="Currency"/> falls back to: the currency this platform was built for.</summary>
    public const string DefaultCurrency = "IRR";

    /// <summary>
    /// ISO 4217 code for the money on this install. One code, for everybody: a wallet carries a copy
    /// so an old row goes on saying what it was denominated in, but nothing here converts between two
    /// currencies and nothing should be read as though it does.
    ///
    /// <para>
    /// It ships as <see cref="DefaultCurrency"/> so an existing install sees no change at all. A
    /// provider selling in something else sets it <b>before the first charge</b>: every wallet
    /// already open keeps the code it was created with, because rewriting them would silently
    /// redenominate balances somebody has already been billed against.
    /// </para>
    /// </summary>
    public string Currency { get; set; } = DefaultCurrency;

    /// <summary>
    /// The code as it is actually written onto a wallet or printed beside a balance. A key present
    /// but blank in a configuration file is an operator who has said nothing, not an operator who
    /// wants a bill with no currency on it.
    /// </summary>
    public string CurrencyOrDefault =>
        string.IsNullOrWhiteSpace(Currency) ? DefaultCurrency : Currency.Trim();
}
