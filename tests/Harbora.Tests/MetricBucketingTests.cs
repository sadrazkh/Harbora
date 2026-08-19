using FluentAssertions;
using Harbora.Infrastructure.Monitoring;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The Applications list's HEALTH · 1H sparkline (2026-08-19 apps-redesign) needs a handful of
/// evenly-spaced points from a raw, irregular stream of samples — <c>SparklinePath</c> only spaces by
/// index, not by the moment a sample was actually taken.
/// </summary>
public class MetricBucketingTests
{
    private static readonly DateTimeOffset Since = new(2026, 8, 19, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Until = Since.AddHours(1);

    [Fact]
    public void Two_samples_in_the_same_bucket_are_averaged_not_stacked()
    {
        var points = new[]
        {
            (Since.AddMinutes(1), 10.0),
            (Since.AddMinutes(2), 20.0),
        };

        var series = MetricBucketing.Bucket(points, Since, Until, bucketCount: 10);

        series.Should().Equal([15.0], "both samples land in the first six-minute bucket");
    }

    [Fact]
    public void A_bucket_nothing_landed_in_is_left_out_rather_than_filled_with_a_guess()
    {
        var points = new[]
        {
            (Since.AddMinutes(1), 5.0),
            // A six-minute gap: minutes 6-54 have no sample at all.
            (Since.AddMinutes(55), 9.0),
        };

        var series = MetricBucketing.Bucket(points, Since, Until, bucketCount: 10);

        series.Should().Equal([5.0, 9.0], "the eight empty buckets between them contribute nothing, not zeroes");
    }

    [Fact]
    public void Samples_spread_evenly_across_the_window_produce_one_point_per_bucket()
    {
        var points = Enumerable.Range(0, 10)
            .Select(i => (Since.AddMinutes(i * 6 + 1), (double)i))
            .ToArray();

        var series = MetricBucketing.Bucket(points, Since, Until, bucketCount: 10);

        series.Should().HaveCount(10, "one sample fell into each of the ten buckets");
    }

    [Fact]
    public void A_sample_exactly_at_the_end_of_the_window_lands_in_the_last_bucket_not_past_it()
    {
        var points = new[] { (Until, 42.0) };

        var series = MetricBucketing.Bucket(points, Since, Until, bucketCount: 10);

        series.Should().Equal([42.0], "the boundary sample must not be dropped or overflow the array");
    }

    [Fact]
    public void A_sample_outside_the_window_on_either_side_is_ignored()
    {
        var points = new[]
        {
            (Since.AddMinutes(-1), 999.0),
            (Until.AddMinutes(1), 999.0),
            (Since.AddMinutes(30), 7.0),
        };

        var series = MetricBucketing.Bucket(points, Since, Until, bucketCount: 10);

        series.Should().Equal([7.0], "only the sample that actually falls inside [since, until] counts");
    }

    [Fact]
    public void No_samples_at_all_produces_an_empty_series_not_a_flat_line()
    {
        var series = MetricBucketing.Bucket([], Since, Until, bucketCount: 10);

        series.Should().BeEmpty();
    }

    [Fact]
    public void An_empty_or_backwards_window_produces_no_buckets()
    {
        MetricBucketing.Bucket([(Since, 1.0)], Since, Since, bucketCount: 10).Should().BeEmpty();
        MetricBucketing.Bucket([(Since, 1.0)], Until, Since, bucketCount: 10).Should().BeEmpty();
    }
}
