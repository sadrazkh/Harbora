using FluentAssertions;
using Harbora.Modules.Backup.Domain;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Retention is the part of the backup system that deletes things, so it is the part most worth
/// pinning down. These tests are about the two questions an operator asks — "why is this still
/// here?" and "why did that one go?" — and about the configurations that would quietly empty a
/// repository.
/// </summary>
public class BackupRetentionTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    private static RetentionCandidate At(DateTimeOffset when) => new(Guid.CreateVersion7(), when);

    private static RetentionPolicy Policy(
        int latest = 0, int hourly = 0, int daily = 0, int weekly = 0,
        int monthly = 0, int yearly = 0, int? maxAgeDays = null) => new()
        {
            KeepLatest = latest,
            KeepHourly = hourly,
            KeepDaily = daily,
            KeepWeekly = weekly,
            KeepMonthly = monthly,
            KeepYearly = yearly,
            MaximumAgeDays = maxAgeDays
        };

    [Fact]
    public void Hourly_tier_keeps_the_newest_in_each_of_the_last_n_hours()
    {
        var snapshots = Enumerable.Range(0, 5)
            .Select(i => At(Now.AddHours(-i)))
            .ToList();

        var decision = RetentionCalculator.Evaluate(snapshots, Policy(hourly: 3), Now, Utc);

        decision.Keep.Should().HaveCount(3);
        decision.Keep.Should().BeEquivalentTo(snapshots.Take(3).Select(s => s.Id));
        decision.Prune.Should().BeEquivalentTo(snapshots.Skip(3).Select(s => s.Id));
    }

    [Fact]
    public void Daily_tier_keeps_one_per_day_not_one_per_snapshot()
    {
        // Two on the 10th, one each on the 9th and 8th.
        var newestToday = At(Now.AddHours(-2));
        var alsoToday = At(Now.AddHours(-10));
        var yesterday = At(Now.AddDays(-1));
        var dayBefore = At(Now.AddDays(-2));

        var decision = RetentionCalculator.Evaluate(
            [newestToday, alsoToday, yesterday, dayBefore], Policy(daily: 2), Now, Utc);

        decision.Keep.Should().BeEquivalentTo([newestToday.Id, yesterday.Id]);
        decision.Prune.Should().Contain(alsoToday.Id).And.Contain(dayBefore.Id);
    }

    [Fact]
    public void Tiers_are_additive_so_one_snapshot_can_satisfy_several()
    {
        var snapshots = Enumerable.Range(0, 3).Select(i => At(Now.AddHours(-i))).ToList();

        var decision = RetentionCalculator.Evaluate(snapshots, Policy(hourly: 2, daily: 1), Now, Utc);

        // The newest is both "hourly #1" and "daily #1"; the reason records both rather than
        // overwriting one with the other.
        decision.Reasons[snapshots[0].Id].Should().Contain("hourly").And.Contain("daily");
    }

    [Fact]
    public void KeepLatest_is_a_floor_that_survives_every_tier_being_zero()
    {
        var snapshots = Enumerable.Range(0, 4).Select(i => At(Now.AddDays(-i))).ToList();

        var decision = RetentionCalculator.Evaluate(snapshots, Policy(latest: 2), Now, Utc);

        decision.Keep.Should().BeEquivalentTo(snapshots.Take(2).Select(s => s.Id));
        decision.Reasons[snapshots[0].Id].Should().Contain("most recent");
    }

    /// <summary>
    /// The calculator is faithful to what it is told: a policy that keeps nothing prunes everything.
    /// Nothing here is a bug — it is the reason <see cref="BackupPolicyValidator"/> refuses to save
    /// such a policy in the first place, which the companion test asserts.
    /// </summary>
    [Fact]
    public void A_policy_that_keeps_nothing_prunes_everything()
    {
        var snapshots = Enumerable.Range(0, 3).Select(i => At(Now.AddDays(-i))).ToList();

        var decision = RetentionCalculator.Evaluate(snapshots, Policy(), Now, Utc);

        decision.Keep.Should().BeEmpty();
        decision.Prune.Should().HaveCount(3);
    }

    [Fact]
    public void Maximum_age_drops_old_snapshots()
    {
        var recent = At(Now.AddDays(-1));
        var ancient = At(Now.AddDays(-400));

        var decision = RetentionCalculator.Evaluate(
            [recent, ancient], Policy(latest: 1, daily: 10, maxAgeDays: 30), Now, Utc);

        decision.Keep.Should().BeEquivalentTo([recent.Id]);
        decision.Prune.Should().BeEquivalentTo([ancient.Id]);
    }

    /// <summary>
    /// A machine backed up once a year ago and never since should still have that snapshot when
    /// someone finally looks — not an empty repository because an age ceiling swept the only copy.
    /// </summary>
    [Fact]
    public void Maximum_age_never_deletes_below_the_KeepLatest_floor()
    {
        var onlyOne = At(Now.AddDays(-400));

        var decision = RetentionCalculator.Evaluate(
            [onlyOne], Policy(latest: 1, maxAgeDays: 30), Now, Utc);

        decision.Keep.Should().BeEquivalentTo([onlyOne.Id]);
        decision.Prune.Should().BeEmpty();
    }

    /// <summary>
    /// Day boundaries are the policy's, not the server's. Two snapshots either side of midnight UTC
    /// are one day apart in UTC and the same day in UTC-2, and retention must agree with whichever
    /// calendar the tenant set.
    /// </summary>
    [Fact]
    public void Daily_buckets_follow_the_policy_timezone()
    {
        var justAfterUtcMidnight = At(new DateTimeOffset(2026, 3, 11, 0, 30, 0, TimeSpan.Zero));
        var justBeforeUtcMidnight = At(new DateTimeOffset(2026, 3, 10, 23, 30, 0, TimeSpan.Zero));
        var now = new DateTimeOffset(2026, 3, 11, 1, 0, 0, TimeSpan.Zero);
        var minusTwo = TimeZoneInfo.CreateCustomTimeZone("t-2", TimeSpan.FromHours(-2), "t-2", "t-2");

        var inUtc = RetentionCalculator.Evaluate(
            [justAfterUtcMidnight, justBeforeUtcMidnight], Policy(daily: 5), now, Utc);
        var inMinusTwo = RetentionCalculator.Evaluate(
            [justAfterUtcMidnight, justBeforeUtcMidnight], Policy(daily: 5), now, minusTwo);

        inUtc.Keep.Should().HaveCount(2, "they fall on different days in UTC");
        inMinusTwo.Keep.Should().BeEquivalentTo([justAfterUtcMidnight.Id],
            "they fall on the same local day two hours behind UTC");
    }

    /// <summary>
    /// A gap in the history must not consume the tier. A laptop switched off for a week should come
    /// back to its previous dailies intact, not to seven empty buckets having spent the allowance.
    /// </summary>
    [Fact]
    public void A_gap_in_history_does_not_consume_the_tier()
    {
        var beforeTheGap = Enumerable.Range(0, 3).Select(i => At(Now.AddDays(-10 - i))).ToList();
        var afterTheGap = At(Now);

        var decision = RetentionCalculator.Evaluate(
            [afterTheGap, .. beforeTheGap], Policy(daily: 4), Now, Utc);

        decision.Keep.Should().HaveCount(4);
        decision.Keep.Should().Contain(beforeTheGap.Select(s => s.Id));
    }
}
