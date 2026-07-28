using Harbora.Application.Abstractions;
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
}
