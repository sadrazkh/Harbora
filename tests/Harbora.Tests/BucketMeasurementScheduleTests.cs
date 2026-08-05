using FluentAssertions;
using Harbora.Infrastructure.Storage;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Which buckets get measured on a tick.
///
/// Measuring runs a container, so the sweep cannot do all of them every time. What it must not do
/// instead is starve any of them: a bucket that never reaches the front of the queue has a usage
/// figure that is permanently unknown, which is the state automatic measurement exists to leave
/// behind.
/// </summary>
public class BucketMeasurementScheduleTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

    private static MeasurableBucket Bucket(string id, DateTimeOffset? measuredAt = null) =>
        new(Guid.Parse($"00000000-0000-0000-0000-00000000000{id}"), measuredAt);

    [Fact]
    public void A_bucket_nobody_has_measured_is_the_most_overdue_thing_there_is()
    {
        // The ordering trap. Sorting by the raw timestamp happens to put nulls first today, and
        // stops the moment somebody substitutes a default for the null — which reads like a tidy-up
        // and silently sends every never-measured bucket to the back of the queue.
        var due = BucketMeasurementSchedule.Due(
            [Bucket("1", Now.AddDays(-30)), Bucket("2"), Bucket("3", Now.AddDays(-20))], Now);

        due.First().Should().Be(Bucket("2").Id);
    }

    [Fact]
    public void The_oldest_measurement_goes_first()
    {
        var due = BucketMeasurementSchedule.Due(
            [Bucket("1", Now.AddDays(-2)), Bucket("2", Now.AddDays(-9)), Bucket("3", Now.AddDays(-5))], Now);

        due.Should().Equal(Bucket("2").Id, Bucket("3").Id, Bucket("1").Id);
    }

    [Fact]
    public void A_fresh_measurement_is_left_alone()
    {
        // The whole point of an interval. Re-measuring something measured a minute ago spends a
        // container to learn nothing.
        BucketMeasurementSchedule.Due([Bucket("1", Now.AddMinutes(-5))], Now).Should().BeEmpty();
    }

    [Fact]
    public void A_measurement_exactly_at_the_interval_is_due()
    {
        // Off-by-one on the boundary means a six-hour interval is really six hours plus one tick,
        // forever.
        var due = BucketMeasurementSchedule.Due(
            [Bucket("1", Now - BucketMeasurementSchedule.DefaultInterval)], Now);

        due.Should().ContainSingle();
    }

    [Fact]
    public void Only_a_batch_is_taken()
    {
        var many = Enumerable.Range(1, 9)
            .Select(i => new MeasurableBucket(Guid.NewGuid(), Now.AddDays(-i)))
            .ToList();

        BucketMeasurementSchedule.Due(many, Now, batch: 3).Should().HaveCount(3);
    }

    [Fact]
    public void What_is_skipped_this_tick_is_first_on_the_next_one()
    {
        // The property that makes the cap safe rather than a silent truncation: the ones left
        // behind are the oldest remaining, so a later pass reaches them. Without it a bucket beyond
        // the batch size is never measured at all.
        var buckets = Enumerable.Range(1, 6)
            .Select(i => new MeasurableBucket(Guid.NewGuid(), Now.AddDays(-i)))
            .ToList();

        var first = BucketMeasurementSchedule.Due(buckets, Now, batch: 2);

        // Whatever the first pass measured is now fresh; the rest are untouched and older.
        var afterwards = buckets
            .Select(b => first.Contains(b.Id) ? b with { MeasuredAt = Now } : b)
            .ToList();

        var second = BucketMeasurementSchedule.Due(afterwards, Now, batch: 2);

        second.Should().NotIntersectWith(first);
        second.Should().HaveCount(2);
    }

    [Fact]
    public void A_batch_of_nothing_measures_nothing_rather_than_everything()
    {
        // `Take(0)` and "no limit" are one keystroke apart, and the second starts a container per
        // bucket on every tick.
        BucketMeasurementSchedule.Due([Bucket("1"), Bucket("2")], Now, batch: 0).Should().BeEmpty();
        BucketMeasurementSchedule.Due([Bucket("1"), Bucket("2")], Now, batch: -1).Should().BeEmpty();
    }

    [Fact]
    public void Nothing_to_measure_is_nothing_to_do()
    {
        BucketMeasurementSchedule.Due([], Now).Should().BeEmpty();
    }

    [Fact]
    public void A_shorter_interval_makes_more_things_due()
    {
        var buckets = new[] { Bucket("1", Now.AddHours(-2)) };

        BucketMeasurementSchedule.Due(buckets, Now, TimeSpan.FromHours(6)).Should().BeEmpty();
        BucketMeasurementSchedule.Due(buckets, Now, TimeSpan.FromHours(1)).Should().ContainSingle();
    }

    [Fact]
    public void A_measurement_dated_in_the_future_is_not_treated_as_overdue()
    {
        // Clock skew between the panel and its database. Treating it as very old would put the
        // bucket at the front of every queue and measure it on every tick forever.
        BucketMeasurementSchedule.Due([Bucket("1", Now.AddHours(1))], Now).Should().BeEmpty();
    }
}
