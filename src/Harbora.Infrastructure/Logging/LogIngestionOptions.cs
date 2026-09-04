namespace Harbora.Infrastructure.Logging;

/// <summary>
/// Disk budget for persisted log retention (2.2, 2026-09 log-retention plan) — this feature's own
/// "how much of the disk may you have", the same question <c>RetentionOptions</c> answers in days for
/// the platform's other append-only tables. Retention is expressed as an administrator-set day count
/// per app (<c>App.LogRetentionDays</c>); these two knobs are the operator-set outer bound that keeps
/// a verbose app — or many retention-enabled apps at once — from filling the disk before that day
/// count is ever reached. See <c>LogBudgetEnforcer</c> for how both are enforced and
/// <c>App.LogRetentionBudgetCapped</c> for how a budget-driven cut short is told apart from an app
/// simply not having produced that much history yet.
/// </summary>
public sealed class LogIngestionOptions
{
    public const string SectionName = "LogIngestion";

    /// <summary>
    /// How often the ingestion loop polls every retention-enabled app's current container. Short
    /// enough that an in-place crash-restart's last lines are almost always still in Docker's own log
    /// buffer by the next tick (see <c>ILogIngestionEngine</c>'s own doc on why that buffer usually
    /// survives a crash); long enough that this never competes meaningfully with a deploy for the
    /// engine's attention. Not exposed as a per-app setting — one shared cadence keeps the loop's own
    /// cost predictable regardless of how many apps opt in.
    /// </summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Bytes of persisted log text one app may hold before its own oldest lines start being dropped,
    /// regardless of how many days of its configured <c>LogRetentionDays</c> that represents. A cap
    /// per app, not only a shared one: without this, one very verbose app on a generous day count
    /// could consume the entire shared budget below and silently starve every other retention-enabled
    /// app in the workspace of any history at all. 50 MB is a few hundred thousand ordinary log lines
    /// — generous for the "what happened before it crashed" question this feature exists to answer,
    /// small next to what a single-server install's disk can typically spare.
    /// </summary>
    public long MaxBytesPerApp { get; set; } = 50 * 1024 * 1024;

    /// <summary>
    /// Bytes of persisted log text the platform will hold in total, across every app and every
    /// workspace, before the globally oldest lines start being dropped first — the backstop for "many
    /// apps each under their own cap can still add up to a full disk". 2 GB is deliberately
    /// conservative for a feature whose entire premise is "disk is finite and this is the feature most
    /// likely to fill it" — an operator running many retention-enabled apps on a large install should
    /// raise this deliberately, the same way <c>RetentionOptions.AuditLogDays</c> asks an operator
    /// with different obligations to choose 0 instead of inheriting a guess.
    /// </summary>
    public long MaxBytesTotal { get; set; } = 2L * 1024 * 1024 * 1024;

    /// <summary>
    /// The largest <c>App.LogRetentionDays</c> an administrator may set — a ceiling on the day count
    /// itself, independent of the byte caps above. Without one, a typo (36500 meant to be 365) would
    /// not merely waste disk the way <c>RetentionRule.CutoffFor</c>'s "too long to be a date" guard
    /// already handles for the platform's other tables; here it would also never trigger the byte
    /// budget's "cut short" signal in a way that reads as intentional, because nothing is technically
    /// wrong until the disk actually fills. 365 mirrors the audit log's own default.
    /// </summary>
    public int MaxRetentionDays { get; set; } = 365;

    /// <summary>
    /// How many lines a single ingest pass pulls per app. Bounds one poll tick's own cost; a burst
    /// larger than this is simply picked up across the next few ticks rather than in one.
    /// </summary>
    public int MaxLinesPerIngest { get; set; } = 2000;
}
