namespace Harbora.Infrastructure.Services;

/// <summary>
/// Whether a database is running the version it was asked to run.
///
/// The failure this exists to make visible: a container recreated from a moving tag comes back on a
/// newer major version, refuses to start on the data directory it already has, and the panel goes on
/// showing the version that was originally chosen. The stored figure is what someone picked; the
/// image on the container is what is true.
/// </summary>
public static class VersionDrift
{
    /// <summary>
    /// True when the running image plainly does not match the configured version. Unknown is not
    /// drift — before a container exists there is nothing to disagree with.
    /// </summary>
    public static bool HasDrifted(string? configuredVersion, string? runningImage)
    {
        if (string.IsNullOrWhiteSpace(configuredVersion) || string.IsNullOrWhiteSpace(runningImage))
            return false;

        return !TagOf(runningImage).Equals(configuredVersion.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The tag part of an image reference. Handles a registry host carrying a port — the colon in
    /// <c>registry:5000/postgres:16</c> is not a tag separator.
    /// </summary>
    public static string TagOf(string imageReference)
    {
        var reference = imageReference.Trim();
        var lastColon = reference.LastIndexOf(':');
        var lastSlash = reference.LastIndexOf('/');

        return lastColon > lastSlash && lastColon >= 0 ? reference[(lastColon + 1)..] : "latest";
    }

    /// <summary>
    /// Whether a version keeps meaning the same thing. A tag like <c>16-alpine</c> stays on one major
    /// version; <c>latest</c> does not, and neither does a bare major on some images — those are the
    /// ones worth warning about before the data is written, rather than after.
    /// </summary>
    public static bool IsMoving(string? version) =>
        string.IsNullOrWhiteSpace(version)
        || version.Trim().Equals("latest", StringComparison.OrdinalIgnoreCase);
}
