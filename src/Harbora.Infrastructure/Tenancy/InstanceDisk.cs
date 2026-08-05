namespace Harbora.Infrastructure.Tenancy;

/// <summary>
/// Whether what is already on disk fits in the tier being asked for.
///
/// The question a resize asks, and it is not the same question <see cref="DiskQuota"/> answers.
/// DiskQuota asks "may this workspace be given another resource", so being exactly at the limit is
/// a refusal. This asks "does what exists fit in this box", so being exactly at the limit is a yes
/// — moving an app that holds 20 GB onto a 20 GB tier changes nothing about it, and refusing that
/// would mean a tier could never hold what it advertises.
///
/// The measurement is the weak point and is treated as one: a volume nobody has measured is not
/// counted as empty, and a resize is not refused on the strength of a figure nobody collected
/// either. Both would be guesses, in opposite directions, about the same missing number.
/// </summary>
public static class InstanceDisk
{
    /// <summary>
    /// Whether an instance holding <paramref name="usage"/> may run on a tier offering
    /// <paramref name="tierDiskBytes"/>. A tier of zero has no ceiling.
    /// </summary>
    public static bool Fits(long tierDiskBytes, DiskUsage usage) =>
        tierDiskBytes <= 0 || usage.MeasuredBytes <= tierDiskBytes;

    /// <summary>
    /// Why it does not fit, naming both figures. Null when it does.
    ///
    /// "Too small" tells somebody nothing they can act on; the next question is always how much is
    /// there, and the answer has to come from the same place that made the decision.
    /// </summary>
    public static string? Explain(long tierDiskBytes, DiskUsage usage)
    {
        if (Fits(tierDiskBytes, usage)) return null;

        return $"This tier comes with {ByteSize.Format(tierDiskBytes)} of disk and " +
               $"{ByteSize.Format(usage.MeasuredBytes)} is already stored. " +
               "Delete some data first, or choose a larger tier.";
    }

    /// <summary>
    /// What the figure leaves out, for the screen that shows it. Null when nothing is missing.
    ///
    /// Kept separate from the refusal on purpose: an unmeasured volume is a reason to distrust the
    /// number, not a reason to refuse the resize.
    /// </summary>
    public static string? Caveat(DiskUsage usage) =>
        usage.UnmeasuredResources == 0
            ? null
            : $"{usage.UnmeasuredResources} volume(s) have never been measured, so the real figure is higher.";
}
