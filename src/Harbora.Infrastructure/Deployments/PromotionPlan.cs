using Harbora.Domain.Common;

namespace Harbora.Infrastructure.Deployments;

/// <summary>The release being promoted.</summary>
/// <param name="ImageTag">The artifact itself. Null for a deployment that never produced one.</param>
public readonly record struct PromotionSource(
    DeploymentStatus Status, string? ImageTag, Guid AppId, Guid? ProjectId, Guid ServerId);

/// <summary>Where it is going.</summary>
public readonly record struct PromotionTarget(Guid AppId, Guid? ProjectId, Guid ServerId);

/// <summary>
/// Moving a release from one environment to the next.
///
/// The point of promoting rather than deploying again is that **it is the same artifact**. Building
/// twice from the same commit does not reliably produce the same image — a floating base tag, a
/// dependency published in between — so "we tested this in staging" only means something if the
/// bytes that go to production are the bytes that passed.
///
/// What is deliberately <b>not</b> carried across is configuration. The target keeps its own
/// variables, its own database, its own domains. Copying staging's environment into production is
/// the way this feature turns into an outage.
/// </summary>
public static class PromotionPlan
{
    /// <summary>
    /// Why this promotion cannot happen, or null when it can. Every branch is a thing that would
    /// otherwise fail later, in the middle of a deployment.
    /// </summary>
    public static string? Refuse(PromotionSource source, PromotionTarget target)
    {
        if (source.Status != DeploymentStatus.Succeeded)
            return "Only a deployment that succeeded can be promoted — this one did not finish.";

        if (string.IsNullOrWhiteSpace(source.ImageTag))
            return "This deployment produced no image, so there is nothing to promote.";

        if (source.AppId == target.AppId)
            return "That is the same service. Promotion moves a release to a different environment.";

        // Different projects means different applications that happen to share a platform. Promoting
        // between them is a mistake, not a feature.
        if (source.ProjectId is null || target.ProjectId is null || source.ProjectId != target.ProjectId)
            return "A release can only be promoted within the same project.";

        // A built image lives on the node that built it. Promoting to another node would fail at
        // pull time, halfway through a deployment, with an error about a missing image — worth
        // refusing here where it can be explained instead.
        if (source.ServerId != target.ServerId && IsLocallyBuilt(source.ImageTag!))
            return "This release was built on a different server, and a built image does not exist " +
                   "anywhere else. Deploy the target service from source instead.";

        return null;
    }

    /// <summary>
    /// True for an image Harbora built rather than pulled. Those exist only on the node that built
    /// them; anything else came from a registry and can be pulled anywhere.
    /// </summary>
    public static bool IsLocallyBuilt(string imageTag)
    {
        var colon = imageTag.LastIndexOf(':');
        if (colon < 0) return false;

        var tag = imageTag[(colon + 1)..];

        // The number is what makes it ours. A registry tag that merely starts with the word
        // "build-" belongs to somebody else and can be pulled anywhere.
        foreach (var prefix in (string[])["build-", "compose-"])
        {
            if (tag.StartsWith(prefix, StringComparison.Ordinal)
                && tag.Length > prefix.Length
                && tag[prefix.Length..].All(char.IsAsciiDigit))
                return true;
        }

        return false;
    }

    /// <summary>
    /// What to tell someone before they press it. Stating what does <i>not</i> travel is the
    /// important half: people expect a promotion to bring the settings with it.
    /// </summary>
    public static string Describe(string imageTag, string targetName) =>
        $"Releases exactly this image to {targetName}: {imageTag}. Nothing is rebuilt. " +
        $"{targetName} keeps its own variables, database and domains.";
}
