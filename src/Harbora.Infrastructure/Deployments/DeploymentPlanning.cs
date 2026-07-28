using Harbora.Application.Abstractions;
using Harbora.Domain.Common;
using Harbora.Domain.Deployments;

namespace Harbora.Infrastructure.Deployments;

/// <summary>
/// Pure, unit-testable planning helpers for the deployment pipeline (P4 / ADR-006/007):
/// versioned container naming (so a new container can run alongside the old one for a
/// zero-downtime cutover), selecting which old containers to retire after cutover, a deterministic
/// per-deployment host port for remote-node overlap, and resolving the image to re-release on a
/// rollback (reuse the prior artifact — never rebuild).
/// </summary>
public static class DeploymentPlanning
{
    public const string AppLabel = "harbora.app";

    /// <summary>Versioned name so old + new can coexist during cutover: harbora-{slug}-{number}.</summary>
    public static string ContainerName(string slug, int number) => $"harbora-{slug}-{number}";

    /// <summary>The pre-P4 single-name convention; still retired as an "old" container on upgrade.</summary>
    public static string LegacyContainerName(string slug) => $"harbora-{slug}";

    /// <summary>
    /// This app's containers that are NOT the just-deployed one — removed only AFTER the new
    /// container is healthy and traffic has been switched. Matches by the harbora.app label so a
    /// legacy (unversioned) container is retired too.
    /// </summary>
    public static IReadOnlyList<string> ContainersToRetire(
        IEnumerable<ContainerInfo> all, string slug, string keepContainerName)
    {
        return all
            .Where(c => c.Labels.TryGetValue(AppLabel, out var s) && s == slug)
            .Where(c => c.Name != keepContainerName)
            .Select(c => c.Id)
            .ToList();
    }

    /// <summary>Pick this app's current serving container id: the running one, else any match.</summary>
    public static string? CurrentContainerId(IEnumerable<ContainerInfo> all, string slug)
    {
        var mine = all.Where(c => c.Labels.TryGetValue(AppLabel, out var s) && s == slug).ToList();
        return (mine.FirstOrDefault(c => c.State.Equals("running", StringComparison.OrdinalIgnoreCase))
                ?? mine.FirstOrDefault())?.Id;
    }

    /// <summary>
    /// Deterministic-but-per-deployment host port (20000–29999) for a remote node, so a new
    /// deployment can publish alongside the old one during cutover without a collision.
    /// </summary>
    public static int HostPort(string slug, int number)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{slug}#{number}"));
        return 20000 + (int)(BitConverter.ToUInt32(hash, 0) % 10000);
    }

    /// <summary>
    /// The image to re-release on a rollback: the target deployment's built image. Throws if the
    /// target has no retained image (so we never silently rebuild something different).
    /// </summary>
    public static string ResolveRollbackImage(Deployment? target)
    {
        if (target is null)
            throw new InvalidOperationException("The deployment to roll back to no longer exists.");
        if (string.IsNullOrWhiteSpace(target.ImageTag))
            throw new InvalidOperationException(
                $"Deployment #{target.Number} has no retained image to roll back to.");
        return target.ImageTag!;
    }

    /// <summary>
    /// Whether a rollback that just cut over should flag the deployment it displaced as
    /// <see cref="DeploymentStatus.RolledBack"/>. True only for a distinct deployment that is
    /// currently in a state the state machine allows to be superseded.
    /// </summary>
    public static bool ShouldMarkRolledBack(
        Deployment? superseded, Guid currentDeploymentId) =>
        superseded is not null &&
        superseded.Id != currentDeploymentId &&
        DeploymentStateMachine.CanTransition(superseded.Status, DeploymentStatus.RolledBack);

    // ---- image retention (Phase C) ----

    /// <summary>
    /// The tag prefix Harbora's own build images share for an app: <c>{prefix}/{slug}:build-</c>.
    /// Retention only ever considers tags matching this — a prebuilt or template image like
    /// <c>nginx:1.27</c> belongs to the user, not to us, and must never be pruned.
    /// </summary>
    public static string BuildImagePrefix(string imagePrefix, string slug) => $"{imagePrefix}/{slug}:build-";

    /// <summary>
    /// Which of this app's build images may be deleted after a successful cutover.
    ///
    /// Artifact rollback re-releases a stored image rather than rebuilding (ADR-006), so retention is
    /// what decides how far back "instant rollback" actually reaches. Kept: the active deployment's
    /// image, and the images of the <paramref name="keep"/> most recent rollback-eligible
    /// deployments (Succeeded or RolledBack — the only ones a user can roll back to). Everything
    /// else carrying our build-tag prefix is prunable.
    /// </summary>
    /// <param name="onNode">Images present on the node (already filtered to this app is fine).</param>
    /// <param name="deployments">This app's deployment history, any order.</param>
    /// <param name="activeDeploymentId">The deployment currently serving traffic.</param>
    /// <param name="imagePrefix">Registry/repository prefix, e.g. "harbora".</param>
    /// <param name="slug">App slug.</param>
    /// <param name="keep">How many rollback-eligible deployments to retain images for (min 1).</param>
    public static IReadOnlyList<string> ImagesToPrune(
        IEnumerable<ImageInfo> onNode,
        IEnumerable<Deployment> deployments,
        Guid? activeDeploymentId,
        string imagePrefix,
        string slug,
        int keep)
    {
        var prefix = BuildImagePrefix(imagePrefix, slug);
        var history = deployments.ToList();

        var protectedTags = new HashSet<string>(StringComparer.Ordinal);

        // Never prune what is serving traffic right now.
        var active = history.FirstOrDefault(d => d.Id == activeDeploymentId);
        if (!string.IsNullOrWhiteSpace(active?.ImageTag)) protectedTags.Add(active!.ImageTag!);

        // Keep the newest N rollback targets. A rollback re-releases an existing tag, so two
        // deployments can share one image — dedupe by tag, not by deployment, or a rollback would
        // silently shrink the retention window.
        var rollbackTargets = history
            .Where(d => d.Status is DeploymentStatus.Succeeded or DeploymentStatus.RolledBack)
            .Where(d => !string.IsNullOrWhiteSpace(d.ImageTag))
            .OrderByDescending(d => d.Number)
            .Select(d => d.ImageTag!)
            .Distinct(StringComparer.Ordinal)
            .Take(Math.Max(1, keep));

        foreach (var tag in rollbackTargets) protectedTags.Add(tag);

        return onNode
            .Select(i => i.Tag)
            .Where(t => t.StartsWith(prefix, StringComparison.Ordinal))
            .Where(t => !protectedTags.Contains(t))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }
}
