namespace Harbora.Infrastructure.Tenancy;

/// <summary>What is known about a workspace's disk use.</summary>
/// <param name="MeasuredBytes">The total of everything that has actually been measured.</param>
/// <param name="UnmeasuredResources">
/// How many volumes have never been measured. Reported rather than assumed to be empty: "not
/// measured" and "measured, and it is nothing" are different, and only one of them is a number.
/// </param>
public readonly record struct DiskUsage(long MeasuredBytes, int UnmeasuredResources);

/// <summary>
/// Whether a workspace has room on disk.
///
/// `MaxDiskBytes` sat on the plan, appeared on the pricing screen and was **checked nowhere** — a
/// limit customers could be sold and the platform never applied. This makes it real, and is honest
/// about what "real" can mean here: a Docker volume has no size of its own, so nothing can stop a
/// process writing. What can be done is measure what is there and refuse to hand out more room to a
/// workspace that is already over — which is what a quota does for capacity planning anyway.
///
/// The measurement is the weak point and is treated as such: a workspace whose volumes have never
/// been measured is not silently assumed to be using nothing.
/// </summary>
public static class DiskQuota
{
    /// <summary>
    /// Whether another resource may be created. A limit of 0 means unlimited, matching every other
    /// field on a plan.
    /// </summary>
    public static bool Allows(long limitBytes, DiskUsage usage) =>
        limitBytes <= 0 || usage.MeasuredBytes < limitBytes;

    /// <summary>
    /// Why it was refused, naming both figures — "quota exceeded" tells someone nothing they can
    /// act on, and the first question is always "how much am I using?".
    /// </summary>
    public static string Explain(long limitBytes, DiskUsage usage) =>
        $"This plan allows {Format(limitBytes)} of disk and {Format(usage.MeasuredBytes)} is already " +
        "in use. Delete something, or move to a larger plan." +
        (usage.UnmeasuredResources > 0
            ? $" ({usage.UnmeasuredResources} volume(s) have never been measured and are not included.)"
            : "");

    /// <summary>
    /// How complete the figure is, for the screen that shows it. A number presented as fact when
    /// half the volumes were never measured is the kind of thing people plan capacity against.
    /// </summary>
    public static string? Caveat(DiskUsage usage) =>
        usage.UnmeasuredResources == 0
            ? null
            : $"{usage.UnmeasuredResources} volume(s) have never been measured, so the real figure is higher.";

    private static string Format(long bytes) => bytes switch
    {
        <= 0 => "unlimited",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB"
    };
}
