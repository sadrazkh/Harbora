using Harbora.Domain.Backups;

namespace Harbora.Infrastructure.Backups;

/// <summary>
/// Which state the recoverable window is in — never collapsed into a single "PITR on/off" flag,
/// because "configured", "actually applied" and "actually working right now" are three different
/// facts that must never look the same on a screen (the platform's own defining defect class).
/// </summary>
public enum PitrStatus
{
    /// <summary>Nobody asked for it.</summary>
    NotConfigured,

    /// <summary>Turned on, but the instance has not been rebuilt since — archiving has not started.
    /// <see cref="Harbora.Domain.Services.ManagedService.HasUnpublishedChanges"/>'s own state.</summary>
    PendingRestart,

    /// <summary>Configured and applied, but nothing has been archived yet, or no base backup has
    /// completed — there is nothing to replay onto, so no timestamp is recoverable yet.</summary>
    NotYetRecoverable,

    /// <summary>Archiving is up to date and there is a base backup underneath it.</summary>
    Healthy,

    /// <summary>Configured and was working, but the most recent attempt(s) failed, or nothing has
    /// shipped in longer than expected. The window still exists — it has simply stopped growing.</summary>
    Degraded
}

/// <summary>
/// The honest answer to "what is the most recent moment this instance could be restored to, right
/// now" (3.1, round-2 market-gaps plan) — computed fresh on every read rather than cached, and never
/// allowed to say more than the evidence supports.
///
/// <para>
/// <see cref="LatestPoint"/> is <see cref="WalArchivingStatus.LastSuccessAt"/>, never
/// <c>DateTimeOffset.UtcNow</c>. A green "PITR enabled" badge that quietly means "as of some earlier
/// successful run" is exactly the "recovery window that is wrong in the safe-looking direction"
/// failure this feature exists to refuse — a failing archive must shrink the reported window
/// (relative to the current time) rather than leave it looking current.
/// </para>
/// </summary>
public sealed record PitrWindow(
    PitrStatus Status,
    DateTimeOffset? EarliestPoint,
    DateTimeOffset? LatestPoint,
    TimeSpan? SinceLastSuccess,
    int ConsecutiveFailures,
    string? LastError,
    string Message)
{
    /// <summary>Whether a timestamp between <see cref="EarliestPoint"/> and <see cref="LatestPoint"/>
    /// can actually be offered to a restore form. False for every status except
    /// <see cref="PitrStatus.Healthy"/> and <see cref="PitrStatus.Degraded"/> — a degraded window is
    /// still a real, restorable window, just one that stopped advancing.</summary>
    public bool HasRecoverableWindow => Status is PitrStatus.Healthy or PitrStatus.Degraded;
}

public static class PitrRecoveryWindow
{
    /// <summary>
    /// How long archiving may go without a success before the window is reported as degraded even
    /// with zero consecutive failures recorded — covers a shipper that stopped running entirely
    /// (crashed, was never scheduled) rather than one that ran and failed. A generous multiple of
    /// <c>archive_timeout=300</c> (<see cref="PostgresWalArchivingCommand"/>), so one slow tick does
    /// not cry wolf.
    /// </summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(30);

    public static PitrWindow Compute(
        bool pitrEnabled,
        bool hasUnpublishedChanges,
        DateTimeOffset? oldestRetainedBaseBackupAt,
        WalArchivingStatus? archiving,
        DateTimeOffset now)
    {
        if (!pitrEnabled)
            return new PitrWindow(PitrStatus.NotConfigured, null, null, null, 0, null,
                "Point-in-time recovery is not turned on for this instance.");

        if (hasUnpublishedChanges)
            return new PitrWindow(PitrStatus.PendingRestart, null, null, null, 0, null,
                "Point-in-time recovery was turned on but has not taken effect yet — it needs this " +
                "instance's next rebuild before archiving starts.");

        if (oldestRetainedBaseBackupAt is null || archiving?.LastSuccessAt is null)
            return new PitrWindow(PitrStatus.NotYetRecoverable, null, null, null,
                archiving?.ConsecutiveFailures ?? 0, archiving?.LastError,
                oldestRetainedBaseBackupAt is null
                    ? "Archiving is configured, but no base backup has completed yet, so there is " +
                      "nothing to replay onto. Nothing is recoverable yet."
                    : "Archiving is configured, but no WAL segment has been shipped yet. Nothing is " +
                      "recoverable yet.");

        var latest = archiving.LastSuccessAt.Value;
        // Never later than the newest successfully archived point — a base backup taken after
        // archiving last succeeded (a stalled shipper, most likely) has nothing shipped to replay
        // forward from yet, so the window collapses to a single, honestly zero-width point rather
        // than claiming a range that has no WAL underneath its far end.
        var earliest = oldestRetainedBaseBackupAt.Value < latest ? oldestRetainedBaseBackupAt.Value : latest;

        var since = now - latest;
        var failing = archiving.ConsecutiveFailures > 0 || since > StaleAfter;

        if (!failing)
            return new PitrWindow(PitrStatus.Healthy, earliest, latest, since, 0, null,
                $"You can restore to any point between {Iso(earliest)} and {Iso(latest)}.");

        var reason = archiving.ConsecutiveFailures > 0
            ? $"Archiving has been failing for {Format(since)} ({archiving.ConsecutiveFailures} attempt(s) in a row)" +
              (archiving.LastError is { } err ? $": {err}" : ".")
            : $"Archiving has not shipped a segment in {Format(since)}, longer than expected.";

        return new PitrWindow(PitrStatus.Degraded, earliest, latest, since,
            archiving.ConsecutiveFailures, archiving.LastError,
            $"{reason} The most recent point you can restore to is stuck at {Iso(latest)}, not now.");
    }

    private static string Iso(DateTimeOffset value) => value.ToString("yyyy-MM-dd'T'HH:mm'Z'");

    /// <summary>A duration in the coarsest unit that keeps it honest — "6h" is what an operator needs
    /// to decide whether to page someone; "6h 12m 4s" is not read any faster for it.</summary>
    private static string Format(TimeSpan span)
    {
        if (span.TotalDays >= 1) return $"{(int)span.TotalDays}d {span.Hours}h";
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours}h {span.Minutes}m";
        if (span.TotalMinutes >= 1) return $"{(int)span.TotalMinutes}m";
        return $"{Math.Max(0, (int)span.TotalSeconds)}s";
    }
}
