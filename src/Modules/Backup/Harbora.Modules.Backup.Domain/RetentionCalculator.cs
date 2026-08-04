using System.Globalization;

namespace Harbora.Modules.Backup.Domain;

/// <summary>A snapshot considered for pruning.</summary>
public readonly record struct RetentionCandidate(Guid Id, DateTimeOffset CreatedAt);

/// <summary>
/// What retention decided, and why.
///
/// <para>
/// The reasons are not decoration. "Why is this snapshot still here?" and "why did that one
/// disappear?" are the two questions asked about retention, and a policy that cannot answer them is
/// one nobody trusts enough to leave enabled.
/// </para>
/// </summary>
public sealed record RetentionDecision(
    IReadOnlyList<Guid> Keep,
    IReadOnlyList<Guid> Prune,
    IReadOnlyDictionary<Guid, string> Reasons);

/// <summary>
/// Decides which snapshots survive a prune.
///
/// <para>
/// Tiers are independent and additive: a snapshot survives if ANY tier still wants it. Keeping 24
/// hourly and 30 daily therefore does not mean 54 snapshots, and certainly not 30 — it means the
/// last day is dense and the last month is sparse, with the same snapshot often satisfying both.
/// </para>
/// <para>
/// Pure and synchronous on purpose. Retention is the part of a backup system that deletes things,
/// so it is the part that most needs to be exercised directly in tests rather than only through a
/// repository that has to exist first.
/// </para>
/// </summary>
public static class RetentionCalculator
{
    /// <summary>
    /// Bucket boundaries are computed from the calendar properties of the local time, never from
    /// string formatting.
    ///
    /// <para>
    /// This codebase has already shipped a bug where the panel's Persian default culture wrote
    /// Jalali years into artifact filenames. <c>ToString("yyyy-MM-dd")</c> would reintroduce exactly
    /// that here, and the symptom would be daily buckets that disagree with the calendar the rest of
    /// the system uses. <see cref="DateTimeOffset.Year"/> and friends are always proleptic
    /// Gregorian, whatever the ambient culture is.
    /// </para>
    /// </summary>
    public static RetentionDecision Evaluate(
        IReadOnlyList<RetentionCandidate> candidates,
        RetentionPolicy policy,
        DateTimeOffset now,
        TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(timeZone);

        var ordered = candidates.OrderByDescending(c => c.CreatedAt).ToList();
        var reasons = new Dictionary<Guid, string>();

        // The floor, applied before anything else. Every tier set to zero would otherwise mean the
        // next prune empties the repository, and that is discovered at exactly the wrong moment.
        var keepLatest = Math.Max(0, policy.KeepLatest);
        foreach (var c in ordered.Take(keepLatest))
            reasons.TryAdd(c.Id, $"one of the {keepLatest} most recent");

        KeepTier(ordered, policy.KeepHourly, "hourly", reasons, timeZone,
            t => (t.Year, t.Month, t.Day, t.Hour, 0));

        KeepTier(ordered, policy.KeepDaily, "daily", reasons, timeZone,
            t => (t.Year, t.Month, t.Day, 0, 0));

        // ISOWeek is culture-independent; a locale-aware "week of year" would move the boundary
        // depending on which day the reader's culture calls the first of the week.
        KeepTier(ordered, policy.KeepWeekly, "weekly", reasons, timeZone,
            t => (ISOWeek.GetYear(t.Date), ISOWeek.GetWeekOfYear(t.Date), 0, 0, 0));

        KeepTier(ordered, policy.KeepMonthly, "monthly", reasons, timeZone,
            t => (t.Year, t.Month, 0, 0, 0));

        KeepTier(ordered, policy.KeepYearly, "yearly", reasons, timeZone,
            t => (t.Year, 0, 0, 0, 0));

        // The age ceiling drops anything older, but never touches the KeepLatest floor: an archive
        // that has not been backed up in a year should still have its last few snapshots when
        // someone finally looks, rather than having quietly emptied itself.
        if (policy.MaximumAgeDays is { } maxAgeDays && maxAgeDays > 0)
        {
            var cutoff = now - TimeSpan.FromDays(maxAgeDays);
            var floor = ordered.Take(keepLatest).Select(c => c.Id).ToHashSet();

            foreach (var c in ordered.Where(c => c.CreatedAt < cutoff && !floor.Contains(c.Id)))
                reasons.Remove(c.Id);
        }

        var keep = ordered.Where(c => reasons.ContainsKey(c.Id)).Select(c => c.Id).ToList();
        var prune = ordered.Where(c => !reasons.ContainsKey(c.Id)).Select(c => c.Id).ToList();

        return new RetentionDecision(keep, prune, reasons);
    }

    /// <summary>
    /// Keeps the newest snapshot in each of the most recent <paramref name="count"/> distinct
    /// periods.
    ///
    /// <para>
    /// Note "distinct periods that contain a snapshot", not "the last N calendar periods". A machine
    /// that was switched off for a week should not lose its daily history to seven empty buckets.
    /// </para>
    /// </summary>
    private static void KeepTier(
        IReadOnlyList<RetentionCandidate> orderedNewestFirst,
        int count,
        string tier,
        Dictionary<Guid, string> reasons,
        TimeZoneInfo timeZone,
        Func<DateTimeOffset, (int, int, int, int, int)> bucketOf)
    {
        if (count <= 0) return;

        var seen = new HashSet<(int, int, int, int, int)>();
        var kept = 0;

        foreach (var c in orderedNewestFirst)
        {
            if (kept >= count) break;

            var local = TimeZoneInfo.ConvertTime(c.CreatedAt, timeZone);
            if (!seen.Add(bucketOf(local))) continue;

            kept++;
            if (reasons.TryGetValue(c.Id, out var existing))
                reasons[c.Id] = $"{existing}, {tier} #{kept}";
            else
                reasons[c.Id] = $"{tier} #{kept}";
        }
    }
}
