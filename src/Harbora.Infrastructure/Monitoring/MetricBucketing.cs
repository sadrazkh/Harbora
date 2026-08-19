namespace Harbora.Infrastructure.Monitoring;

/// <summary>
/// Turns raw, irregularly-spaced samples into a handful of evenly-spaced points a sparkline can draw.
///
/// <see cref="Harbora.Infrastructure.Design.SparklinePath"/> only knows how to space points by index,
/// not by the moment each one was actually taken — so a container that reported every few seconds for
/// the first half of the window and then went quiet would draw as a steady trend if the raw samples
/// were handed to it directly. Bucketing by time first is what keeps the shape honest.
///
/// A bucket with no sample inside it is left out of the result rather than filled with a guessed
/// value (zero, or the neighbour's value): a flat line where nothing was measured must not be drawn
/// the same way as a flat line where the app was genuinely idle the whole time.
/// </summary>
public static class MetricBucketing
{
    public static IReadOnlyList<double> Bucket(
        IEnumerable<(DateTimeOffset Timestamp, double Value)> points,
        DateTimeOffset since,
        DateTimeOffset until,
        int bucketCount)
    {
        if (bucketCount < 1 || until <= since) return [];

        var width = (until - since) / bucketCount;
        var sums = new double[bucketCount];
        var counts = new int[bucketCount];

        foreach (var (timestamp, value) in points)
        {
            if (timestamp < since || timestamp > until) continue;

            var index = (int)((timestamp - since).Ticks / width.Ticks);
            // The sample taken at the exact instant `until` lands one past the last bucket.
            index = Math.Clamp(index, 0, bucketCount - 1);

            sums[index] += value;
            counts[index]++;
        }

        var series = new List<double>(bucketCount);
        for (var i = 0; i < bucketCount; i++)
        {
            if (counts[i] > 0) series.Add(sums[i] / counts[i]);
        }

        return series;
    }
}
