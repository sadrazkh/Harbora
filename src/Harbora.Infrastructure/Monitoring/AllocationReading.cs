namespace Harbora.Infrastructure.Monitoring;

/// <summary>What a "used of allocated" figure is allowed to claim.</summary>
public enum AllocationKind
{
    /// <summary>Nothing has been measured. There is no share, and none may be drawn.</summary>
    Unmeasured = 0,

    /// <summary>Measured, but nothing was allocated — so there is no denominator to be a share of.</summary>
    Unlimited = 1,

    /// <summary>Both halves are known.</summary>
    Known = 2
}

/// <summary>
/// How full something is: a measurement over what it was given.
///
/// This existed three times, differently. The plan page drew a 5% bar whenever the limit was
/// unlimited — a permanently slightly-full bar under the word "∞". The application list printed a
/// memory sample with no denominator at all, so "512 MB" answered nothing: full or empty depends
/// entirely on whether the app was given 512 MB or 8 GB. And the details page had the honest
/// version written inline in Razor, where nothing could test it.
///
/// The two ways to lie here are both about a missing half. An unmeasured value drawn as an empty
/// bar reads as "idle", and an unlimited allocation drawn as any bar at all invents a ceiling that
/// does not exist. Each gets its own answer rather than a percentage.
/// </summary>
/// <param name="Kind">Which of the three cases this is.</param>
/// <param name="Percent">
/// The share, 0–100, and only meaningful when <see cref="AllocationKind.Known"/>. Clamped, so a bar
/// can be drawn from it directly.
/// </param>
/// <param name="IsOver">
/// The measurement exceeds the allocation. Real: a container resized downwards keeps running at its
/// old size until it is redeployed, and the sample taken in between is genuinely over the new limit.
/// </param>
public readonly record struct AllocationReading(AllocationKind Kind, int Percent, bool IsOver)
{
    /// <summary>Whether a bar may be drawn at all.</summary>
    public bool HasShare => Kind == AllocationKind.Known;

    /// <summary>
    /// Reads a measurement against an allocation. A limit of zero or less means unlimited, matching
    /// what a zero means everywhere else on a plan.
    /// </summary>
    public static AllocationReading Of(double? used, double allocated)
    {
        if (used is not { } measured || double.IsNaN(measured) || measured < 0)
            return new AllocationReading(AllocationKind.Unmeasured, 0, false);

        if (allocated <= 0 || double.IsNaN(allocated))
            return new AllocationReading(AllocationKind.Unlimited, 0, false);

        var share = measured / allocated * 100;

        // Rounded, then clamped: a reading of 99.6% of a limit is full enough to say 100, and a
        // sample taken before a shrink took effect must not produce a bar wider than its track.
        var percent = (int)Math.Round(Math.Clamp(share, 0, 100), MidpointRounding.AwayFromZero);

        return new AllocationReading(AllocationKind.Known, percent, measured > allocated);
    }

    /// <summary>The same reading for a counted resource — apps against an app limit.</summary>
    public static AllocationReading OfCount(int used, int allowed) => Of(used, allowed);
}
