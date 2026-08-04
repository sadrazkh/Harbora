using Harbora.Domain.Templates;

namespace Harbora.Infrastructure.Templates;

/// <summary>Why a version cannot be deployed, or null when it can.</summary>
public sealed record VersionRefusal(string Reason);

/// <summary>
/// Which versions a tenant may deploy, and which one is offered first.
///
/// Three separate failures are prevented here, and each of them looks like something else when it
/// happens:
///
/// <list type="bullet">
/// <item>an unpublished version reaching a customer — a draft is an operator's note to themselves,
/// not an offer;</item>
/// <item>an image without a digest being deployed — the tag it would resolve through is a moving
/// pointer, so the same "version" installs different software on different days;</item>
/// <item>an image built for another architecture — this fails deep inside the container runtime
/// with a message about exec formats that says nothing about architectures.</item>
/// </list>
/// </summary>
public static class VersionSelection
{
    /// <summary>
    /// The versions a tenant should see, best first. Draft and unsupported versions never appear:
    /// one is not ready to offer, the other must not be started again.
    /// </summary>
    public static IReadOnlyList<AppTemplateVersion> Offerable(
        IEnumerable<AppTemplateVersion> versions, string? nodeArchitecture = null)
    {
        return versions
            .Where(v => v.Publication == VersionPublication.Published)
            .Where(v => v.Lifecycle != VersionLifecycle.Unsupported)
            .Where(v => nodeArchitecture is null || RunsOn(v, nodeArchitecture))
            .OrderBy(v => v.Lifecycle)
            .ThenByDescending(v => v.ReleasedAt ?? DateTimeOffset.MinValue)
            .ToList();
    }

    /// <summary>
    /// The one to select by default. The recommended version when there is one, otherwise the best
    /// of what is left — never nothing, so long as anything is offerable.
    /// </summary>
    public static AppTemplateVersion? Default(
        IEnumerable<AppTemplateVersion> versions, string? nodeArchitecture = null) =>
        Offerable(versions, nodeArchitecture).FirstOrDefault();

    /// <summary>Whether this image was built for the architecture the node runs.</summary>
    public static bool RunsOn(AppTemplateVersion version, string nodeArchitecture)
    {
        if (string.IsNullOrWhiteSpace(nodeArchitecture)) return true;

        return version.SupportedArchitectures
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(a => string.Equals(a, nodeArchitecture.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Whether this version may be deployed onto this node, and why not when it may not.
    ///
    /// Checked again here rather than trusting the list the UI drew: the page was rendered a while
    /// ago, and a version can be withdrawn between a person opening the form and submitting it.
    /// </summary>
    public static VersionRefusal? Refuse(AppTemplateVersion version, string? nodeArchitecture)
    {
        if (version.Publication != VersionPublication.Published)
            return new VersionRefusal("That version has not been published yet.");

        if (version.Lifecycle == VersionLifecycle.Unsupported)
            return new VersionRefusal("That version is no longer supported and cannot be deployed.");

        // The whole point of pinning. Without a digest the deployment resolves whatever the tag
        // points at today, which is not the version anybody chose.
        if (string.IsNullOrWhiteSpace(version.ImageDigest))
            return new VersionRefusal(
                "That version has no pinned image digest, so what it installs cannot be guaranteed.");

        if (nodeArchitecture is not null && !RunsOn(version, nodeArchitecture))
            return new VersionRefusal(
                $"That version does not support {nodeArchitecture}. Supported: {version.SupportedArchitectures}.");

        return null;
    }

    /// <summary>
    /// The exact image reference to deploy: repository pinned to the digest.
    ///
    /// The tag is deliberately left out. <c>postgres:16@sha256:…</c> is legal and reads well, but it
    /// invites someone to "fix" the tag later and quietly change what runs; the digest alone cannot
    /// be edited into something else by accident.
    /// </summary>
    public static string? PinnedImage(AppTemplateVersion version) =>
        string.IsNullOrWhiteSpace(version.ImageDigest) || string.IsNullOrWhiteSpace(version.ImageRepository)
            ? null
            : $"{version.ImageRepository}@{version.ImageDigest}";
}
