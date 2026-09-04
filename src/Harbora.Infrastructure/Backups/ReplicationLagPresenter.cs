using Harbora.Domain.Services;

namespace Harbora.Infrastructure.Backups;

/// <summary>
/// What a replica's lag is honestly known to be, right now (3.2, round-2 market-gaps plan) — never
/// collapsed into a single number, because "never checked", "checked and it failed", "checked and
/// PostgreSQL itself does not know yet" and "checked and it is this many seconds" are four different
/// facts a panel must never show as the same green tick or the same bare zero. This is the platform's
/// defining defect class, restated for replication: a replica whose lag is unknown must say
/// <c>Unknown</c>, never <c>0</c>.
/// </summary>
public enum ReplicaLagStatus
{
    /// <summary>No check has ever completed for this replica — it was only just created, or the
    /// monitor has not ticked yet.</summary>
    NeverMeasured,

    /// <summary>The most recent check could not get an answer at all (unreachable, refused, the
    /// query itself failed), or the last successful answer is too old to still call current.</summary>
    Unknown,

    /// <summary>The most recent check succeeded, recently enough to trust, and named a real figure.</summary>
    Known
}

/// <param name="LagAge">
/// How old <paramref name="MeasuredAt"/> is as of the moment this was computed — carried separately
/// from <see cref="Lag"/> because a stale <see cref="ReplicaLagStatus.Unknown"/> reading still has a
/// last-known-good moment worth showing ("last measured 40 minutes ago"), even though the lag figure
/// itself is being withheld.
/// </param>
public sealed record ReplicaLagView(
    ReplicaLagStatus Status,
    TimeSpan? Lag,
    DateTimeOffset? MeasuredAt,
    TimeSpan? LagAge,
    int ConsecutiveFailures,
    string? LastError,
    string Message);

/// <summary>
/// Turns a <see cref="ReplicationLagStatus"/> row into the sentence a panel can honestly show — the
/// replication-lag counterpart of <see cref="PitrRecoveryWindow"/>, computed fresh on every read
/// rather than cached, for the identical reason: a cached "healthy" reading is exactly the "green dot
/// for a probe that never fired" failure this whole feature exists to refuse.
/// </summary>
public static class ReplicationLagPresenter
{
    /// <summary>
    /// How long a successful measurement stays trustworthy before it is presented as unknown rather
    /// than as a live figure quietly going stale. A generous multiple of <c>ReplicationLagMonitor</c>'s
    /// own tick interval, so one slow tick does not cry wolf — the same reasoning
    /// <c>PitrRecoveryWindow.StaleAfter</c> already gives its own, slower-ticking counterpart.
    /// </summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(10);

    public static ReplicaLagView Compute(ReplicationLagStatus? status, DateTimeOffset now)
    {
        if (status?.LastAttemptAt is null)
            return new ReplicaLagView(ReplicaLagStatus.NeverMeasured, null, null, null, 0, null,
                "Replication lag has not been measured yet.");

        if (status.LastSuccessAt is not { } lastSuccess)
            return new ReplicaLagView(ReplicaLagStatus.Unknown, null, null, null,
                status.ConsecutiveFailures, status.LastError,
                $"Replication lag is unknown: {status.LastError ?? "no successful measurement yet"}.");

        var age = now - lastSuccess;

        // Stale or currently failing: the last real figure is not presented as current. A number
        // read here from a measurement taken 40 minutes ago, shown as if it were live, is exactly the
        // failure this feature's single most important requirement exists to refuse.
        if (age > StaleAfter || status.ConsecutiveFailures > 0)
        {
            var why = status.ConsecutiveFailures > 0
                ? $"the last {status.ConsecutiveFailures} attempt(s) failed" +
                  (status.LastError is { } err ? $" ({err})" : "")
                : $"the last successful measurement was {Format(age)} ago";
            return new ReplicaLagView(ReplicaLagStatus.Unknown, null, lastSuccess, age,
                status.ConsecutiveFailures, status.LastError, $"Replication lag is unknown: {why}.");
        }

        if (status.LagSeconds is not { } seconds)
            // The query itself succeeded — PostgreSQL answered — but had no timestamp to give
            // (pg_last_xact_replay_timestamp() is NULL until the standby replays its first
            // commit-timestamped transaction). Still Unknown, never zero.
            return new ReplicaLagView(ReplicaLagStatus.Unknown, null, lastSuccess, age, 0, null,
                "Replication lag is unknown: this replica has not replayed a transaction with a " +
                "commit timestamp yet.");

        var lag = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return new ReplicaLagView(ReplicaLagStatus.Known, lag, lastSuccess, age, 0, null,
            lag < TimeSpan.FromSeconds(1)
                ? "Caught up with its primary."
                : $"{Format(lag)} behind its primary.");
    }

    /// <summary>A duration in the coarsest unit that keeps it honest — the same rule
    /// <c>PitrRecoveryWindow.Format</c> already states for its own duration text.</summary>
    private static string Format(TimeSpan span)
    {
        if (span.TotalDays >= 1) return $"{(int)span.TotalDays}d {span.Hours}h";
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours}h {span.Minutes}m";
        if (span.TotalMinutes >= 1) return $"{(int)span.TotalMinutes}m {span.Seconds}s";
        return $"{Math.Max(0, (int)span.TotalSeconds)}s";
    }
}
