namespace Harbora.Infrastructure.Tenancy;

/// <summary>
/// One way of writing a number of bytes.
///
/// It is a shared rule rather than a local helper because the same figure is written by the quota
/// refusal, the size picker and the detail page — and a plan that offers "40 GB" while the refusal
/// says "40960 MB" reads as two different limits.
/// </summary>
public static class ByteSize
{
    /// <summary>
    /// Bytes as a person reads them. Zero or less is unlimited, which is what a zero means on every
    /// limit in this platform.
    /// </summary>
    /// <summary>
    /// A measurement, where zero means the thing is empty.
    ///
    /// Separate from <see cref="Format"/> because the two answer different questions with the same
    /// number. A ceiling of zero means "no ceiling"; a measurement of zero means "nothing in it",
    /// and running both through one function made an empty bucket report its usage as "unlimited".
    /// </summary>
    public static string Measured(long bytes) => bytes <= 0 ? "0 B" : Format(bytes);

    public static string Format(long bytes, string unlimited = "unlimited") => bytes switch
    {
        <= 0 => unlimited,
        // Under a kilobyte, said in bytes. Nineteen bytes rounds to "0 KB", and a real measurement
        // that reads as nothing is the same lie as an unmeasured one shown as empty.
        < 1024 => $"{bytes} B",
        < 1024L * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB"
    };
}
