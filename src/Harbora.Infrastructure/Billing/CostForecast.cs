namespace Harbora.Infrastructure.Billing;

/// <summary>
/// The two questions a bill cannot answer on its own: what the month is heading towards, and when the
/// balance runs out at that rate.
///
/// <para>
/// <b>Every figure here is a claim about the future, not a record of the past.</b> It assumes the
/// workloads billed in the most recently charged hour keep running exactly as they are — no scale-up,
/// no scale-down, no new app, nobody topping up. <see cref="WalletService.ForecastAsync"/> is the one
/// place that assumption is made explicit; nothing here should be shown to a customer without saying
/// so beside it, the same rule <see cref="MonthlyEstimate"/> already keeps for a single resource's
/// rate.
/// </para>
/// </summary>
/// <param name="HasEnoughHistory">
/// False means every other figure here except <see cref="SpentSoFarMinor"/> is unset. A workspace
/// billed for only a handful of hours has not shown this feature a stable pattern yet — the one hour
/// it has might be a deploy in progress, a trial resource spun up and about to be torn down, or simple
/// backfill noise — and turning that single hour into a month is the confident-wrong-number failure
/// this whole feature exists to avoid. See <see cref="WalletService.MinimumHistoryHours"/> for the
/// bound and why it is where it is.
/// </param>
/// <param name="HistoryHours">
/// How many distinct hours this workspace has ever been billed for, so a caller can say "not enough
/// history yet (6 of 24 hours)" rather than a bare refusal.
/// </param>
/// <param name="MinimumHistoryHours">Copied from <see cref="WalletService.MinimumHistoryHours"/> so a
/// view never has to import the service that computed this just to read a constant off it.</param>
/// <param name="SpentSoFarMinor">
/// What the ledger already shows for the period asked about, up to now. Not a forecast — every minor
/// unit of it already happened — and shown even when there is not enough history to project further,
/// because "what have I spent so far" is always a fact rather than a guess.
/// </param>
/// <param name="BurnRateHourlyMinor">
/// What the most recently completed billing hour cost, in minor units. This is "today's running
/// workloads" made concrete: it is priced by the same hourly pass that writes the real bill, not by a
/// second calculation invented for this screen, so it moves the moment a workload is stopped,
/// resized, or a workspace is suspended — the next hour the tick runs prices whatever is actually
/// there. Zero means nothing currently running costs anything.
/// </param>
/// <param name="ProjectedPeriodTotalMinor">
/// <see cref="SpentSoFarMinor"/> plus <see cref="BurnRateHourlyMinor"/> for every hour of the period
/// still to come. Null only when that sum would be too large to state honestly — see the overflow
/// guard in <see cref="WalletService.ForecastAsync"/> — never because the figure is merely large.
/// </param>
/// <param name="RunwayHours">
/// <see cref="BurnRate.RunwayHours"/> against the wallet's balance right now. Null when nothing is
/// currently costing money, in which case the balance is not being drawn down at all rather than
/// lasting an unstatable number of hours.
/// </param>
/// <param name="RunwayDate">
/// <see cref="BurnRate.RunwayDate"/> for the same pair — the same hours, expressed as a moment
/// instead of a count, because "around the 25th" is the sentence a customer decides a top-up from and
/// "412 hours" is not.
/// </param>
public sealed record CostForecast(
    bool HasEnoughHistory,
    int HistoryHours,
    int MinimumHistoryHours,
    long SpentSoFarMinor,
    long BurnRateHourlyMinor,
    long? ProjectedPeriodTotalMinor,
    long? RunwayHours,
    DateTimeOffset? RunwayDate);
