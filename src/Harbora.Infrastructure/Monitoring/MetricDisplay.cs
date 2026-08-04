using System.Globalization;

namespace Harbora.Infrastructure.Monitoring;

/// <summary>A metric ready to render, and whether there is anything to render at all.</summary>
/// <param name="HasData">False when nothing was ever measured. Not the same as measuring zero.</param>
/// <param name="Text">The formatted value, or empty when there is nothing to show.</param>
/// <param name="Series">The samples behind it, empty when there are none.</param>
public sealed record MetricView(bool HasData, string Text, IReadOnlyList<double> Series);

/// <summary>
/// The one place allowed to turn a measurement into something a person reads.
///
/// The distinction it exists to hold: <b>unknown is not zero</b>. A panel with no data behind it
/// must say so, because a zero, an em dash or a flat line all read as an observation. Harbora
/// currently collects four metrics and the design has room for forty, so most panels are in this
/// state — and will be until the collector catches up.
///
/// Formatting is invariant on purpose. The ambient culture here is usually Persian, whose digits
/// and decimal separator would make the same number look like two different measurements depending
/// on which page rendered it.
/// </summary>
public static class MetricDisplay
{
    /// <summary>A single measurement, or nothing.</summary>
    public static MetricView For(double? value, string unit = "") =>
        value is { } measured
            ? new MetricView(true, Format(measured) + unit, [])
            : new MetricView(false, string.Empty, []);

    /// <summary>
    /// A series and its headline, which is the latest sample rather than an average: the number
    /// above a sparkline answers "what is it now".
    /// </summary>
    public static MetricView ForSeries(IReadOnlyList<double>? series, string unit = "")
    {
        if (series is not { Count: > 0 }) return new MetricView(false, string.Empty, []);

        return new MetricView(true, Format(series[^1]) + unit, series);
    }

    /// <summary>
    /// A value that is a word rather than a measurement — a plan name, a formatted amount.
    ///
    /// Goes through the same gate so an absent value renders as "not collected" rather than as an
    /// empty box that reads like a rendering fault.
    /// </summary>
    public static MetricView ForText(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? new MetricView(false, string.Empty, [])
            : new MetricView(true, text.Trim(), []);

    /// <summary>
    /// A throughput, scaled to a unit a person reads without counting zeros.
    ///
    /// Goes through the same gate as everything else: null is "not collected yet", and stays that
    /// way. A rate is null when the counters could not be compared — most often because a container
    /// restarted — and rendering 0 B/s there would draw a flat line across an outage.
    /// </summary>
    public static MetricView ForThroughput(double? bytesPerSecond)
    {
        if (bytesPerSecond is not { } rate || rate < 0) return new MetricView(false, string.Empty, []);

        string[] units = ["B/s", "KB/s", "MB/s", "GB/s"];
        var unit = 0;
        while (rate >= 1024 && unit < units.Length - 1)
        {
            rate /= 1024;
            unit++;
        }

        return new MetricView(true, Format(rate) + " " + units[unit], []);
    }

    /// <summary>
    /// One decimal place at most. The <c>#</c> is already optional, so a whole number formats
    /// without a trailing separator on its own — an explicit branch for that case was here, and was
    /// removed once mutation testing showed nothing could tell the difference.
    /// </summary>
    private static string Format(double value) =>
        value.ToString("0.#", CultureInfo.InvariantCulture);
}
