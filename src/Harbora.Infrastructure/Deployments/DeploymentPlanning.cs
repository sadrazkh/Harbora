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
        IEnumerable<ContainerInfo> all, string slug, string keepContainerName) =>
        ContainersToRetire(all, slug, new[] { keepContainerName });

    /// <summary>
    /// Multi-container form. A Compose stack replaces several containers at once, so the cutover has
    /// to keep the whole new set — retiring per-service would tear down half the stack it just built.
    /// </summary>
    public static IReadOnlyList<string> ContainersToRetire(
        IEnumerable<ContainerInfo> all, string slug, IReadOnlyCollection<string> keepContainerNames)
    {
        return all
            .Where(c => c.Labels.TryGetValue(AppLabel, out var s) && s == slug)
            .Where(c => !keepContainerNames.Contains(c.Name))
            .Select(c => c.Id)
            .ToList();
    }

    /// <summary>Versioned name for one service of a Compose stack: harbora-{slug}-{service}-{number}.</summary>
    public static string ComposeContainerName(string slug, string service, int number) =>
        $"harbora-{slug}-{service}-{number}";

    /// <summary>Pick this app's current serving container id: the running one, else any match.</summary>
    public static string? CurrentContainerId(IEnumerable<ContainerInfo> all, string slug)
    {
        var mine = all.Where(c => c.Labels.TryGetValue(AppLabel, out var s) && s == slug).ToList();
        return (mine.FirstOrDefault(c => c.State.Equals("running", StringComparison.OrdinalIgnoreCase))
                ?? mine.FirstOrDefault())?.Id;
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
        var protectedTags = RetainedImageTags(deployments.ToList(), activeDeploymentId, keep);

        return onNode
            .Select(i => i.Tag)
            .Where(t => t.StartsWith(prefix, StringComparison.Ordinal))
            .Where(t => !protectedTags.Contains(t))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// The build-image tags <see cref="ImagesToPrune"/> refuses to delete: the active deployment's
    /// image, plus the newest <paramref name="keep"/> distinct rollback-eligible (Succeeded or
    /// RolledBack) tags. Pulled out of <see cref="ImagesToPrune"/> itself so a second question — which
    /// deployment rows the Deployments tab can mark as an instant rollback, see
    /// <see cref="RollbackEligibleDeploymentIds"/> — asks the pruner's own rule directly instead of a
    /// second <c>OrderByDescending(...).Take(n)</c> that could quietly drift from it.
    /// </summary>
    private static HashSet<string> RetainedImageTags(
        IReadOnlyCollection<Deployment> history, Guid? activeDeploymentId, int keep)
    {
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
        return protectedTags;
    }

    /// <summary>
    /// Which of this app's deployments can still be rolled back to without a rebuild — the same
    /// question <see cref="ImagesToPrune"/> answers about images sitting on a node, asked per
    /// deployment row instead so the Deployments tab can mark each one (doc
    /// 2026-08-15-rollback-depth-design, sub-project F). Derived, not stored: a second source of truth
    /// for "does this still have its image" would drift from the pruner, and the drift would show as a
    /// Rollback link that lies about whether it can work.
    /// </summary>
    /// <param name="deployments">This app's deployment history, any order.</param>
    /// <param name="activeDeploymentId">The deployment currently serving traffic.</param>
    /// <param name="keep">
    /// <see cref="HarboraRuntimeOptions.ImageRetentionCount"/>. <c>&lt;= 0</c> means retention is off
    /// ("0 disables pruning entirely" — both production callers of <see cref="ImagesToPrune"/> skip
    /// calling it in that case), so nothing is ever pruned and every deployment that still carries an
    /// image tag is an instant rollback.
    /// </param>
    public static IReadOnlySet<Guid> RollbackEligibleDeploymentIds(
        IEnumerable<Deployment> deployments, Guid? activeDeploymentId, int keep)
    {
        var history = deployments as IReadOnlyCollection<Deployment> ?? deployments.ToList();

        if (keep <= 0)
            return history
                .Where(d => !string.IsNullOrWhiteSpace(d.ImageTag))
                .Select(d => d.Id)
                .ToHashSet();

        var retainedTags = RetainedImageTags(history, activeDeploymentId, keep);
        return history
            .Where(d => !string.IsNullOrWhiteSpace(d.ImageTag) && retainedTags.Contains(d.ImageTag!))
            .Select(d => d.Id)
            .ToHashSet();
    }
}
