namespace Harbora.Infrastructure.Maintenance;

/// <summary>
/// How long each unbounded table keeps its rows.
///
/// <para>
/// Every value is a number of days, and <b>0 or less means keep forever</b> — as does a number too
/// large to be a date (roughly 739,000 and up, which is how far back the calendar goes). Both
/// readings are deliberate: an operator who blanks a setting, or a config file that loses a line,
/// must get the harmless answer rather than a cutoff of "now" that empties the table on the next
/// tick; and somebody reaching for a huge integer to mean "keep this a very long time" must get what
/// they meant rather than an exception. A table kept for either reason is named in the log every
/// sweep — with a <i>warning</i> in the too-large case, since nobody chooses that on purpose.
/// </para>
/// <para>
/// <see cref="Harbora.Domain.Common.IdempotencyRecord"/> has no knob here on purpose. It carries its
/// own <c>ExpiresAt</c>, written by the store that created it, and <c>IdempotencyStore.FindAsync</c>
/// already treats an expired row as absent — so a configured cutoff could only disagree with the
/// deadline the row itself was given, in one of two useless directions.
/// </para>
/// </summary>
public sealed class RetentionOptions
{
    public const string SectionName = "Retention";

    /// <summary>
    /// Persisted build output. The largest table by volume — every line of every build — and the
    /// reason this sweeper exists.
    ///
    /// <para>
    /// Ninety days rather than "the last N deployments per app": an age bound is what an operator
    /// can reason about ("we keep a quarter of build history"), it needs no per-app bookkeeping, and
    /// it degrades sensibly for an app that deploys twice a year — which a count-based rule would
    /// keep for ever. Volume is bounded by deploy rate × 90 days, which is a bound.
    /// </para>
    /// </summary>
    public int DeploymentLogDays { get; set; } = 90;

    /// <summary>
    /// The security trail.
    ///
    /// <para>
    /// <b>This default is a guess about your obligations, and it will be wrong for some installs.</b>
    /// A year is long enough that ordinary operation never notices it and short enough that the
    /// table stays bounded, but retention of audit evidence is a policy question, not a technical
    /// one. If you are subject to a retention requirement — SOC 2, PCI DSS, ISO 27001, a contract,
    /// or a national record-keeping rule — set this deliberately rather than inheriting 365.
    /// Set it to <c>0</c> to keep audit entries for ever; the table is low-volume compared with
    /// deployment logs, so that costs little.
    /// </para>
    /// </summary>
    public int AuditLogDays { get; set; } = 365;

    /// <summary>History of scheduled job runs — exit code and output tail.</summary>
    public int CronRunDays { get; set; } = 90;

    /// <summary>
    /// What the panel asked each node to do. The node detail page shows the last 30 and the admin
    /// API returns the last 25, so 90 days is already far more than anything reads.
    /// </summary>
    public int NodeCommandDays { get; set; } = 90;

    /// <summary>
    /// What nodes reported unprompted. The node detail page shows the last 40 and the admin API
    /// returns the last 50.
    /// </summary>
    public int NodeEventDays { get; set; } = 90;

    /// <summary>
    /// How long a dead password-reset token is kept after it was used or expired — never while it
    /// could still work. Kept at all only so "was this link already used?" stays answerable for a
    /// support conversation; a week covers that and nothing longer serves anyone.
    /// </summary>
    public int PasswordResetTokenDays { get; set; } = 7;

    /// <summary>
    /// The hour (UTC, 0–23) the nightly sweep runs at. Defaults to 03:00 UTC.
    ///
    /// <para>
    /// An hour rather than a period, because a period counted from start-up runs the sweep at
    /// whatever time the panel was last restarted — which is the one time of day nobody chose, and
    /// is as likely to be the busiest hour as the quietest. The first pass on an install that has
    /// been running for a year is the largest <c>DELETE</c> this platform will ever issue, so being
    /// able to put it somewhere quiet is worth one setting. Values outside 0–23 are clamped rather
    /// than rejected: a mistyped hour should cost a sweep at an odd time, not the sweeper.
    /// </para>
    /// </summary>
    public int SweepHourUtc { get; set; } = 3;
}
