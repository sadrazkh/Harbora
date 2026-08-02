using System.Globalization;
using System.Text;

namespace Harbora.Infrastructure.Design;

/// <summary>
/// The <c>d</c> attribute of a sparkline.
///
/// Returns null rather than a path whenever the samples cannot honestly describe a trend — no
/// samples, or only one. The caller renders the "not collected yet" state instead; a chart drawn
/// from nothing is read as a measurement, and it is read at a glance, which is precisely why a
/// wrong one is believed.
/// </summary>
public static class SparklinePath
{
    public static string? Build(IReadOnlyList<double> series, int width, int height)
    {
        if (series.Count < 2) return null;

        var min = series.Min();
        var max = series.Max();
        var span = max - min;

        var stepX = (double)width / (series.Count - 1);
        var path = new StringBuilder();

        for (var i = 0; i < series.Count; i++)
        {
            // A constant series has no span to scale by. Drawn along the floor it would read as an
            // outage, so it runs through the middle instead.
            var normalized = span == 0 ? 0.5 : (series[i] - min) / span;

            var x = i * stepX;

            // SVG's y axis grows downwards, so the largest sample has to become the smallest y.
            // Getting this backwards yields a chart that is precisely wrong rather than obviously
            // broken — the outage becomes the peak.
            var y = height - normalized * height;

            path.Append(i == 0 ? 'M' : 'L')
                .Append(Coordinate(x)).Append(' ').Append(Coordinate(y));
        }

        return path.ToString();
    }

    /// <summary>
    /// Invariant, always. A Persian decimal separator inside a path attribute does not throw — it
    /// produces a shape nobody drew.
    /// </summary>
    private static string Coordinate(double value) =>
        Math.Round(value, 2).ToString("0.##", CultureInfo.InvariantCulture);
}
