namespace Harbora.Infrastructure.Tenancy;

/// <summary>
/// How a resource tier reads in a picker.
///
/// It exists because the same line was built in four places — the application form, the database
/// form, the template deploy form and the platform settings — each with its own string, each
/// showing CPU and memory in megabytes and none of them mentioning disk. Four copies is four
/// chances for one picker to describe a tier differently from the picker beside it, and it is why
/// adding storage to a size would otherwise have meant remembering four edits.
/// </summary>
public static class InstanceSizeLabel
{
    /// <summary>
    /// "Small — 1 vCPU / 1 GB / 20 GB".
    ///
    /// Disk is left off entirely when the tier does not set one, rather than shown as "0 GB" or as
    /// "unlimited": a tier with no ceiling is the state every tier was in until now, and a picker
    /// that says "unlimited disk" on all five reads as a promise nobody made.
    /// </summary>
    public static string For(
        string name, double cpuCores, long memoryBytes, long diskBytes,
        long? runningRatePerHourMinor = null, string? currency = null)
    {
        var label = $"{name} — {cpuCores:0.##} vCPU / {ByteSize.Format(memoryBytes)}";

        if (diskBytes > 0) label += $" / {ByteSize.Format(diskBytes)}";
        if (runningRatePerHourMinor is { } rate)
            label += $" — {(rate / 100m).ToString("#,##0.00", System.Globalization.CultureInfo.InvariantCulture)} {currency ?? ""}/hour";
        else if (currency is not null)
            label += " — price not set";
        return label;
    }
}
