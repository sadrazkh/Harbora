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
    /// One decimal place at most. The <c>#</c> is already optional, so a whole number formats
    /// without a trailing separator on its own — an explicit branch for that case was here, and was
    /// removed once mutation testing showed nothing could tell the difference.
    /// </summary>
    private static string Format(double value) =>
        value.ToString("0.#", CultureInfo.InvariantCulture);
}
