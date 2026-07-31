using Harbora.Domain.Backups;
using Harbora.Domain.Common;

namespace Harbora.Infrastructure.Backups;

/// <summary>
/// Which backup to check next, and when to check it again.
///
/// A backup nobody has verified is a backup nobody knows about, and nobody presses a "verify"
/// button on a Tuesday for fun — it gets pressed during an incident, which is far too late. So the
/// platform checks them on its own.
///
/// Verifying costs a real restore into a scratch database, so this is deliberately frugal: the
/// newest backup of each thing, one at a time, and never the same one twice in a period.
/// </summary>
public static class VerificationSchedule
{
    /// <summary>How long a verdict stays good enough. A week matches the usual retention.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(7);

    /// <summary>
    /// The one to check now, or null when nothing is due.
    ///
    /// Only the newest completed backup of each target is a candidate: verifying an artifact that
    /// will be pruned tomorrow spends a restore on a question nobody will ask. Never-verified comes
    /// before stale, because "unknown" is worse than "was fine a week ago".
    /// </summary>
    public static Backup? NextDue(IEnumerable<Backup> backups, DateTimeOffset now)
    {
        var newestPerTarget = backups
            .Where(b => b.Status == BackupStatus.Completed && b.ArtifactPath is not null)
            .GroupBy(b => (b.Type, b.TargetRef))
            .Select(g => g.OrderByDescending(b => b.FinishedAt ?? b.CreatedAt).First())
            .ToList();

        var neverChecked = newestPerTarget
            .Where(b => b.VerifiedAt is null)
            .OrderBy(b => b.FinishedAt ?? b.CreatedAt)
            .FirstOrDefault();
        if (neverChecked is not null) return neverChecked;

        return newestPerTarget
            .Where(b => b.VerifiedAt is { } verified && now - verified >= StaleAfter)
            .OrderBy(b => b.VerifiedAt)
            .FirstOrDefault();
    }

    /// <summary>
    /// Backups whose last verdict was "would not restore". Separated because this is the finding
    /// worth waking someone for, and it is easy to lose in a list of green ticks.
    /// </summary>
    public static IReadOnlyList<Backup> KnownBad(IEnumerable<Backup> backups) =>
        backups.Where(b => b.VerifiedRestorable == false).ToList();
}
