namespace Harbora.Infrastructure.Templates;

/// <summary>
/// Turns what a host calls its architecture into what an image manifest calls it.
///
/// Docker reports the kernel's name — <c>x86_64</c>, <c>aarch64</c> — while image platforms and
/// therefore <see cref="Harbora.Domain.Templates.AppTemplateVersion.SupportedArchitectures"/> use
/// Go's names: <c>amd64</c>, <c>arm64</c>. Comparing the two directly matches nothing, and a
/// version-compatibility check that never matches is a check that refuses everything or, worse, is
/// quietly turned off to make the page work again.
/// </summary>
public static class HostArchitecture
{
    /// <summary>
    /// The normalised name, or null when the host did not say.
    ///
    /// An unrecognised value is passed through rather than dropped: a machine reporting something
    /// this list has not met yet still has an architecture, and a template that names the same
    /// string should still match it.
    /// </summary>
    public static string? Normalise(string? reported)
    {
        if (string.IsNullOrWhiteSpace(reported)) return null;

        var value = reported.Trim().ToLowerInvariant();
        return value switch
        {
            "x86_64" or "x86-64" or "amd64" => "amd64",
            "aarch64" or "arm64" or "arm64/v8" => "arm64",
            "armv7l" or "armv7" or "arm/v7" => "arm",
            "i386" or "i686" or "x86" => "386",
            _ => value
        };
    }
}
